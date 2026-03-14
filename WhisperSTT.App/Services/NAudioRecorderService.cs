using System.IO;
using NAudio.Wave;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.Services;

public sealed class NAudioRecorderService : IAudioRecorderService
{
    private readonly ApplicationPaths _paths;
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<string>? _recordingStoppedSource;
    private string? _currentFilePath;
    private bool _discardOnStop;

    public NAudioRecorderService(ApplicationPaths paths)
    {
        _paths = paths;
        _paths.EnsureCreated();
    }

    public bool IsRecording => _waveIn is not null;

    public Task StartRecordingAsync(AudioSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (WaveInEvent.DeviceCount <= 0)
        {
            throw new InvalidOperationException("No microphone input device is available.");
        }

        if (IsRecording)
        {
            return Task.CompletedTask;
        }

        _currentFilePath = Path.Combine(
            _paths.TempDirectory,
            $"recording-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.wav");

        _recordingStoppedSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _discardOnStop = false;

        _waveIn = new WaveInEvent
        {
            DeviceNumber = ResolveDeviceNumber(settings.PreferredInputDeviceNumber),
            BufferMilliseconds = 200,
            WaveFormat = new WaveFormat(16000, 16, 1)
        };

        _writer = new WaveFileWriter(_currentFilePath, _waveIn.WaveFormat);
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();

        return Task.CompletedTask;
    }

    public async Task<string> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (_waveIn is null || _recordingStoppedSource is null)
        {
            throw new InvalidOperationException("Recording is not active.");
        }

        _discardOnStop = false;
        _waveIn.StopRecording();
        return await _recordingStoppedSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (_waveIn is null || _recordingStoppedSource is null)
        {
            return;
        }

        _discardOnStop = true;
        _waveIn.StopRecording();
        _ = await _recordingStoppedSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        _writer?.Flush();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }

        _writer?.Dispose();
        _writer = null;

        if (e.Exception is not null)
        {
            _recordingStoppedSource?.TrySetException(e.Exception);
            return;
        }

        if (_discardOnStop)
        {
            if (!string.IsNullOrWhiteSpace(_currentFilePath) && File.Exists(_currentFilePath))
            {
                File.Delete(_currentFilePath);
            }

            _recordingStoppedSource?.TrySetResult(string.Empty);
            return;
        }

        _recordingStoppedSource?.TrySetResult(_currentFilePath ?? string.Empty);
    }

    private static int ResolveDeviceNumber(int? preferredDeviceNumber)
    {
        if (preferredDeviceNumber is >= 0 &&
            preferredDeviceNumber.Value < WaveInEvent.DeviceCount)
        {
            return preferredDeviceNumber.Value;
        }

        return 0;
    }
}
