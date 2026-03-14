using System.Configuration;
using System.Data;
using System.ComponentModel;
using Drawing = System.Drawing;
using System.Windows;
using WhisperSTT.App.Services;
using WhisperSTT.App.ViewModels;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;
using Forms = System.Windows.Forms;

namespace WhisperSTT.App;

public partial class App : System.Windows.Application
{
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _toggleRecordingMenuItem;
    private bool _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var paths = new ApplicationPaths();
            var settingsStore = new JsonSettingsStore(paths);
            var settings = await settingsStore.LoadAsync().ConfigureAwait(true);

            var logger = new FileActivityLogService(paths);
            var history = new TranscriptHistoryService(paths);
            var modelManager = new WhisperModelService(paths);
            var transcriptionService = new WhisperTranscriptionService();
            var recorderService = new NAudioRecorderService(paths);
            var pasteService = new ClipboardPasteService();
            var audioPreviewService = new MediaPlayerAudioPreviewService();

            _viewModel = new MainViewModel(
                settings,
                settingsStore,
                logger,
                history,
                modelManager,
                transcriptionService,
                recorderService,
                pasteService,
                audioPreviewService);

            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            _window = new MainWindow(_viewModel);
            _window.Closing += OnMainWindowClosing;
            _window.Show();
            _window.HideToTray();

            CreateTrayIcon();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                exception.Message,
                "WhisperSTT startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _viewModel?.Dispose();
        base.OnExit(e);
    }

    private void CreateTrayIcon()
    {
        if (_viewModel is null)
        {
            return;
        }

        var contextMenu = new Forms.ContextMenuStrip();
        _toggleRecordingMenuItem = new Forms.ToolStripMenuItem("Start Recording");
        _toggleRecordingMenuItem.Click += async (_, _) => await _viewModel.ToggleRecordingAsync();

        var cancelMenuItem = new Forms.ToolStripMenuItem("Cancel Recording");
        cancelMenuItem.Click += async (_, _) => await _viewModel.CancelRecordingAsync();

        var openMenuItem = new Forms.ToolStripMenuItem("Open Settings");
        openMenuItem.Click += (_, _) => _window?.ShowFromTray();

        var exitMenuItem = new Forms.ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => ExitApplication();

        contextMenu.Items.Add(openMenuItem);
        contextMenu.Items.Add(_toggleRecordingMenuItem);
        contextMenu.Items.Add(cancelMenuItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add(exitMenuItem);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = contextMenu,
            Text = "WhisperSTT"
        };

        _trayIcon.DoubleClick += (_, _) => _window?.ShowFromTray();
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
            _toggleRecordingMenuItem.Text = _viewModel.Status == AppStatus.Recording
                ? "Stop Recording"
                : "Start Recording";
        }

        if (_trayIcon is not null)
        {
            var tooltip = $"WhisperSTT - {_viewModel.StatusText}";
            _trayIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
        }
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
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
            _window.AllowClose();
            _window.Close();
        }

        Shutdown();
    }
}

