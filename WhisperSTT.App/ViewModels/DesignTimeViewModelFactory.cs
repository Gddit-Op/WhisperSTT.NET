using WhisperSTT.App.Services;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.ViewModels;

internal static class DesignTimeViewModelFactory
{
    public static MainViewModel Create()
    {
        var paths = new ApplicationPaths();
        var settings = new AppSettings();

        return new MainViewModel(
            settings,
            new JsonSettingsStore(paths),
            new FileActivityLogService(paths),
            new TranscriptHistoryService(paths),
            new WhisperModelService(paths),
            new WhisperTranscriptionService(),
            new NAudioRecorderService(paths),
            new ClipboardPasteService(),
            new AvaloniaFilePickerService(),
            new MediaPlayerAudioPreviewService());
    }
}
