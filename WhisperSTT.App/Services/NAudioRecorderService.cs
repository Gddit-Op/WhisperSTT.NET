using System.IO;
using NAudio.Wave;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.Services;

public sealed class NAudioRecorderService : IAudioRecorderService
{
    private const int WaveHeaderSizeBytes = 44;
    private readonly ApplicationPaths _paths;
    private readonly object _syncRoot = new();
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<string>? _recordingStoppedSource;
    private string? _currentFilePath;
    private bool _discardOnStop;
    private long _recordedByteCount;

    public NAudioRecorderService(ApplicationPaths paths)
    {
        _paths = paths;
        _paths.EnsureCreated();
    }

    public bool IsRecording
    {
        get
        {
            lock (_syncRoot)
            {
                return _waveIn is not null;
            }
        }
    }

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

        var currentFilePath = Path.Combine(
            _paths.TempDirectory,
            $"recording-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.wav");

        var recordingStoppedSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waveIn = new WaveInEvent
        {
            DeviceNumber = ResolveDeviceNumber(settings.PreferredInputDeviceNumber),
            BufferMilliseconds = 200,
            WaveFormat = new WaveFormat(16000, 16, 1)
        };

        var writer = new WaveFileWriter(currentFilePath, waveIn.WaveFormat);
        waveIn.DataAvailable += OnDataAvailable;
        waveIn.RecordingStopped += OnRecordingStopped;

        lock (_syncRoot)
        {
            _currentFilePath = currentFilePath;
            _recordingStoppedSource = recordingStoppedSource;
            _discardOnStop = false;
            _recordedByteCount = 0;
            _waveIn = waveIn;
            _writer = writer;
        }

        waveIn.StartRecording();

        return Task.CompletedTask;
    }

    public async Task<string> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        Task<string> completionTask;
        WaveInEvent? waveIn;

        lock (_syncRoot)
        {
            if (_recordingStoppedSource is null)
            {
                throw new InvalidOperationException("Recording is not active.");
            }

            _discardOnStop = false;
            completionTask = _recordingStoppedSource.Task;
            waveIn = _waveIn;
        }

        waveIn?.StopRecording();
        return await completionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelRecordingAsync(CancellationToken cancellationToken = default)
    {
        Task<string>? completionTask;
        WaveInEvent? waveIn;

        lock (_syncRoot)
        {
            if (_recordingStoppedSource is null)
            {
                return;
            }

            _discardOnStop = true;
            completionTask = _recordingStoppedSource.Task;
            waveIn = _waveIn;
        }

        waveIn?.StopRecording();
        _ = await completionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        _writer?.Flush();
        Interlocked.Add(ref _recordedByteCount, e.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        WaveInEvent? waveIn;
        WaveFileWriter? writer;
        TaskCompletionSource<string>? recordingStoppedSource;
        string currentFilePath;
        bool discardOnStop;
        long recordedByteCount;

        lock (_syncRoot)
        {
            waveIn = _waveIn;
            _waveIn = null;
            writer = _writer;
            _writer = null;
            recordingStoppedSource = _recordingStoppedSource;
            currentFilePath = _currentFilePath ?? string.Empty;
            _currentFilePath = null;
            discardOnStop = _discardOnStop;
            recordedByteCount = _recordedByteCount;
            _recordedByteCount = 0;
        }

        if (waveIn is not null)
        {
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.RecordingStopped -= OnRecordingStopped;
            waveIn.Dispose();
        }

        writer?.Dispose();

        if (e.Exception is not null && !IsBenignStopException(e.Exception))
        {
            recordingStoppedSource?.TrySetException(e.Exception);
            return;
        }

        if (discardOnStop)
        {
            if (!string.IsNullOrWhiteSpace(currentFilePath) && File.Exists(currentFilePath))
            {
                File.Delete(currentFilePath);
            }

            recordingStoppedSource?.TrySetResult(string.Empty);
            return;
        }

        if (!HasUsableAudioData(currentFilePath, recordedByteCount))
        {
            if (!string.IsNullOrWhiteSpace(currentFilePath) && File.Exists(currentFilePath))
            {
                File.Delete(currentFilePath);
            }

            recordingStoppedSource?.TrySetResult(string.Empty);
            return;
        }

        recordingStoppedSource?.TrySetResult(currentFilePath);
    }

    private static bool HasUsableAudioData(string filePath, long recordedByteCount)
    {
        if (recordedByteCount > 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        var fileLength = new FileInfo(filePath).Length;
        return fileLength > WaveHeaderSizeBytes;
    }

    private static bool IsBenignStopException(Exception exception)
    {
        if (exception is not NAudio.MmException mmException)
        {
            return false;
        }

        return mmException.Message.Contains("WaveHeaderUnprepared", StringComparison.OrdinalIgnoreCase) &&
               mmException.Message.Contains("waveInAddBuffer", StringComparison.OrdinalIgnoreCase);
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
