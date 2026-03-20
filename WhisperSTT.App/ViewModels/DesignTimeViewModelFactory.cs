using WhisperSTT.App.Services;
using WhisperSTT.Client.Services;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.ViewModels;

internal static class DesignTimeViewModelFactory
{
    public static MainViewModel Create()
    {
        var paths = new ApplicationPaths();
        var settings = new AppSettings();
        var logger = new FileActivityLogService(paths);

        return new MainViewModel(
            paths,
            settings,
            new JsonSettingsStore(paths),
            logger,
            new TranscriptHistoryService(paths),
            new WhisperModelService(paths),
            new WhisperTranscriptionService(logger),
            new WebRtcTranscriptionClient(new HttpClient(), logger),
            new SoundFlowRecorderService(paths, logger),
            new AudioInputDeviceService(),
            new ClipboardPasteService(logger),
            new AvaloniaFilePickerService(),
            new MediaPlayerAudioPreviewService());
    }
}
