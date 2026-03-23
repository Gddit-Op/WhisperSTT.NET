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
        var remoteTranscriptionService = new RemoteServerTranscriptionClient(new HttpClient(), logger);

        return new MainViewModel(
            paths,
            settings,
            new JsonSettingsStore(paths),
            logger,
            new TranscriptHistoryService(paths),
            new WhisperModelService(paths),
            new WhisperTranscriptionService(logger),
            remoteTranscriptionService,
            remoteTranscriptionService,
            new SoundFlowRecorderService(paths, logger),
            new AudioInputDeviceService(),
            PlatformServices.CreatePasteService(logger),
            new AvaloniaFilePickerService(),
            PlatformServices.CreateAudioPreviewService(),
            new AvaloniaMessageDialogService());
    }
}
