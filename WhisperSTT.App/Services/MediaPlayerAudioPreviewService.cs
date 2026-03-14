using NAudio.Wave;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.Services;

public sealed class MediaPlayerAudioPreviewService : IAudioPreviewService
{
    private WaveOutEvent? _waveOut;
    private AudioFileReader? _audioFileReader;
    private string? _loadedFilePath;

    public bool IsLoaded => !string.IsNullOrWhiteSpace(_loadedFilePath);

    public void Load(string filePath)
    {
        DisposePlayback();
        _loadedFilePath = filePath;
        _audioFileReader = new AudioFileReader(filePath);
        _waveOut = new WaveOutEvent();
        _waveOut.Init(_audioFileReader);
    }

    public void Play()
    {
        _waveOut?.Play();
    }

    public void Pause()
    {
        _waveOut?.Pause();
    }

    public void Stop()
    {
        _waveOut?.Stop();
        if (_audioFileReader is not null)
        {
            _audioFileReader.Position = 0;
        }
    }

    public void Dispose()
    {
        DisposePlayback();
    }

    private void DisposePlayback()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;

        _audioFileReader?.Dispose();
        _audioFileReader = null;
    }
}
