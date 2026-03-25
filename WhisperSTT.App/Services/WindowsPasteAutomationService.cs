using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.Services;

[SupportedOSPlatform("windows")]
internal sealed class WindowsPasteAutomationService : IPasteAutomationService
{
    private const int PasteRetryCount = 3;
    private static readonly TimeSpan PasteRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly string[] ShiftInsertWindowClasses =
    {
        "CASCADIA_HOSTING_WINDOW_CLASS",
        "ConsoleWindowClass",
        "VirtualConsoleClass",
        "mintty",
        "org.wezfurlong.wezterm"
    };
    private static readonly string[] BrowserWindowClasses =
    {
        "Chrome_WidgetWin_1",
        "Chrome_WidgetWin_0",
        "MozillaWindowClass",
        "ApplicationFrameWindow"
    };
    private readonly IActivityLogService? _activityLogService;

    public WindowsPasteAutomationService(IActivityLogService? activityLogService = null)
    {
        _activityLogService = activityLogService;
    }

    public PasteAutomationTarget CaptureTarget()
    {
        return new PasteAutomationTarget(NativeMethods.GetForegroundWindow());
    }

    public void PasteFromClipboard(PasteAutomationTarget target)
    {
        var targetDiagnostics = CapturePasteTargetDiagnostics(target);
        TryWriteDiagnosticLine($"Paste diagnostics: target={targetDiagnostics}.");
        var preferredShortcut = SelectPreferredShortcut(targetDiagnostics);

        string? lastShortcutDetail = null;
        string? lastPasteMessageDetail = null;
        for (var attempt = 1; attempt <= PasteRetryCount; attempt++)
        {
            RestoreForegroundWindow(targetDiagnostics.ForegroundWindow);

            if (TrySendPasteShortcut(preferredShortcut, out var shortcutDetail))
            {
                TryWriteDiagnosticLine(
                    $"Paste diagnostics: attempt {attempt}: {GetShortcutName(preferredShortcut)} SendInput dispatched.");
                return;
            }

            lastShortcutDetail = shortcutDetail;
            TryWriteDiagnosticLine(
                $"Paste diagnostics: attempt {attempt}: {GetShortcutName(preferredShortcut)} SendInput failed: {shortcutDetail}.");

            var supportsPasteMessage = SupportsPasteMessage(targetDiagnostics);
            string? currentPasteMessageDetail = null;
            if (supportsPasteMessage &&
                TrySendPasteMessage(targetDiagnostics.ForegroundWindow, out currentPasteMessageDetail))
            {
                TryWriteDiagnosticLine($"Paste diagnostics: attempt {attempt}: WM_PASTE fallback succeeded.");
                return;
            }

            lastPasteMessageDetail = supportsPasteMessage
                ? currentPasteMessageDetail
                : $"WM_PASTE skipped for browser-style window class '{targetDiagnostics.ForegroundClassName}'";
            TryWriteDiagnosticLine($"Paste diagnostics: attempt {attempt}: WM_PASTE fallback failed: {lastPasteMessageDetail}.");
            Thread.Sleep(PasteRetryDelay);
        }

        throw new InvalidOperationException(CreatePasteFailureMessage(targetDiagnostics, preferredShortcut, lastShortcutDetail, lastPasteMessageDetail));
    }

    private static bool TrySendPasteShortcut(PasteShortcut shortcut, out string detail)
    {
        NativeMethods.INPUT[] inputs = shortcut switch
        {
            PasteShortcut.CtrlV =>
            new[]
            {
                CreateKeyInput(NativeMethods.VkControl, keyUp: true),
                CreateKeyInput(NativeMethods.VkMenu, keyUp: true),
                CreateKeyInput(NativeMethods.VkShift, keyUp: true),
                CreateKeyInput(NativeMethods.VkControl, keyUp: false),
                CreateKeyInput(NativeMethods.VkV, keyUp: false),
                CreateKeyInput(NativeMethods.VkV, keyUp: true),
                CreateKeyInput(NativeMethods.VkControl, keyUp: true)
            },
            PasteShortcut.ShiftInsert =>
            new[]
            {
                CreateKeyInput(NativeMethods.VkControl, keyUp: true),
                CreateKeyInput(NativeMethods.VkMenu, keyUp: true),
                CreateKeyInput(NativeMethods.VkShift, keyUp: true),
                CreateKeyInput(NativeMethods.VkShift, keyUp: false),
                CreateKeyInput(NativeMethods.VkInsert, keyUp: false, extendedKey: true),
                CreateKeyInput(NativeMethods.VkInsert, keyUp: true, extendedKey: true),
                CreateKeyInput(NativeMethods.VkShift, keyUp: true)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(shortcut), shortcut, null)
        };

        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent == inputs.Length)
        {
            detail = $"{GetShortcutName(shortcut)} sent {sent}/{inputs.Length} inputs";
            return true;
        }

        var errorCode = Marshal.GetLastWin32Error();
        if (errorCode == 0)
        {
            detail = $"{GetShortcutName(shortcut)} SendInput returned {sent}/{inputs.Length} without a Win32 error";
            return false;
        }

        detail = $"{GetShortcutName(shortcut)} Win32 {errorCode}: {new Win32Exception(errorCode).Message}";
        return false;
    }

    private static bool TrySendPasteMessage(IntPtr foregroundWindow, out string detail)
    {
        if (foregroundWindow == IntPtr.Zero)
        {
            detail = "no foreground window";
            return false;
        }

        var failures = new List<string>();
        foreach (var targetWindow in EnumeratePasteTargets(foregroundWindow))
        {
            var sendResult = NativeMethods.SendMessageTimeout(
                targetWindow,
                NativeMethods.WmPaste,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.SmtoAbortIfHung,
                500,
                out _);

            if (sendResult != IntPtr.Zero)
            {
                detail = $"WM_PASTE accepted by {DescribeWindow(targetWindow)}";
                return true;
            }

            var errorCode = Marshal.GetLastWin32Error();
            failures.Add(errorCode == 0
                ? $"{DescribeWindow(targetWindow)} rejected or ignored WM_PASTE"
                : $"{DescribeWindow(targetWindow)} failed with Win32 {errorCode}: {new Win32Exception(errorCode).Message}");
        }

        detail = failures.Count > 0
            ? string.Join("; ", failures)
            : "no paste targets accepted WM_PASTE";
        return false;
    }

    private static string CreatePasteFailureMessage(
        PasteTargetDiagnostics diagnostics,
        PasteShortcut shortcut,
        string? shortcutDetail,
        string? pasteMessageDetail)
    {
        if (diagnostics.ForegroundWindow == IntPtr.Zero)
        {
            return "Automatic paste failed. The transcript remains on the clipboard because no active target window was detected. Use the Copy button or paste manually.";
        }

        if (diagnostics.IsElevated)
        {
            return "Automatic paste failed. The transcript remains on the clipboard because the focused target window is running as administrator. Start WhisperSTT as administrator to paste there automatically, or paste manually.";
        }

        if (!string.IsNullOrWhiteSpace(shortcutDetail) || !string.IsNullOrWhiteSpace(pasteMessageDetail))
        {
            return $"Automatic paste failed. The transcript remains on the clipboard because the target application rejected both simulated {GetShortcutName(shortcut)} input and WM_PASTE. Use the Copy button or paste manually.";
        }

        return "Automatic paste failed. The transcript remains on the clipboard. Use the Copy button or paste manually.";
    }

    private static PasteShortcut SelectPreferredShortcut(PasteTargetDiagnostics diagnostics)
    {
        if (UsesShiftInsertPaste(diagnostics.FocusedClassName) ||
            UsesShiftInsertPaste(diagnostics.ForegroundClassName))
        {
            return PasteShortcut.ShiftInsert;
        }

        return PasteShortcut.CtrlV;
    }

    private static bool UsesShiftInsertPaste(string className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        return ShiftInsertWindowClasses.Any(candidate => string.Equals(candidate, className, StringComparison.Ordinal));
    }

    private static string GetShortcutName(PasteShortcut shortcut)
    {
        return shortcut switch
        {
            PasteShortcut.CtrlV => "Ctrl+V",
            PasteShortcut.ShiftInsert => "Shift+Insert",
            _ => throw new ArgumentOutOfRangeException(nameof(shortcut), shortcut, null)
        };
    }

    private static bool SupportsPasteMessage(PasteTargetDiagnostics diagnostics)
    {
        return !IsBrowserStyleWindow(diagnostics.ForegroundClassName) &&
               !IsBrowserStyleWindow(diagnostics.FocusedClassName);
    }

    private static bool IsBrowserStyleWindow(string className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        return BrowserWindowClasses.Any(candidate => string.Equals(candidate, className, StringComparison.Ordinal));
    }

    private static bool TryIsWindowElevated(IntPtr windowHandle, out bool isElevated)
    {
        isElevated = false;
        _ = NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0)
        {
            return false;
        }

        var processHandle = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation,
            bInheritHandle: false,
            processId);
        if (processHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!NativeMethods.OpenProcessToken(processHandle, NativeMethods.TokenQuery, out var tokenHandle))
            {
                return false;
            }

            try
            {
                var size = Marshal.SizeOf<NativeMethods.TOKEN_ELEVATION>();
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!NativeMethods.GetTokenInformation(
                            tokenHandle,
                            NativeMethods.TokenElevation,
                            buffer,
                            size,
                            out _))
                    {
                        return false;
                    }

                    var elevation = Marshal.PtrToStructure<NativeMethods.TOKEN_ELEVATION>(buffer);
                    isElevated = elevation.TokenIsElevated != 0;
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                _ = NativeMethods.CloseHandle(tokenHandle);
            }
        }
        finally
        {
            _ = NativeMethods.CloseHandle(processHandle);
        }
    }

    private static IEnumerable<IntPtr> EnumeratePasteTargets(IntPtr foregroundWindow)
    {
        var seen = new HashSet<IntPtr>();

        if (TryGetFocusedWindow(foregroundWindow, out var focusedWindow) &&
            focusedWindow != IntPtr.Zero &&
            seen.Add(focusedWindow))
        {
            yield return focusedWindow;
        }

        if (seen.Add(foregroundWindow))
        {
            yield return foregroundWindow;
        }
    }

    private static PasteTargetDiagnostics CapturePasteTargetDiagnostics(PasteAutomationTarget target)
    {
        var currentForegroundWindow = NativeMethods.GetForegroundWindow();
        var foregroundWindow = target.ForegroundWindow != IntPtr.Zero
            ? target.ForegroundWindow
            : currentForegroundWindow;
        var focusedWindow = IntPtr.Zero;
        _ = TryGetFocusedWindow(foregroundWindow, out focusedWindow);
        var couldDetermineElevation = TryIsWindowElevated(foregroundWindow, out var isElevated);

        return new PasteTargetDiagnostics(
            foregroundWindow,
            currentForegroundWindow,
            focusedWindow,
            GetWindowClassName(foregroundWindow),
            GetWindowClassName(focusedWindow),
            DescribeWindow(foregroundWindow),
            DescribeWindow(currentForegroundWindow),
            DescribeWindow(focusedWindow),
            couldDetermineElevation,
            isElevated);
    }

    private static void RestoreForegroundWindow(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero)
        {
            return;
        }

        var currentForegroundWindow = NativeMethods.GetForegroundWindow();
        if (currentForegroundWindow == targetWindow)
        {
            return;
        }

        if (!NativeMethods.SetForegroundWindow(targetWindow))
        {
            return;
        }

        Thread.Sleep(25);
    }

    private static bool TryGetFocusedWindow(IntPtr foregroundWindow, out IntPtr focusedWindow)
    {
        focusedWindow = IntPtr.Zero;
        var foregroundThreadId = NativeMethods.GetWindowThreadProcessId(foregroundWindow, out _);
        if (foregroundThreadId == 0)
        {
            return false;
        }

        var guiThreadInfo = new NativeMethods.GUITHREADINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };

        if (!NativeMethods.GetGUIThreadInfo(foregroundThreadId, ref guiThreadInfo))
        {
            return false;
        }

        focusedWindow = guiThreadInfo.hwndFocus;
        return focusedWindow != IntPtr.Zero;
    }

    private static NativeMethods.INPUT CreateKeyInput(ushort virtualKey, bool keyUp, bool extendedKey = false)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.InputKeyboard,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = (keyUp ? NativeMethods.KeyEventFKeyUp : 0) |
                              (extendedKey ? NativeMethods.KeyEventFExtendedKey : 0)
                }
            }
        };
    }

    private static string DescribeWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return "0x0";
        }

        _ = NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        var className = GetWindowClassName(windowHandle);
        var windowText = GetWindowText(windowHandle);
        return $"0x{windowHandle.ToInt64():X} pid={processId} class='{className}' title='{windowText}'";
    }

    private static string GetWindowClassName(IntPtr windowHandle)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetClassName(windowHandle, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "<unknown>";
    }

    private static string GetWindowText(IntPtr windowHandle)
    {
        var textLength = NativeMethods.GetWindowTextLength(windowHandle);
        if (textLength <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[textLength + 1];
        var copiedLength = NativeMethods.GetWindowText(windowHandle, buffer, buffer.Length);
        return copiedLength > 0 ? new string(buffer, 0, copiedLength) : string.Empty;
    }

    private void TryWriteDiagnosticLine(string message)
    {
        if (_activityLogService is null)
        {
            return;
        }

        _ = WriteDiagnosticLineAsync(message);
    }

    private async Task WriteDiagnosticLineAsync(string message)
    {
        try
        {
            await _activityLogService!.WriteAsync(message).ConfigureAwait(false);
        }
        catch
        {
            // Paste diagnostics must never crash the app.
        }
    }

    private readonly record struct PasteTargetDiagnostics(
        IntPtr ForegroundWindow,
        IntPtr CurrentForegroundWindow,
        IntPtr FocusedWindow,
        string ForegroundClassName,
        string FocusedClassName,
        string ForegroundDescription,
        string CurrentForegroundDescription,
        string FocusedDescription,
        bool CouldDetermineElevation,
        bool IsElevated)
    {
        public override string ToString()
        {
            var elevationText = CouldDetermineElevation
                ? (IsElevated ? "elevated" : "not elevated")
                : "elevation unknown";
            return $"foreground={ForegroundDescription}; currentForeground={CurrentForegroundDescription}; focus={FocusedDescription}; {elevationText}";
        }
    }

    private enum PasteShortcut
    {
        CtrlV,
        ShiftInsert
    }
}
