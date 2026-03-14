using NAudio.CoreAudioApi;
using NAudio.Wave;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.Services;

public sealed class MediaPlayerAudioPreviewService : IAudioPreviewService
{
    private IWavePlayer? _waveOut;
    private AudioFileReader? _audioFileReader;
    private string? _loadedFilePath;

    public bool IsLoaded => !string.IsNullOrWhiteSpace(_loadedFilePath);

    public string? LoadedFilePath => _loadedFilePath;

    public void Load(string filePath)
    {
        if (string.Equals(_loadedFilePath, filePath, StringComparison.OrdinalIgnoreCase) && _waveOut is not null)
        {
            return;
        }

        DisposePlayback();

        var reader = new AudioFileReader(filePath);
        try
        {
            var player = CreatePlayer(reader);
            _audioFileReader = reader;
            _waveOut = player;
            _loadedFilePath = filePath;
        }
        catch
        {
            reader.Dispose();
            _loadedFilePath = null;
            throw;
        }
    }

    public void Play()
    {
        _waveOut?.Play();
    }

    public void Unload()
    {
        DisposePlayback();
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
        _loadedFilePath = null;
    }

    private static IWavePlayer CreatePlayer(IWaveProvider waveProvider)
    {
        try
        {
            var waveOut = new WaveOutEvent();
            waveOut.Init(waveProvider);
            return waveOut;
        }
        catch (Exception)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, false, 200);
                wasapiOut.Init(waveProvider);
                return wasapiOut;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Audio preview could not be initialized. File selection and transcription still work, but playback preview is unavailable on this system.",
                    exception);
            }
        }
    }
}
