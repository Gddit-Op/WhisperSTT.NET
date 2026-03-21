using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WhisperSTT.Core.Models;

namespace WhisperSTT.App.Services;

[SupportedOSPlatform("windows")]
public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private readonly IntPtr _windowHandle;
    private readonly Dictionary<int, string> _actionsById = new();
    private readonly WindowProc _windowProc;
    private IntPtr _previousWindowProc;

    public GlobalHotkeyService(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _windowProc = WndProc;
        _previousWindowProc = NativeMethods.SetWindowLongPtr(
            _windowHandle,
            NativeMethods.GwlpWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowProc));
    }

    public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

    public void ApplySettings(HotkeySettings settings)
    {
        UnregisterAll();
        Register(1, settings.ToggleRecordingGesture, HotkeyActions.ToggleRecording);
        Register(2, settings.CancelRecordingGesture, HotkeyActions.CancelRecording);
    }

    public void Dispose()
    {
        UnregisterAll();
        if (_previousWindowProc != IntPtr.Zero)
        {
            NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GwlpWndProc, _previousWindowProc);
            _previousWindowProc = IntPtr.Zero;
        }
    }

    private void Register(int id, string gesture, string actionName)
    {
        if (!TryParseGesture(gesture, out var modifiers, out var key))
        {
            return;
        }

        if (NativeMethods.RegisterHotKey(_windowHandle, id, modifiers, key))
        {
            _actionsById[id] = actionName;
        }
    }

    private void UnregisterAll()
    {
        foreach (var id in _actionsById.Keys)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, id);
        }

        _actionsById.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WmHotKey)
        {
            var id = wParam.ToInt32();
            if (_actionsById.TryGetValue(id, out var actionName))
            {
                HotkeyPressed?.Invoke(this, new HotkeyEventArgs(actionName));
                return IntPtr.Zero;
            }
        }

        return NativeMethods.CallWindowProc(_previousWindowProc, hwnd, msg, wParam, lParam);
    }

    private static bool TryParseGesture(string gesture, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;

        foreach (var token in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = token.ToUpperInvariant();
            switch (normalized)
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= NativeMethods.ModControl;
                    break;
                case "ALT":
                    modifiers |= NativeMethods.ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= NativeMethods.ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= NativeMethods.ModWin;
                    break;
                default:
                    if (!TryParseKey(normalized, out key))
                    {
                        return false;
                    }

                    break;
            }
        }

        return key != 0;
    }

    private static bool TryParseKey(string token, out uint key)
    {
        key = token switch
        {
            "SPACE" => 0x20,
            "ESC" or "ESCAPE" => 0x1B,
            "ENTER" => 0x0D,
            "TAB" => 0x09,
            "DELETE" or "DEL" => 0x2E,
            "BACKSPACE" => 0x08,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            _ => 0
        };

        if (key != 0)
        {
            return true;
        }

        if (token.Length == 1 && char.IsLetterOrDigit(token[0]))
        {
            key = token[0];
            return true;
        }

        if (token.StartsWith('F') &&
            int.TryParse(token[1..], out var functionIndex) &&
            functionIndex is >= 1 and <= 24)
        {
            key = (uint)(0x70 + functionIndex - 1);
            return true;
        }

        return false;
    }

    private delegate IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
