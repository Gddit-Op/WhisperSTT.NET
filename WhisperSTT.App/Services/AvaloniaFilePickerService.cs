using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.Services;

public sealed class AvaloniaFilePickerService : IFilePickerService
{
    public async Task<string?> PickAudioFileAsync(CancellationToken cancellationToken = default)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            return null;
        }

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select audio file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio Files")
                {
                    Patterns = ["*.wav", "*.mp3"],
                    MimeTypes = ["audio/wav", "audio/mpeg"]
                }
            ]
        }).ConfigureAwait(true);

        cancellationToken.ThrowIfCancellationRequested();
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
