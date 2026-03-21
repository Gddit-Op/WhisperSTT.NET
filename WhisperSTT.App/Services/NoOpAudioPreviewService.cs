using WhisperSTT.Core.Services;

namespace WhisperSTT.App.Services;

public sealed class NoOpAudioPreviewService : IAudioPreviewService
{
    public bool IsLoaded => false;

    public string? LoadedFilePath => null;

    public void Load(string filePath)
    {
        throw new InvalidOperationException("Audio preview is not available on this platform.");
    }

    public void Unload()
    {
    }

    public void Play()
    {
        throw new InvalidOperationException("Audio preview is not available on this platform.");
    }

    public void Pause()
    {
    }

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}
