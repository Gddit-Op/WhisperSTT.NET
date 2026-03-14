using System.Windows.Media;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.Services;

public sealed class MediaPlayerAudioPreviewService : IAudioPreviewService
{
    private readonly MediaPlayer _player = new();
    private string? _loadedFilePath;

    public bool IsLoaded => !string.IsNullOrWhiteSpace(_loadedFilePath);

    public void Load(string filePath)
    {
        _loadedFilePath = filePath;
        _player.Open(new Uri(filePath));
    }

    public void Play()
    {
        _player.Play();
    }

    public void Pause()
    {
        _player.Pause();
    }

    public void Stop()
    {
        _player.Stop();
    }

    public void Dispose()
    {
        _player.Close();
    }
}
