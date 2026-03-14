using System.Text;
using System.Windows;
using WhisperSTT.App.Services;
using WhisperSTT.App.ViewModels;
using System.Windows.Controls;
using System.Windows.Input;

namespace WhisperSTT.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private GlobalHotkeyService? _globalHotkeyService;
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = _viewModel;
        SourceInitialized += OnSourceInitialized;
        _viewModel.HotkeysChanged += OnHotkeysChanged;
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    public void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosed(EventArgs e)
    {
        _globalHotkeyService?.Dispose();
        _viewModel.HotkeysChanged -= OnHotkeysChanged;
        base.OnClosed(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _globalHotkeyService = new GlobalHotkeyService(this);
        _globalHotkeyService.HotkeyPressed += OnHotkeyPressed;
        _globalHotkeyService.ApplySettings(_viewModel.Settings.Hotkeys);
    }

    private async void OnHotkeyPressed(object? sender, HotkeyEventArgs e)
    {
        if (e.ActionName == HotkeyActions.ToggleRecording)
        {
            await _viewModel.ToggleRecordingAsync();
            return;
        }

        if (e.ActionName == HotkeyActions.CancelRecording)
        {
            await _viewModel.CancelRecordingAsync();
        }
    }

    private void OnHotkeysChanged(object? sender, EventArgs e)
    {
        _globalHotkeyService?.ApplySettings(_viewModel.Settings.Hotkeys);
    }

    private void OnHotkeyTextBoxPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }

        var gesture = BuildGesture(Keyboard.Modifiers, key);
        if (string.IsNullOrWhiteSpace(gesture))
        {
            e.Handled = true;
            return;
        }

        switch (textBox.Tag as string)
        {
            case "ToggleRecording":
                _viewModel.Settings.Hotkeys.ToggleRecordingGesture = gesture;
                break;
            case "CancelRecording":
                _viewModel.Settings.Hotkeys.CancelRecordingGesture = gesture;
                break;
            default:
                e.Handled = true;
                return;
        }

        textBox.Text = gesture;
        _viewModel.NotifyHotkeyValuesChanged();
        e.Handled = true;
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin;
    }

    private static string BuildGesture(ModifierKeys modifiers, Key key)
    {
        var tokens = new List<string>();

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            tokens.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            tokens.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            tokens.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            tokens.Add("Win");
        }

        var keyToken = ToGestureKey(key);
        if (string.IsNullOrWhiteSpace(keyToken))
        {
            return string.Empty;
        }

        tokens.Add(keyToken);
        return string.Join("+", tokens);
    }

    private static string ToGestureKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return key.ToString().ToUpperInvariant();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((char)('0' + (key - Key.D0))).ToString();
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            return key.ToString().ToUpperInvariant();
        }

        return key switch
        {
            Key.Space => "Space",
            Key.Escape => "Escape",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            _ => string.Empty
        };
    }
}
