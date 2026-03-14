using System.Text;
using System.Windows;
using WhisperSTT.App.Services;
using WhisperSTT.App.ViewModels;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

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
}
