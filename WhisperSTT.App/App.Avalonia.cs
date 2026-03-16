using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using WhisperSTT.App.Services;
using WhisperSTT.App.ViewModels;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App;

public partial class App : Avalonia.Application
{
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private TrayIcon? _trayIcon;
    private TrayIcons? _trayIcons;
    private NativeMenuItem? _toggleRecordingMenuItem;
    private StatusIconSet? _statusIcons;
    private bool _isExiting;
    private IClassicDesktopStyleApplicationLifetime? _desktopLifetime;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktopLifetime)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        _desktopLifetime = desktopLifetime;
        _desktopLifetime.Exit += OnDesktopLifetimeExit;

        try
        {
            var paths = new ApplicationPaths();
            var settingsStore = new JsonSettingsStore(paths);
            var settings = await settingsStore.LoadAsync().ConfigureAwait(true);

            var logger = new FileActivityLogService(paths);
            var history = new TranscriptHistoryService(paths);
            var modelManager = new WhisperModelService(paths);
            var transcriptionService = new WhisperTranscriptionService(logger);
            var recorderService = new NAudioRecorderService(paths, logger);
            var audioInputDeviceService = new AudioInputDeviceService();
            var pasteService = new ClipboardPasteService(logger);
            var filePickerService = new AvaloniaFilePickerService();
            var audioPreviewService = new MediaPlayerAudioPreviewService();

            _viewModel = new MainViewModel(
                settings,
                settingsStore,
                logger,
                history,
                modelManager,
                transcriptionService,
                recorderService,
                audioInputDeviceService,
                pasteService,
                filePickerService,
                audioPreviewService);

            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            _window = new MainWindow(_viewModel);
            _window.Closing += OnMainWindowClosing;
            desktopLifetime.MainWindow = _window;
            CreateTrayIcon();
            _window.ShowFromTray();
        }
        catch (Exception exception)
        {
            ShowStartupErrorWindow(desktopLifetime, exception.Message);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnDesktopLifetimeExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_trayIcons is not null)
        {
            TrayIcon.SetIcons(this, null);
        }

        _trayIcon?.Dispose();
        _statusIcons?.Dispose();
        _viewModel?.Dispose();
    }

    private void CreateTrayIcon()
    {
        if (_viewModel is null)
        {
            return;
        }

        var contextMenu = new NativeMenu();
        _toggleRecordingMenuItem = new NativeMenuItem("Start Recording")
        {
            Command = _viewModel.ToggleRecordingCommand
        };

        var cancelMenuItem = new NativeMenuItem("Cancel Recording")
        {
            Command = _viewModel.CancelRecordingCommand
        };

        var openMenuItem = new NativeMenuItem("Open Settings");
        openMenuItem.Click += (_, _) => _window?.ShowFromTray();

        var exitMenuItem = new NativeMenuItem("Exit");
        exitMenuItem.Click += (_, _) => ExitApplication();

        contextMenu.Add(openMenuItem);
        contextMenu.Add(_toggleRecordingMenuItem);
        contextMenu.Add(cancelMenuItem);
        contextMenu.Add(new NativeMenuItemSeparator());
        contextMenu.Add(exitMenuItem);

        _trayIcon = new TrayIcon
        {
            IsVisible = true,
            Menu = contextMenu,
            ToolTipText = "WhisperSTT"
        };

        _trayIcon.Clicked += (_, _) => _window?.ShowFromTray();
        _trayIcons = [_trayIcon];
        TrayIcon.SetIcons(this, _trayIcons);
        UpdateTrayPresentation();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Status) or nameof(MainViewModel.StatusText))
        {
            UpdateTrayPresentation();
        }
    }

    private void UpdateTrayPresentation()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_toggleRecordingMenuItem is not null)
        {
            _toggleRecordingMenuItem.Header = _viewModel.Status == AppStatus.Recording
                ? "Stop Recording"
                : "Start Recording";
        }

        if (_trayIcon is not null)
        {
            var tooltip = $"WhisperSTT - {_viewModel.StatusText}";
            _trayIcon.ToolTipText = tooltip.Length > 63 ? tooltip[..63] : tooltip;
        }

        UpdateStatusIcons(_viewModel.Status);
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        _window?.HideToTray();
    }

    private void ExitApplication()
    {
        _isExiting = true;

        if (_window is not null)
        {
            _window.Close();
        }

        _desktopLifetime?.Shutdown();
    }

    private void UpdateStatusIcons(AppStatus status)
    {
        var previousIcons = _statusIcons;
        _statusIcons = StatusIconFactory.Create(status);

        if (_trayIcon is not null)
        {
            _trayIcon.Icon = _statusIcons.TrayIcon;
        }

        if (_window is not null)
        {
            _window.Icon = _statusIcons.WindowIcon;
        }

        previousIcons?.Dispose();
    }

    private static void ShowStartupErrorWindow(
        IClassicDesktopStyleApplicationLifetime desktopLifetime,
        string message)
    {
        var closeButton = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var errorWindow = new Window
        {
            Title = "WhisperSTT startup failed",
            Width = 540,
            Height = 240,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "The application could not be started.",
                        FontSize = 20,
                        FontWeight = Avalonia.Media.FontWeight.SemiBold
                    },
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    closeButton
                }
            }
        };

        closeButton.Click += (_, _) => desktopLifetime.Shutdown(-1);
        errorWindow.Closed += (_, _) => desktopLifetime.Shutdown(-1);
        desktopLifetime.MainWindow = errorWindow;
        errorWindow.Show();
    }
}
