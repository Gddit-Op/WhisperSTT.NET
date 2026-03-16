using System.IO;
using NAudio.Wave;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.Services;

public sealed class NAudioRecorderService : IAudioRecorderService
{
    private const int WaveHeaderSizeBytes = 44;
    private readonly ApplicationPaths _paths;
    private readonly IActivityLogService? _activityLogService;
    private readonly object _syncRoot = new();
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<string>? _recordingStoppedSource;
    private string? _currentFilePath;
    private bool _discardOnStop;
    private long _recordedByteCount;
    private Exception? _lastRecordingException;
    private bool _lastRecordingEndedWithoutData;
    private bool _firstDataAvailableLogged;
    private int _currentDeviceNumber = -1;
    private string _currentDeviceName = string.Empty;

    public NAudioRecorderService(ApplicationPaths paths, IActivityLogService? activityLogService = null)
    {
        _paths = paths;
        _activityLogService = activityLogService;
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

        if (IsRecording)
        {
            return Task.CompletedTask;
        }

        if (WaveInEvent.DeviceCount <= 0)
        {
            throw new InvalidOperationException("No microphone input device is available.");
        }

        var currentFilePath = Path.Combine(
            _paths.TempDirectory,
            $"recording-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.wav");

        var recordingStoppedSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deviceNumber = ResolveDeviceNumber(settings.PreferredInputDeviceNumber);
        var deviceName = GetDeviceName(deviceNumber);
        var waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
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
            _lastRecordingException = null;
            _lastRecordingEndedWithoutData = false;
            _firstDataAvailableLogged = false;
            _currentDeviceNumber = deviceNumber;
            _currentDeviceName = deviceName;
            _waveIn = waveIn;
            _writer = writer;
        }

        TryWriteDiagnosticLine(
            $"Recorder diagnostics: starting WaveInEvent on device {_currentDeviceNumber} ({_currentDeviceName}); bufferMs={waveIn.BufferMilliseconds}; format={waveIn.WaveFormat.SampleRate}Hz/{waveIn.WaveFormat.BitsPerSample}bit/{waveIn.WaveFormat.Channels}ch; output={currentFilePath}");

        try
        {
            waveIn.StartRecording();
            TryWriteDiagnosticLine("Recorder diagnostics: WaveInEvent.StartRecording succeeded.");
        }
        catch (Exception exception)
        {
            TryWriteDiagnosticLine($"Recorder diagnostics: WaveInEvent.StartRecording failed: {exception.GetType().Name}: {exception.Message}");
            throw;
        }

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
                if (_lastRecordingException is not null)
                {
                    var lastRecordingException = _lastRecordingException;
                    _lastRecordingException = null;
                    throw new InvalidOperationException("Recording stopped unexpectedly.", lastRecordingException);
                }

                if (_lastRecordingEndedWithoutData)
                {
                    _lastRecordingEndedWithoutData = false;
                    return string.Empty;
                }

                return string.Empty;
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
        var totalBytes = Interlocked.Add(ref _recordedByteCount, e.BytesRecorded);

        if (_firstDataAvailableLogged)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_firstDataAvailableLogged)
            {
                return;
            }

            _firstDataAvailableLogged = true;
        }

        TryWriteDiagnosticLine(
            $"Recorder diagnostics: first DataAvailable bytes={e.BytesRecorded}; totalBytes={totalBytes}; device={_currentDeviceNumber} ({_currentDeviceName}).");
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
            _recordingStoppedSource = null;
            currentFilePath = _currentFilePath ?? string.Empty;
            _currentFilePath = null;
            discardOnStop = _discardOnStop;
            recordedByteCount = _recordedByteCount;
            _recordedByteCount = 0;
            _lastRecordingException = null;
            _lastRecordingEndedWithoutData = false;
            _firstDataAvailableLogged = false;
        }

        if (waveIn is not null)
        {
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.RecordingStopped -= OnRecordingStopped;
            waveIn.Dispose();
        }

        writer?.Dispose();

        TryWriteDiagnosticLine(
            $"Recorder diagnostics: RecordingStopped exception={(e.Exception is null ? "null" : $"{e.Exception.GetType().Name}: {e.Exception.Message}")}; recordedBytes={recordedByteCount}; discardOnStop={discardOnStop}; file={currentFilePath}; fileExists={File.Exists(currentFilePath)}.");

        if (e.Exception is not null && !IsBenignStopException(e.Exception))
        {
            lock (_syncRoot)
            {
                _lastRecordingException = e.Exception;
            }

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

            lock (_syncRoot)
            {
                _lastRecordingEndedWithoutData = true;
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

    private static string GetDeviceName(int deviceNumber)
    {
        try
        {
            var capabilities = WaveInEvent.GetCapabilities(deviceNumber);
            return string.IsNullOrWhiteSpace(capabilities.ProductName)
                ? $"Input device {deviceNumber}"
                : capabilities.ProductName.Trim();
        }
        catch
        {
            return $"Input device {deviceNumber}";
        }
    }

    private void TryWriteDiagnosticLine(string message)
    {
        if (_activityLogService is null)
        {
            return;
        }

        _ = WriteDiagnosticLineAsync(message);
    }

    private async Task WriteDiagnosticLineAsync(string message)
    {
        try
        {
            await _activityLogService!.WriteAsync(message).ConfigureAwait(false);
        }
        catch
        {
            // Recorder diagnostics must never crash the app.
        }
    }
}
