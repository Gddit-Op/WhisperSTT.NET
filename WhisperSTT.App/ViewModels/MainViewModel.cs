using System.Media;
using System.IO;
using System.Windows.Media;
using Microsoft.Win32;
using MediaBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using FileDialog = Microsoft.Win32.OpenFileDialog;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _settingsStore;
    private readonly IActivityLogService _activityLogService;
    private readonly ITranscriptHistoryService _historyService;
    private readonly IModelManagementService _modelManagementService;
    private readonly ITranscriptionService _transcriptionService;
    private readonly IAudioRecorderService _audioRecorderService;
    private readonly IPasteService _pasteService;
    private readonly IAudioPreviewService _audioPreviewService;
    private AppStatus _status = AppStatus.Idle;
    private string _statusText = "Idle";
    private string _currentTranscript = string.Empty;
    private string _fileTranscript = string.Empty;
    private string _selectedFilePath;
    private string _lastError = "No errors.";
    private string _preferredInputDeviceNumberText;

    public MainViewModel(
        AppSettings settings,
        ISettingsStore settingsStore,
        IActivityLogService activityLogService,
        ITranscriptHistoryService historyService,
        IModelManagementService modelManagementService,
        ITranscriptionService transcriptionService,
        IAudioRecorderService audioRecorderService,
        IPasteService pasteService,
        IAudioPreviewService audioPreviewService)
    {
        Settings = settings;
        _settingsStore = settingsStore;
        _activityLogService = activityLogService;
        _historyService = historyService;
        _modelManagementService = modelManagementService;
        _transcriptionService = transcriptionService;
        _audioRecorderService = audioRecorderService;
        _pasteService = pasteService;
        _audioPreviewService = audioPreviewService;
        _selectedFilePath = settings.Transcription.LastFilePath;
        _preferredInputDeviceNumberText = settings.Audio.PreferredInputDeviceNumber?.ToString() ?? string.Empty;

        ToggleRecordingCommand = new AsyncRelayCommand(ToggleRecordingAsync, () => Status != AppStatus.Transcribing);
        CancelRecordingCommand = new AsyncRelayCommand(CancelRecordingAsync, () => Status == AppStatus.Recording);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        BrowseFileCommand = new RelayCommand(BrowseFile);
        TranscribeFileCommand = new AsyncRelayCommand(TranscribeSelectedFileAsync, CanTranscribeFile);
        DownloadModelCommand = new AsyncRelayCommand(DownloadSelectedModelAsync, () => Status != AppStatus.Recording);
        PlayPreviewCommand = new RelayCommand(PlayPreview, CanPreviewFile);
        PausePreviewCommand = new RelayCommand(PausePreview, () => _audioPreviewService.IsLoaded);
        StopPreviewCommand = new RelayCommand(StopPreview, () => _audioPreviewService.IsLoaded);
    }

    public event EventHandler? HotkeysChanged;

    public AppSettings Settings { get; }

    public Array Languages { get; } = Enum.GetValues<LanguageMode>();

    public Array ModelPresets { get; } = Enum.GetValues<ModelPreset>();

    public AsyncRelayCommand ToggleRecordingCommand { get; }

    public AsyncRelayCommand CancelRecordingCommand { get; }

    public AsyncRelayCommand SaveSettingsCommand { get; }

    public RelayCommand BrowseFileCommand { get; }

    public AsyncRelayCommand TranscribeFileCommand { get; }

    public AsyncRelayCommand DownloadModelCommand { get; }

    public RelayCommand PlayPreviewCommand { get; }

    public RelayCommand PausePreviewCommand { get; }

    public RelayCommand StopPreviewCommand { get; }

    public AppStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(RecordingButtonText));
                RaiseCommandStates();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public MediaBrush StatusBrush => Status switch
    {
        AppStatus.Idle => WpfBrushes.SeaGreen,
        AppStatus.Recording => WpfBrushes.Firebrick,
        AppStatus.Transcribing => WpfBrushes.DarkGoldenrod,
        AppStatus.Error => WpfBrushes.IndianRed,
        _ => WpfBrushes.Gray
    };

    public string RecordingButtonText => Status == AppStatus.Recording ? "Stop Recording" : "Start Recording";

    public string CurrentTranscript
    {
        get => _currentTranscript;
        private set => SetProperty(ref _currentTranscript, value);
    }

    public string FileTranscript
    {
        get => _fileTranscript;
        private set => SetProperty(ref _fileTranscript, value);
    }

    public string SelectedFilePath
    {
        get => _selectedFilePath;
        set
        {
            if (SetProperty(ref _selectedFilePath, value))
            {
                Settings.Transcription.LastFilePath = value;
                OnPropertyChanged(nameof(ActiveModelSummary));
                RaiseCommandStates();
            }
        }
    }

    public string LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public string PreferredInputDeviceNumberText
    {
        get => _preferredInputDeviceNumberText;
        set => SetProperty(ref _preferredInputDeviceNumberText, value);
    }

    public string ActiveModelSummary
    {
        get
        {
            var recordingModelPath = _modelManagementService.ResolveModelPath(Settings, Settings.Transcription.RecordingModelPreset);
            return File.Exists(recordingModelPath)
                ? $"Ready: {recordingModelPath}"
                : $"Missing model file: {recordingModelPath}";
        }
    }

    public string PersistenceSummary =>
        $"Config: {_settingsStore.ConfigPath}{Environment.NewLine}History: {_historyService.HistoryPath}{Environment.NewLine}Log: {_activityLogService.LogPath}";

    public async Task ToggleRecordingAsync()
    {
        try
        {
            if (Status == AppStatus.Recording)
            {
                await StopRecordingAndTranscribeAsync().ConfigureAwait(true);
                return;
            }

            ApplyPreferredInputDeviceSetting();
            await _audioRecorderService.StartRecordingAsync(Settings.Audio).ConfigureAwait(true);
            SetStatus(AppStatus.Recording, "Recording");
            await LogAsync("Recording started.").ConfigureAwait(true);
            PlayFeedbackSound(SystemSounds.Asterisk);
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(exception).ConfigureAwait(true);
        }
    }

    public async Task CancelRecordingAsync()
    {
        try
        {
            await _audioRecorderService.CancelRecordingAsync().ConfigureAwait(true);
            SetStatus(AppStatus.Idle, "Idle");
            await LogAsync("Recording cancelled.").ConfigureAwait(true);
            PlayFeedbackSound(SystemSounds.Hand);
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(exception).ConfigureAwait(true);
        }
    }

    public async Task SaveSettingsAsync()
    {
        try
        {
            ApplyPreferredInputDeviceSetting();
            await _settingsStore.SaveAsync(Settings).ConfigureAwait(true);
            HotkeysChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(ActiveModelSummary));
            OnPropertyChanged(nameof(PersistenceSummary));
            LastError = "Settings saved.";
            await LogAsync("Settings saved.").ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(exception).ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        _audioPreviewService.Dispose();
    }

    private async Task StopRecordingAndTranscribeAsync()
    {
        var audioPath = await _audioRecorderService.StopRecordingAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(audioPath))
        {
            SetStatus(AppStatus.Idle, "Idle");
            return;
        }

        SetStatus(AppStatus.Transcribing, "Transcribing microphone input");
        var transcript = await TranscribeAsync(
            audioPath,
            Settings.Transcription.RecordingModelPreset,
            Settings.Transcription.RecordingThreadCount).ConfigureAwait(true);

        CurrentTranscript = transcript;
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            await _pasteService.PasteTextAsync(
                transcript,
                Settings.Paste.RestoreClipboardAfterPaste).ConfigureAwait(true);

            if (Settings.Logging.WriteTranscriptHistory)
            {
                await _historyService.AppendAsync(transcript).ConfigureAwait(true);
            }
        }

        SetStatus(AppStatus.Idle, "Idle");
        PlayFeedbackSound(SystemSounds.Exclamation);
    }

    private async Task<string> TranscribeAsync(string audioPath, ModelPreset preset, int threadCount)
    {
        var modelPath = _modelManagementService.ResolveModelPath(Settings, preset);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"Model file was not found. Download the selected model first from Settings.{Environment.NewLine}{modelPath}",
                modelPath);
        }

        var result = await _transcriptionService.TranscribeAsync(new TranscriptionRequest(
            audioPath,
            modelPath,
            Settings.Transcription.LanguageMode,
            Math.Max(1, threadCount),
            Settings.Audio.EnableLivePreview)).ConfigureAwait(true);

        await LogAsync($"Transcription completed with model {modelPath}.").ConfigureAwait(true);
        return result.Text;
    }

    private void BrowseFile()
    {
        var dialog = new FileDialog
        {
            Filter = "Audio Files|*.wav;*.mp3",
            Title = "Select audio file"
        };

        if (dialog.ShowDialog() == true)
        {
            SelectedFilePath = dialog.FileName;
            _audioPreviewService.Load(dialog.FileName);
            RaiseCommandStates();
        }
    }

    private async Task TranscribeSelectedFileAsync()
    {
        try
        {
            SetStatus(AppStatus.Transcribing, "Transcribing local file");
            FileTranscript = await TranscribeAsync(
                SelectedFilePath,
                Settings.Transcription.FileModelPreset,
                Settings.Transcription.FileThreadCount).ConfigureAwait(true);

            if (Settings.Logging.WriteTranscriptHistory)
            {
                await _historyService.AppendAsync(FileTranscript).ConfigureAwait(true);
            }

            SetStatus(AppStatus.Idle, "Idle");
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(exception).ConfigureAwait(true);
        }
    }

    private async Task DownloadSelectedModelAsync()
    {
        try
        {
            SetStatus(AppStatus.Transcribing, "Downloading Whisper model");
            var modelPath = await _modelManagementService
                .DownloadModelAsync(Settings, Settings.Transcription.RecordingModelPreset)
                .ConfigureAwait(true);
            SetStatus(AppStatus.Idle, "Idle");
            LastError = $"Model available at {modelPath}";
            OnPropertyChanged(nameof(ActiveModelSummary));
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(exception).ConfigureAwait(true);
        }
    }

    private void PlayPreview()
    {
        if (!_audioPreviewService.IsLoaded && File.Exists(SelectedFilePath))
        {
            _audioPreviewService.Load(SelectedFilePath);
        }

        _audioPreviewService.Play();
    }

    private void PausePreview()
    {
        _audioPreviewService.Pause();
    }

    private void StopPreview()
    {
        _audioPreviewService.Stop();
    }

    private bool CanTranscribeFile()
    {
        return Status != AppStatus.Recording &&
               !string.IsNullOrWhiteSpace(SelectedFilePath) &&
               File.Exists(SelectedFilePath);
    }

    private bool CanPreviewFile()
    {
        return !string.IsNullOrWhiteSpace(SelectedFilePath) && File.Exists(SelectedFilePath);
    }

    private void ApplyPreferredInputDeviceSetting()
    {
        if (int.TryParse(PreferredInputDeviceNumberText, out var value) && value >= 0)
        {
            Settings.Audio.PreferredInputDeviceNumber = value;
            return;
        }

        Settings.Audio.PreferredInputDeviceNumber = null;
        PreferredInputDeviceNumberText = string.Empty;
    }

    private async Task HandleExceptionAsync(Exception exception)
    {
        LastError = exception.Message;
        SetStatus(AppStatus.Error, "Error");
        await LogAsync($"Error: {exception}").ConfigureAwait(true);
    }

    private async Task LogAsync(string message)
    {
        if (!Settings.Logging.EnableLogging)
        {
            return;
        }

        await _activityLogService.WriteAsync(message).ConfigureAwait(true);
    }

    private void SetStatus(AppStatus status, string text)
    {
        Status = status;
        StatusText = text;
    }

    private void RaiseCommandStates()
    {
        ToggleRecordingCommand.RaiseCanExecuteChanged();
        CancelRecordingCommand.RaiseCanExecuteChanged();
        SaveSettingsCommand.RaiseCanExecuteChanged();
        TranscribeFileCommand.RaiseCanExecuteChanged();
        DownloadModelCommand.RaiseCanExecuteChanged();
        PlayPreviewCommand.RaiseCanExecuteChanged();
        PausePreviewCommand.RaiseCanExecuteChanged();
        StopPreviewCommand.RaiseCanExecuteChanged();
    }

    private void PlayFeedbackSound(SystemSound sound)
    {
        if (Settings.Audio.EnableFeedbackSounds)
        {
            sound.Play();
        }
    }
}
