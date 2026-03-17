using System.Media;
using System.IO;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvaloniaBrushes = Avalonia.Media.Brushes;
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
    private readonly IAudioInputDeviceService _audioInputDeviceService;
    private readonly IPasteService _pasteService;
    private readonly IFilePickerService _filePickerService;
    private readonly IAudioPreviewService _audioPreviewService;
    private AppStatus _status = AppStatus.Idle;
    private string _statusText = "Idle";
    private string _currentTranscript = string.Empty;
    private string _fileTranscript = string.Empty;
    private string _selectedFilePath;
    private string _lastError = "No errors.";
    private string _lastDetectedLanguage = "unknown";
    private IReadOnlyList<AudioInputDeviceOption> _inputDevices = [];
    private AudioInputDeviceOption? _selectedInputDevice;

    public MainViewModel(
        AppSettings settings,
        ISettingsStore settingsStore,
        IActivityLogService activityLogService,
        ITranscriptHistoryService historyService,
        IModelManagementService modelManagementService,
        ITranscriptionService transcriptionService,
        IAudioRecorderService audioRecorderService,
        IAudioInputDeviceService audioInputDeviceService,
        IPasteService pasteService,
        IFilePickerService filePickerService,
        IAudioPreviewService audioPreviewService)
    {
        Settings = settings;
        _settingsStore = settingsStore;
        _activityLogService = activityLogService;
        _historyService = historyService;
        _modelManagementService = modelManagementService;
        _transcriptionService = transcriptionService;
        _audioRecorderService = audioRecorderService;
        _audioInputDeviceService = audioInputDeviceService;
        _pasteService = pasteService;
        _filePickerService = filePickerService;
        _audioPreviewService = audioPreviewService;
        _selectedFilePath = settings.Transcription.LastFilePath;

        ToggleRecordingCommand = new AsyncRelayCommand(ToggleRecordingAsync, () => Status != AppStatus.Transcribing);
        CancelRecordingCommand = new AsyncRelayCommand(CancelRecordingAsync, () => Status == AppStatus.Recording || _audioRecorderService.IsRecording);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        BrowseFileCommand = new AsyncRelayCommand(BrowseFileAsync);
        BrowseOpenVinoRuntimePathCommand = new AsyncRelayCommand(BrowseOpenVinoRuntimePathAsync);
        TranscribeFileCommand = new AsyncRelayCommand(TranscribeSelectedFileAsync, CanTranscribeFile);
        DownloadModelCommand = new AsyncRelayCommand(DownloadSelectedModelAsync, () => Status != AppStatus.Recording);
        CopyLatestTranscriptCommand = new AsyncRelayCommand(CopyLatestTranscriptAsync, CanCopyLatestTranscript);
        PlayPreviewCommand = new RelayCommand(PlayPreview, CanPreviewFile);
        PausePreviewCommand = new RelayCommand(PausePreview, () => _audioPreviewService.IsLoaded);
        StopPreviewCommand = new RelayCommand(StopPreview, () => _audioPreviewService.IsLoaded);
        RefreshInputDevicesCommand = new RelayCommand(RefreshInputDevices);

        RefreshInputDevices();
        InitializePreviewState();
    }

    public event EventHandler? HotkeysChanged;

    public AppSettings Settings { get; }

    public Array Languages { get; } = Enum.GetValues<LanguageMode>();

    public Array RuntimePreferences { get; } = Enum.GetValues<WhisperRuntimePreference>();

    public Array ModelPresets { get; } = Enum.GetValues<ModelPreset>();

    public AsyncRelayCommand ToggleRecordingCommand { get; }

    public AsyncRelayCommand CancelRecordingCommand { get; }

    public AsyncRelayCommand SaveSettingsCommand { get; }

    public AsyncRelayCommand BrowseFileCommand { get; }

    public AsyncRelayCommand BrowseOpenVinoRuntimePathCommand { get; }

    public AsyncRelayCommand TranscribeFileCommand { get; }

    public AsyncRelayCommand DownloadModelCommand { get; }

    public AsyncRelayCommand CopyLatestTranscriptCommand { get; }

    public RelayCommand PlayPreviewCommand { get; }

    public RelayCommand PausePreviewCommand { get; }

    public RelayCommand StopPreviewCommand { get; }

    public RelayCommand RefreshInputDevicesCommand { get; }

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

    public IBrush StatusBrush => Status switch
    {
        AppStatus.Idle => AvaloniaBrushes.SeaGreen,
        AppStatus.Recording => AvaloniaBrushes.Firebrick,
        AppStatus.Transcribing => AvaloniaBrushes.DarkGoldenrod,
        AppStatus.Error => AvaloniaBrushes.IndianRed,
        _ => AvaloniaBrushes.Gray
    };

    public string RecordingButtonText => Status == AppStatus.Recording || _audioRecorderService.IsRecording ? "Stop Recording" : "Start Recording";

    public string CurrentTranscript
    {
        get => _currentTranscript;
        private set
        {
            if (SetProperty(ref _currentTranscript, value))
            {
                RaiseCommandStates();
            }
        }
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
                if (!string.Equals(_audioPreviewService.LoadedFilePath, value, StringComparison.OrdinalIgnoreCase))
                {
                    _audioPreviewService.Unload();
                }

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

    public string OpenVinoRuntimePath
    {
        get => Settings.Transcription.OpenVinoRuntimePath;
        set
        {
            if (string.Equals(Settings.Transcription.OpenVinoRuntimePath, value, StringComparison.Ordinal))
            {
                return;
            }

            Settings.Transcription.OpenVinoRuntimePath = value;
            OnPropertyChanged();
        }
    }

    public string LastDetectedLanguage
    {
        get => _lastDetectedLanguage;
        private set
        {
            if (SetProperty(ref _lastDetectedLanguage, value))
            {
                OnPropertyChanged(nameof(DetectedLanguageText));
            }
        }
    }

    public string DetectedLanguageText => $"Detected language: {LastDetectedLanguage}";

    public IReadOnlyList<AudioInputDeviceOption> InputDevices
    {
        get => _inputDevices;
        private set => SetProperty(ref _inputDevices, value);
    }

    public AudioInputDeviceOption? SelectedInputDevice
    {
        get => _selectedInputDevice;
        set
        {
            if (SetProperty(ref _selectedInputDevice, value))
            {
                Settings.Audio.PreferredInputDeviceNumber = value?.DeviceNumber;
            }
        }
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
            if (Status == AppStatus.Recording || _audioRecorderService.IsRecording)
            {
                await StopRecordingAndTranscribeAsync().ConfigureAwait(true);
                return;
            }

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

    public void NotifyHotkeyValuesChanged()
    {
        OnPropertyChanged(nameof(Settings));
    }

    public void Dispose()
    {
        _audioPreviewService.Dispose();
    }

    private async Task CopyLatestTranscriptAsync()
    {
        try
        {
            await _pasteService.CopyTextToClipboardAsync(CurrentTranscript).ConfigureAwait(true);
            LastError = "Latest transcript copied to clipboard.";
            await LogAsync("Latest transcript copied to clipboard.").ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(exception).ConfigureAwait(true);
        }
    }

    private async Task StopRecordingAndTranscribeAsync()
    {
        var audioPath = await _audioRecorderService.StopRecordingAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(audioPath))
        {
            SetStatus(AppStatus.Idle, "Idle");
            LastError = "No microphone audio data was captured. Check the selected input device and Windows microphone access.";
            await LogAsync("Recording stopped without usable audio data.").ConfigureAwait(true);
            return;
        }

        SetStatus(AppStatus.Transcribing, "Transcribing microphone input");
        var result = await TranscribeAsync(
            audioPath,
            Settings.Transcription.RecordingModelPreset,
            Settings.Transcription.RecordingThreadCount).ConfigureAwait(true);

        CurrentTranscript = result.Text;
        UpdateDetectedLanguage(result.DetectedLanguage);
        if (!string.IsNullOrWhiteSpace(result.Text))
        {
            await TryPasteTranscriptAsync(result.Text).ConfigureAwait(true);

            if (Settings.Logging.WriteTranscriptHistory)
            {
                await _historyService.AppendAsync(result.Text).ConfigureAwait(true);
            }
        }

        SetStatus(AppStatus.Idle, "Idle");
        PlayFeedbackSound(SystemSounds.Exclamation);
    }

    private async Task<TranscriptionResult> TranscribeAsync(string audioPath, ModelPreset preset, int threadCount)
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
            Settings.Transcription.RuntimePreference,
            OpenVinoRuntimePath,
            Settings.Audio.EnableLivePreview,
            Settings.Logging.EnableLogging)).ConfigureAwait(true);

        var detectedLanguageText = string.IsNullOrWhiteSpace(result.DetectedLanguage)
            ? "unknown"
            : result.DetectedLanguage;
        var usedRuntimeText = string.IsNullOrWhiteSpace(result.UsedRuntime)
            ? "unknown"
            : result.UsedRuntime;
        await LogAsync($"Configured runtime: {Settings.Transcription.RuntimePreference}.").ConfigureAwait(true);
        await LogAsync($"Used runtime: {usedRuntimeText}.").ConfigureAwait(true);
        await LogAsync($"Detected language: {detectedLanguageText}.").ConfigureAwait(true);
        await LogAsync($"Transcription completed with model {modelPath}.").ConfigureAwait(true);
        return result;
    }

    private async Task BrowseFileAsync()
    {
        var filePath = await _filePickerService.PickAudioFileAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        SelectedFilePath = filePath;
        TryLoadPreview(filePath);
        RaiseCommandStates();
    }

    private async Task BrowseOpenVinoRuntimePathAsync()
    {
        var folderPath = await _filePickerService
            .PickFolderAsync("Select OpenVINO Toolkit folder")
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        OpenVinoRuntimePath = folderPath;
        LastError = $"OpenVINO Toolkit folder selected: {folderPath}";
    }

    private async Task TranscribeSelectedFileAsync()
    {
        try
        {
            SetStatus(AppStatus.Transcribing, "Transcribing local file");
            var result = await TranscribeAsync(
                SelectedFilePath,
                Settings.Transcription.FileModelPreset,
                Settings.Transcription.FileThreadCount).ConfigureAwait(true);
            FileTranscript = result.Text;
            UpdateDetectedLanguage(result.DetectedLanguage);

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
            if (!TryLoadPreview(SelectedFilePath))
            {
                return;
            }
        }

        _audioPreviewService.Play();
        RaiseCommandStates();
    }

    private void PausePreview()
    {
        _audioPreviewService.Pause();
        RaiseCommandStates();
    }

    private void StopPreview()
    {
        _audioPreviewService.Stop();
        RaiseCommandStates();
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

    private bool CanCopyLatestTranscript()
    {
        return !string.IsNullOrWhiteSpace(CurrentTranscript);
    }

    private bool TryLoadPreview(string filePath)
    {
        try
        {
            _audioPreviewService.Load(filePath);
            RaiseCommandStates();
            return true;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            _ = LogAsync($"Preview unavailable: {exception.Message}");
            RaiseCommandStates();
            return false;
        }
    }

    private void InitializePreviewState()
    {
        if (!string.IsNullOrWhiteSpace(_selectedFilePath) && File.Exists(_selectedFilePath))
        {
            TryLoadPreview(_selectedFilePath);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_selectedFilePath) && !File.Exists(_selectedFilePath))
        {
            Settings.Transcription.LastFilePath = string.Empty;
            _selectedFilePath = string.Empty;
        }

        RaiseCommandStates();
    }

    private void RefreshInputDevices()
    {
        var devices = _audioInputDeviceService.GetAvailableDevices().ToList();
        var options = new List<AudioInputDeviceOption>
        {
            new(null, devices.Count > 0
                ? $"Automatic (fallback to device 0: {devices[0].DisplayName})"
                : "Automatic (fallback to device 0)")
        };

        options.AddRange(devices);

        if (Settings.Audio.PreferredInputDeviceNumber is { } preferredDeviceNumber &&
            options.All(option => option.DeviceNumber != preferredDeviceNumber))
        {
            options.Add(new AudioInputDeviceOption(
                preferredDeviceNumber,
                $"{preferredDeviceNumber}: Saved device currently unavailable"));
        }

        InputDevices = options;
        SelectedInputDevice = options.FirstOrDefault(option => option.DeviceNumber == Settings.Audio.PreferredInputDeviceNumber)
            ?? options[0];
    }

    private async Task HandleExceptionAsync(Exception exception)
    {
        LastError = exception.Message;
        if (exception is InvalidOperationException &&
            exception.Message.Contains("Recording is not active.", StringComparison.Ordinal))
        {
            SetStatus(AppStatus.Idle, "Idle");
        }
        else
        {
            SetStatus(AppStatus.Error, "Error");
        }

        await LogAsync($"Error: {exception}").ConfigureAwait(true);
    }

    private void UpdateDetectedLanguage(string? detectedLanguage)
    {
        LastDetectedLanguage = string.IsNullOrWhiteSpace(detectedLanguage)
            ? "unknown"
            : detectedLanguage;
    }

    private async Task LogAsync(string message)
    {
        if (!Settings.Logging.EnableLogging)
        {
            return;
        }

        await _activityLogService.WriteAsync(message).ConfigureAwait(true);
    }

    private async Task TryPasteTranscriptAsync(string text)
    {
        try
        {
            await _pasteService.PasteTextAsync(
                text,
                Settings.Paste.RestoreClipboardAfterPaste).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            await LogAsync(CreatePasteFailureLogMessage(exception.Message)).ConfigureAwait(true);
        }
    }

    private static string CreatePasteFailureLogMessage(string message)
    {
        const string prefix = "Automatic paste failed";
        if (message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return message;
        }

        return $"{prefix}: {message}";
    }

    private void SetStatus(AppStatus status, string text)
    {
        Status = status;
        StatusText = text;
    }

    private void RaiseCommandStates()
    {
        ToggleRecordingCommand.NotifyCanExecuteChanged();
        CancelRecordingCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
        TranscribeFileCommand.NotifyCanExecuteChanged();
        DownloadModelCommand.NotifyCanExecuteChanged();
        CopyLatestTranscriptCommand.NotifyCanExecuteChanged();
        PlayPreviewCommand.NotifyCanExecuteChanged();
        PausePreviewCommand.NotifyCanExecuteChanged();
        StopPreviewCommand.NotifyCanExecuteChanged();
    }

    private void PlayFeedbackSound(SystemSound sound)
    {
        if (Settings.Audio.EnableFeedbackSounds)
        {
            sound.Play();
        }
    }
}
