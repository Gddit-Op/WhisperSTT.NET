using System.IO;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Enums;
using SoundFlow.Structs;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.Services;

public sealed class SoundFlowRecorderService : IAudioRecorderService, IDisposable
{
    private const int WaveHeaderSizeBytes = 44;
    private readonly ApplicationPaths _paths;
    private readonly IActivityLogService? _activityLogService;
    private readonly object _syncRoot = new();
    private readonly AudioEngine _audioEngine;
    private AudioCaptureDevice? _captureDevice;
    private WaveFileUtility.Pcm16WaveFileWriter? _writer;
    private TaskCompletionSource<string>? _recordingStoppedSource;
    private string? _currentFilePath;
    private bool _discardOnStop;
    private long _recordedSampleCount;
    private Exception? _lastRecordingException;
    private bool _lastRecordingEndedWithoutData;
    private bool _firstDataAvailableLogged;
    private int _currentDeviceNumber = -1;
    private string _currentDeviceName = string.Empty;

    public SoundFlowRecorderService(ApplicationPaths paths, IActivityLogService? activityLogService = null)
    {
        _paths = paths;
        _activityLogService = activityLogService;
        _paths.EnsureCreated();
        _audioEngine = new MiniAudioEngine(Array.Empty<MiniAudioBackend>());
    }

    public bool IsRecording
    {
        get
        {
            lock (_syncRoot)
            {
                return _captureDevice is not null;
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

        var captureDevices = _audioEngine.CaptureDevices;
        if (captureDevices.Length == 0)
        {
            throw new InvalidOperationException("No microphone input device is available.");
        }

        var currentFilePath = Path.Combine(
            _paths.TempDirectory,
            $"recording-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.wav");

        var recordingStoppedSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deviceNumber = ResolveDeviceNumber(settings.PreferredInputDeviceNumber, captureDevices.Length);
        var deviceInfo = captureDevices[deviceNumber];
        var audioFormat = new AudioFormat
        {
            Format = SampleFormat.F32,
            Channels = 1,
            Layout = AudioFormat.GetLayoutFromChannels(1),
            SampleRate = 16000
        };

        var captureDevice = _audioEngine.InitializeCaptureDevice(deviceInfo, audioFormat, null);
        var writer = WaveFileUtility.CreatePcm16Writer(currentFilePath, sampleRate: 16000, channels: 1);

        lock (_syncRoot)
        {
            _currentFilePath = currentFilePath;
            _recordingStoppedSource = recordingStoppedSource;
            _discardOnStop = false;
            _recordedSampleCount = 0;
            _lastRecordingException = null;
            _lastRecordingEndedWithoutData = false;
            _firstDataAvailableLogged = false;
            _currentDeviceNumber = deviceNumber;
            _currentDeviceName = string.IsNullOrWhiteSpace(deviceInfo.Name) ? $"Input device {deviceNumber}" : deviceInfo.Name;
            _captureDevice = captureDevice;
            _writer = writer;
        }

        TryWriteDiagnosticLine(
            $"Recorder diagnostics: starting SoundFlow capture on device {_currentDeviceNumber} ({_currentDeviceName}); format={audioFormat.SampleRate}Hz/{audioFormat.Format}/{audioFormat.Channels}ch; output={currentFilePath}");

        try
        {
            captureDevice.OnAudioProcessed += OnAudioProcessed;
            captureDevice.Start();
            TryWriteDiagnosticLine("Recorder diagnostics: SoundFlow capture started successfully.");
        }
        catch (Exception exception)
        {
            TryWriteDiagnosticLine($"Recorder diagnostics: SoundFlow start failed: {exception.GetType().Name}: {exception.Message}");
            CleanupActiveRecordingState(disposeTask: true);
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task<string> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        Task<string>? completionTask;

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
        }

        await FinalizeRecordingAsync(cancellationToken).ConfigureAwait(false);
        return await completionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelRecordingAsync(CancellationToken cancellationToken = default)
    {
        Task<string>? completionTask;

        lock (_syncRoot)
        {
            if (_recordingStoppedSource is null)
            {
                return;
            }

            _discardOnStop = true;
            completionTask = _recordingStoppedSource.Task;
        }

        await FinalizeRecordingAsync(cancellationToken).ConfigureAwait(false);
        _ = await completionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        CleanupActiveRecordingState(disposeTask: true);
        _audioEngine.Dispose();
    }

    private Task FinalizeRecordingAsync(CancellationToken cancellationToken)
    {
        AudioCaptureDevice? captureDevice;
        WaveFileUtility.Pcm16WaveFileWriter? writer;
        TaskCompletionSource<string>? recordingStoppedSource;
        string currentFilePath;
        bool discardOnStop;
        long recordedSampleCount;
        Exception? recordingException;

        lock (_syncRoot)
        {
            captureDevice = _captureDevice;
            _captureDevice = null;
            writer = _writer;
            _writer = null;
            recordingStoppedSource = _recordingStoppedSource;
            _recordingStoppedSource = null;
            currentFilePath = _currentFilePath ?? string.Empty;
            _currentFilePath = null;
            discardOnStop = _discardOnStop;
            recordedSampleCount = _recordedSampleCount;
            _recordedSampleCount = 0;
            recordingException = _lastRecordingException;
            _lastRecordingException = null;
            _lastRecordingEndedWithoutData = false;
            _firstDataAvailableLogged = false;
        }

        try
        {
            if (captureDevice is not null)
            {
                captureDevice.OnAudioProcessed -= OnAudioProcessed;
            }
            captureDevice?.Stop();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            recordingException ??= exception;
        }
        finally
        {
            writer?.Dispose();
            captureDevice?.Dispose();
        }

        cancellationToken.ThrowIfCancellationRequested();

        var recordedFileLength = !string.IsNullOrWhiteSpace(currentFilePath) && File.Exists(currentFilePath)
            ? new FileInfo(currentFilePath).Length
            : 0L;

        TryWriteDiagnosticLine(
            $"Recorder diagnostics: SoundFlow stop exception={(recordingException is null ? "null" : $"{recordingException.GetType().Name}: {recordingException.Message}")}; recordedSamples={recordedSampleCount}; fileBytes={recordedFileLength}; discardOnStop={discardOnStop}; file={currentFilePath}; fileExists={File.Exists(currentFilePath)}.");

        if (recordingException is not null)
        {
            lock (_syncRoot)
            {
                _lastRecordingException = recordingException;
            }

            recordingStoppedSource?.TrySetException(recordingException);
            return Task.CompletedTask;
        }

        if (discardOnStop)
        {
            if (!string.IsNullOrWhiteSpace(currentFilePath) && File.Exists(currentFilePath))
            {
                File.Delete(currentFilePath);
            }

            recordingStoppedSource?.TrySetResult(string.Empty);
            return Task.CompletedTask;
        }

        if (!HasUsableAudioData(currentFilePath, recordedSampleCount, recordedFileLength))
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
            return Task.CompletedTask;
        }

        recordingStoppedSource?.TrySetResult(currentFilePath);
        return Task.CompletedTask;
    }

    private void OnAudioProcessed(Span<float> samples, Capability capability)
    {
        if (capability is not Capability.Record and not Capability.Mixed)
        {
            return;
        }

        WaveFileUtility.Pcm16WaveFileWriter? writer;
        lock (_syncRoot)
        {
            writer = _writer;
        }

        if (writer is null)
        {
            return;
        }

        lock (_syncRoot)
        {
            _writer?.WriteSamples(samples);
        }

        var totalSamples = Interlocked.Add(ref _recordedSampleCount, samples.Length);

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

        var averageAmplitude = ComputeAverageAmplitude(samples);
        TryWriteDiagnosticLine(
            $"Recorder diagnostics: first SoundFlow samples={samples.Length}; totalSamples={totalSamples}; avgAmplitude={averageAmplitude:F6}; device={_currentDeviceNumber} ({_currentDeviceName}).");
    }

    private void CleanupActiveRecordingState(bool disposeTask)
    {
        AudioCaptureDevice? captureDevice;
        WaveFileUtility.Pcm16WaveFileWriter? writer;

        lock (_syncRoot)
        {
            captureDevice = _captureDevice;
            _captureDevice = null;
            writer = _writer;
            _writer = null;
            _recordingStoppedSource = null;
            _currentFilePath = null;
            _discardOnStop = false;
            _recordedSampleCount = 0;
            _lastRecordingException = null;
            _lastRecordingEndedWithoutData = false;
            _firstDataAvailableLogged = false;
        }

        if (disposeTask)
        {
            try
            {
                if (captureDevice is not null)
                {
                    captureDevice.OnAudioProcessed -= OnAudioProcessed;
                }

                captureDevice?.Stop();
            }
            catch
            {
                // Cleanup path.
            }
        }

        writer?.Dispose();
        captureDevice?.Dispose();
    }

    private static bool HasUsableAudioData(string filePath, long recordedSampleCount, long fileLength)
    {
        if (recordedSampleCount > 0 && fileLength > WaveHeaderSizeBytes)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        return new FileInfo(filePath).Length > WaveHeaderSizeBytes;
    }

    private static int ResolveDeviceNumber(int? preferredDeviceNumber, int deviceCount)
    {
        if (preferredDeviceNumber is >= 0 &&
            preferredDeviceNumber.Value < deviceCount)
        {
            return preferredDeviceNumber.Value;
        }

        return 0;
    }

    private static float ComputeAverageAmplitude(ReadOnlySpan<float> samples)
    {
        if (samples.Length <= 0)
        {
            return 0f;
        }

        var amplitudeSum = 0f;
        for (var sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
        {
            amplitudeSum += MathF.Abs(samples[sampleIndex]);
        }

        return amplitudeSum / samples.Length;
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
