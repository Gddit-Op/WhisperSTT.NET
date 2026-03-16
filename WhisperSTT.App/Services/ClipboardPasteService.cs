using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.Services;

public sealed class ClipboardPasteService : IPasteService
{
    private const int ClipboardRetryCount = 8;
    private const int PasteRetryCount = 3;
    private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan PasteRetryDelay = TimeSpan.FromMilliseconds(50);
    private readonly IActivityLogService? _activityLogService;

    public ClipboardPasteService(IActivityLogService? activityLogService = null)
    {
        _activityLogService = activityLogService;
    }

    public async Task CopyTextToClipboardAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await RunClipboardActionAsync(
            clipboard => clipboard.SetTextAsync(text),
            cancellationToken,
            throwOnFailure: true).ConfigureAwait(true);
    }

    public async Task PasteTextAsync(string text, bool restoreClipboard, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        IAsyncDataTransfer? snapshot = null;
        if (restoreClipboard)
        {
            snapshot = await TryGetClipboardSnapshotAsync(cancellationToken).ConfigureAwait(true);
        }

        try
        {
            await RunClipboardActionAsync(
                clipboard => clipboard.SetTextAsync(text),
                cancellationToken,
                throwOnFailure: true).ConfigureAwait(true);

            SendPasteShortcutWithFallback();
            await Task.Delay(100, cancellationToken).ConfigureAwait(true);

            if (!restoreClipboard)
            {
                return;
            }

            await RunClipboardActionAsync(
                snapshot is null
                    ? clipboard => clipboard.ClearAsync()
                    : clipboard => clipboard.SetDataAsync(snapshot),
                cancellationToken,
                throwOnFailure: false).ConfigureAwait(true);
        }
        finally
        {
            if (snapshot is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static async Task<IAsyncDataTransfer?> TryGetClipboardSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RunClipboardFunctionAsync(
                clipboard => clipboard.TryGetDataAsync(),
                cancellationToken,
                throwOnFailure: false).ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
    }

    private static async Task RunClipboardActionAsync(
        Func<IClipboard, Task> action,
        CancellationToken cancellationToken,
        bool throwOnFailure)
    {
        _ = await RunClipboardFunctionAsync(
            async clipboard =>
            {
                await action(clipboard).ConfigureAwait(true);
                return true;
            },
            cancellationToken,
            throwOnFailure).ConfigureAwait(true);
    }

    private static async Task<T?> RunClipboardFunctionAsync<T>(
        Func<IClipboard, Task<T>> action,
        CancellationToken cancellationToken,
        bool throwOnFailure)
    {
        for (var attempt = 1; attempt <= ClipboardRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var clipboard = GetClipboard();
                return await action(clipboard).ConfigureAwait(true);
            }
            catch (Exception exception) when (IsTransientClipboardException(exception))
            {
                if (attempt == ClipboardRetryCount)
                {
                    if (!throwOnFailure)
                    {
                        return default;
                    }

                    throw new InvalidOperationException(
                        "Clipboard is busy. Close clipboard managers or other apps using the clipboard and try again.",
                        exception);
                }

                await Task.Delay(ClipboardRetryDelay, cancellationToken).ConfigureAwait(true);
            }
        }

        return default;
    }

    private static IClipboard GetClipboard()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window } &&
            window.Clipboard is { } clipboard)
        {
            return clipboard;
        }

        throw new InvalidOperationException("Clipboard is not available because the main window is not initialized.");
    }

    private static bool IsTransientClipboardException(Exception exception)
    {
        return exception is COMException or ExternalException or InvalidOperationException;
    }

    private void SendPasteShortcutWithFallback()
    {
        var targetDiagnostics = CapturePasteTargetDiagnostics();
        TryWriteDiagnosticLine($"Paste diagnostics: target={targetDiagnostics}.");

        string? lastSendInputDetail = null;
        string? lastPasteMessageDetail = null;
        for (var attempt = 1; attempt <= PasteRetryCount; attempt++)
        {
            if (TrySendPasteShortcut(out var sendInputDetail))
            {
                TryWriteDiagnosticLine($"Paste diagnostics: attempt {attempt}: SendInput succeeded.");
                return;
            }

            lastSendInputDetail = sendInputDetail;
            TryWriteDiagnosticLine($"Paste diagnostics: attempt {attempt}: SendInput failed: {sendInputDetail}.");

            if (TrySendPasteMessage(targetDiagnostics.ForegroundWindow, out var pasteMessageDetail))
            {
                TryWriteDiagnosticLine($"Paste diagnostics: attempt {attempt}: WM_PASTE fallback succeeded.");
                return;
            }

            lastPasteMessageDetail = pasteMessageDetail;
            TryWriteDiagnosticLine($"Paste diagnostics: attempt {attempt}: WM_PASTE fallback failed: {pasteMessageDetail}.");
            Thread.Sleep(PasteRetryDelay);
        }

        throw new InvalidOperationException(CreatePasteFailureMessage(targetDiagnostics, lastSendInputDetail, lastPasteMessageDetail));
    }

    private static bool TrySendPasteShortcut(out string detail)
    {
        var inputs = new[]
        {
            CreateKeyInput(NativeMethods.VkControl, keyUp: false),
            CreateKeyInput(NativeMethods.VkV, keyUp: false),
            CreateKeyInput(NativeMethods.VkV, keyUp: true),
            CreateKeyInput(NativeMethods.VkControl, keyUp: true)
        };

        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent == inputs.Length)
        {
            detail = $"sent {sent}/{inputs.Length} inputs";
            return true;
        }

        var errorCode = Marshal.GetLastWin32Error();
        if (errorCode == 0)
        {
            detail = $"SendInput returned {sent}/{inputs.Length} without a Win32 error";
            return false;
        }

        detail = $"Win32 {errorCode}: {new Win32Exception(errorCode).Message}";
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
        string? sendInputDetail,
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

        if (!string.IsNullOrWhiteSpace(sendInputDetail) || !string.IsNullOrWhiteSpace(pasteMessageDetail))
        {
            return "Automatic paste failed. The transcript remains on the clipboard because the target application rejected both simulated Ctrl+V input and WM_PASTE. Use the Copy button or paste manually.";
        }

        return "Automatic paste failed. The transcript remains on the clipboard. Use the Copy button or paste manually.";
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

    private static PasteTargetDiagnostics CapturePasteTargetDiagnostics()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        var focusedWindow = IntPtr.Zero;
        _ = TryGetFocusedWindow(foregroundWindow, out focusedWindow);
        var couldDetermineElevation = TryIsWindowElevated(foregroundWindow, out var isElevated);

        return new PasteTargetDiagnostics(
            foregroundWindow,
            focusedWindow,
            DescribeWindow(foregroundWindow),
            DescribeWindow(focusedWindow),
            couldDetermineElevation,
            isElevated);
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

    private static NativeMethods.INPUT CreateKeyInput(ushort virtualKey, bool keyUp)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.InputKeyboard,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = keyUp ? NativeMethods.KeyEventFKeyUp : 0
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
        IntPtr FocusedWindow,
        string ForegroundDescription,
        string FocusedDescription,
        bool CouldDetermineElevation,
        bool IsElevated)
    {
        public override string ToString()
        {
            var elevationText = CouldDetermineElevation
                ? (IsElevated ? "elevated" : "not elevated")
                : "elevation unknown";
            return $"foreground={ForegroundDescription}; focus={FocusedDescription}; {elevationText}";
        }
    }
}
