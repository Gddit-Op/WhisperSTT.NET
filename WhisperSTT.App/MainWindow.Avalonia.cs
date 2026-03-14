using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using WhisperSTT.App.Services;
using WhisperSTT.App.ViewModels;

namespace WhisperSTT.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private GlobalHotkeyService? _globalHotkeyService;

    public MainWindow()
    {
        _viewModel = DesignTimeViewModelFactory.Create();
        InitializeWindow();
        DataContext = _viewModel;
    }

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeWindow();
        DataContext = _viewModel;
    }

    private void InitializeWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        _viewModel.HotkeysChanged += OnHotkeysChanged;
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

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_globalHotkeyService is not null)
        {
            return;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _globalHotkeyService = new GlobalHotkeyService(handle);
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

    private void OnHotkeyTextBoxKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (sender is not Avalonia.Controls.TextBox textBox)
        {
            return;
        }

        var key = e.Key;
        if (IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }

        var gesture = BuildGesture(e.KeyModifiers, key);
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

    private static string BuildGesture(KeyModifiers modifiers, Key key)
    {
        var tokens = new List<string>();

        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            tokens.Add("Ctrl");
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            tokens.Add("Alt");
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            tokens.Add("Shift");
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
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
