using WhisperSTT.Core.Models;

namespace WhisperSTT.Core.Services;

public interface ISettingsStore
{
    string ConfigPath { get; }

    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface ITranscriptionService
{
    Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IAudioRecorderService
{
    bool IsRecording { get; }

    Task StartRecordingAsync(AudioSettings settings, CancellationToken cancellationToken = default);

    Task<string> StopRecordingAsync(CancellationToken cancellationToken = default);

    Task CancelRecordingAsync(CancellationToken cancellationToken = default);
}

public interface IAudioInputDeviceService
{
    IReadOnlyList<AudioInputDeviceOption> GetAvailableDevices();
}

public interface IPasteService
{
    Task CopyTextToClipboardAsync(
        string text,
        CancellationToken cancellationToken = default);

    Task PasteTextAsync(
        string text,
        bool restoreClipboard,
        CancellationToken cancellationToken = default);
}

public interface IFilePickerService
{
    Task<string?> PickAudioFileAsync(CancellationToken cancellationToken = default);

    Task<string?> PickFolderAsync(string title, CancellationToken cancellationToken = default);
}

public interface IModelManagementService
{
    string ResolveModelPath(AppSettings settings, ModelPreset preset);

    Task<string> DownloadModelAsync(
        AppSettings settings,
        ModelPreset preset,
        CancellationToken cancellationToken = default);
}

public interface ITranscriptHistoryService
{
    string HistoryPath { get; }

    Task AppendAsync(string transcript, CancellationToken cancellationToken = default);
}

public interface IActivityLogService
{
    string LogPath { get; }

    Task WriteAsync(string message, CancellationToken cancellationToken = default);
}

public interface IAudioPreviewService : IDisposable
{
    bool IsLoaded { get; }

    string? LoadedFilePath { get; }

    void Load(string filePath);

    void Unload();

    void Play();

    void Pause();

    void Stop();
}
