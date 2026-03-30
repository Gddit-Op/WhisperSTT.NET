using System.Buffers;
using System.IO;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Components;
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
    private Recorder? _recorder;
    private TaskCompletionSource<RecordedAudioCapture?>? _recordingStoppedSource;
    private ArrayBufferWriter<float>? _recordedSamples;
    private string? _currentFilePath;
    private bool _recordToMemory;
    private bool _discardOnStop;
    private long _recordedSampleCount;
    private Exception? _lastRecordingException;
    private bool _lastRecordingEndedWithoutData;
    private bool _firstDataAvailableLogged;
    private int _currentDeviceNumber = -1;
    private int _currentSampleRate;
    private int _currentChannels;
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

    public event EventHandler<AudioLevelChangedEventArgs>? AudioLevelChanged;

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

        var recordToMemory = settings.TranscribeMicrophoneDirectlyFromMemory;
        var currentFilePath = recordToMemory
            ? null
            : Path.Combine(
                _paths.TempDirectory,
                $"recording-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.wav");

        var recordingStoppedSource = new TaskCompletionSource<RecordedAudioCapture?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deviceNumber = ResolveDeviceNumber(settings.PreferredInputDeviceNumber, captureDevices.Length);
        var deviceInfo = captureDevices[deviceNumber];
        var audioFormat = ResolveCaptureFormat(deviceInfo);

        var captureDevice = _audioEngine.InitializeCaptureDevice(deviceInfo, audioFormat, null);
        Recorder? recorder = null;
        if (!recordToMemory)
        {
            recorder = new Recorder(captureDevice, currentFilePath!, "wav");
            var startResult = recorder.StartRecording();
            if (startResult.IsFailure)
            {
                recorder.Dispose();
                captureDevice.Dispose();
                throw CreateRecorderException("SoundFlow recorder initialization failed.", startResult);
            }
        }

        lock (_syncRoot)
        {
            _currentFilePath = currentFilePath;
            _recordingStoppedSource = recordingStoppedSource;
            _recordToMemory = recordToMemory;
            _discardOnStop = false;
            _recordedSampleCount = 0;
            _recordedSamples = recordToMemory
                ? new ArrayBufferWriter<float>(Math.Max(audioFormat.SampleRate * audioFormat.Channels * 10, 16384))
                : null;
            _lastRecordingException = null;
            _lastRecordingEndedWithoutData = false;
            _firstDataAvailableLogged = false;
            _currentDeviceNumber = deviceNumber;
            _currentSampleRate = audioFormat.SampleRate;
            _currentChannels = audioFormat.Channels;
            _currentDeviceName = string.IsNullOrWhiteSpace(deviceInfo.Name) ? $"Input device {deviceNumber}" : deviceInfo.Name;
            _captureDevice = captureDevice;
            _recorder = recorder;
        }

        TryWriteDiagnosticLine(
            $"Recorder diagnostics: starting SoundFlow capture on device {_currentDeviceNumber} ({_currentDeviceName}); format={audioFormat.SampleRate}Hz/{audioFormat.Format}/{audioFormat.Channels}ch; output={(recordToMemory ? "<memory>" : currentFilePath)}; directMemory={recordToMemory}.");

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

    public async Task<RecordedAudioCapture?> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        Task<RecordedAudioCapture?>? completionTask;

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
                    return null;
                }

                return null;
            }

            _discardOnStop = false;
            completionTask = _recordingStoppedSource.Task;
        }

        await FinalizeRecordingAsync(cancellationToken).ConfigureAwait(false);
        return await completionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelRecordingAsync(CancellationToken cancellationToken = default)
    {
        Task<RecordedAudioCapture?>? completionTask;

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

    private async Task FinalizeRecordingAsync(CancellationToken cancellationToken)
    {
        AudioCaptureDevice? captureDevice;
        Recorder? recorder;
        TaskCompletionSource<RecordedAudioCapture?>? recordingStoppedSource;
        ArrayBufferWriter<float>? recordedSamples;
        string currentFilePath;
        bool recordToMemory;
        bool discardOnStop;
        long recordedSampleCount;
        Exception? recordingException;
        int currentSampleRate;
        int currentChannels;

        lock (_syncRoot)
        {
            captureDevice = _captureDevice;
            _captureDevice = null;
            recorder = _recorder;
            _recorder = null;
            recordedSamples = _recordedSamples;
            _recordedSamples = null;
            recordingStoppedSource = _recordingStoppedSource;
            _recordingStoppedSource = null;
            currentFilePath = _currentFilePath ?? string.Empty;
            _currentFilePath = null;
            recordToMemory = _recordToMemory;
            _recordToMemory = false;
            discardOnStop = _discardOnStop;
            recordedSampleCount = _recordedSampleCount;
            _recordedSampleCount = 0;
            currentSampleRate = _currentSampleRate;
            _currentSampleRate = 0;
            currentChannels = _currentChannels;
            _currentChannels = 0;
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

            if (recorder is not null)
            {
                var stopResult = await recorder.StopRecordingAsync().ConfigureAwait(false);
                if (stopResult.IsFailure)
                {
                    recordingException ??= CreateRecorderException("SoundFlow recorder finalization failed.", stopResult);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            recordingException ??= exception;
        }
        finally
        {
            recorder?.Dispose();
            captureDevice?.Dispose();
        }

        cancellationToken.ThrowIfCancellationRequested();

        var recordedFileLength = !string.IsNullOrWhiteSpace(currentFilePath) && File.Exists(currentFilePath)
            ? new FileInfo(currentFilePath).Length
            : 0L;

        TryWriteDiagnosticLine(
            $"Recorder diagnostics: SoundFlow stop exception={(recordingException is null ? "null" : $"{recordingException.GetType().Name}: {recordingException.Message}")}; recordedSamples={recordedSampleCount}; fileBytes={recordedFileLength}; discardOnStop={discardOnStop}; file={currentFilePath}; fileExists={File.Exists(currentFilePath)}; directMemory={recordToMemory}; sampleRate={currentSampleRate}; channels={currentChannels}.");

        if (recordingException is not null)
        {
            lock (_syncRoot)
            {
                _lastRecordingException = recordingException;
            }

            recordingStoppedSource?.TrySetException(recordingException);
            return;
        }

        if (discardOnStop)
        {
            if (!string.IsNullOrWhiteSpace(currentFilePath) && File.Exists(currentFilePath))
            {
                File.Delete(currentFilePath);
            }

            recordingStoppedSource?.TrySetResult(null);
            return;
        }

        if (recordToMemory)
        {
            if (recordedSampleCount <= 0 || recordedSamples is null || recordedSamples.WrittenCount <= 0)
            {
                lock (_syncRoot)
                {
                    _lastRecordingEndedWithoutData = true;
                }

                recordingStoppedSource?.TrySetResult(null);
                return;
            }

            recordingStoppedSource?.TrySetResult(new RecordedAudioCapture(
                AudioFilePath: null,
                AudioSamples: recordedSamples.WrittenMemory.ToArray(),
                SampleRate: currentSampleRate,
                Channels: currentChannels));
            return;
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

            recordingStoppedSource?.TrySetResult(null);
            return;
        }

        recordingStoppedSource?.TrySetResult(new RecordedAudioCapture(
            AudioFilePath: currentFilePath,
            AudioSamples: null,
            SampleRate: currentSampleRate,
            Channels: currentChannels));
    }

    private void OnAudioProcessed(Span<float> samples, Capability capability)
    {
        if (capability is not Capability.Record and not Capability.Mixed)
        {
            return;
        }

        Recorder? recorder;
        ArrayBufferWriter<float>? recordedSamples;
        bool recordToMemory;
        lock (_syncRoot)
        {
            recorder = _recorder;
            recordedSamples = _recordedSamples;
            recordToMemory = _recordToMemory;
        }

        if (recorder is null && !recordToMemory)
        {
            return;
        }

        var audioLevel = ComputeAverageAmplitude(samples);
        AudioLevelChanged?.Invoke(this, new AudioLevelChangedEventArgs(audioLevel));

        if (recordToMemory && recordedSamples is not null)
        {
            lock (_syncRoot)
            {
                if (_recordedSamples is not null)
                {
                    samples.CopyTo(_recordedSamples.GetSpan(samples.Length));
                    _recordedSamples.Advance(samples.Length);
                }
            }
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

        TryWriteDiagnosticLine(
            $"Recorder diagnostics: first SoundFlow samples={samples.Length}; totalSamples={totalSamples}; avgAmplitude={audioLevel:F6}; device={_currentDeviceNumber} ({_currentDeviceName}).");
    }

    private void CleanupActiveRecordingState(bool disposeTask)
    {
        AudioCaptureDevice? captureDevice;
        Recorder? recorder;

        lock (_syncRoot)
        {
            captureDevice = _captureDevice;
            _captureDevice = null;
            recorder = _recorder;
            _recorder = null;
            _recordedSamples = null;
            _recordingStoppedSource = null;
            _currentFilePath = null;
            _recordToMemory = false;
            _discardOnStop = false;
            _recordedSampleCount = 0;
            _currentSampleRate = 0;
            _currentChannels = 0;
            _lastRecordingException = null;
            _lastRecordingEndedWithoutData = false;
            _firstDataAvailableLogged = false;
        }

        AudioLevelChanged?.Invoke(this, new AudioLevelChangedEventArgs(0f));

        if (disposeTask)
        {
            try
            {
                if (captureDevice is not null)
                {
                    captureDevice.OnAudioProcessed -= OnAudioProcessed;
                }

                captureDevice?.Stop();
                recorder?.StopRecording();
            }
            catch
            {
                // Cleanup path.
            }
        }

        recorder?.Dispose();
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

    private static AudioFormat ResolveCaptureFormat(DeviceInfo deviceInfo)
    {
        var supportedFormats = deviceInfo.SupportedDataFormats;
        if (supportedFormats is not null && supportedFormats.Length > 0)
        {
            var bestFormat = supportedFormats[0];
            var bestScore = ScoreNativeFormat(bestFormat);
            for (var formatIndex = 1; formatIndex < supportedFormats.Length; formatIndex++)
            {
                var candidate = supportedFormats[formatIndex];
                var candidateScore = ScoreNativeFormat(candidate);
                if (candidateScore > bestScore)
                {
                    bestFormat = candidate;
                    bestScore = candidateScore;
                }
            }

            if (bestFormat.SampleRate > 0 &&
                bestFormat.Channels > 0 &&
                bestFormat.Format != SampleFormat.Unknown)
            {
                var channels = checked((int)bestFormat.Channels);
                var sampleRate = checked((int)bestFormat.SampleRate);
                return new AudioFormat
                {
                    Format = bestFormat.Format,
                    Channels = channels,
                    Layout = AudioFormat.GetLayoutFromChannels(channels),
                    SampleRate = sampleRate
                };
            }
        }

        return new AudioFormat
        {
            Format = SampleFormat.F32,
            Channels = 1,
            Layout = AudioFormat.GetLayoutFromChannels(1),
            SampleRate = 48000
        };
    }

    private static int ScoreNativeFormat(NativeDataFormat format)
    {
        if (format.SampleRate <= 0 || format.Channels <= 0 || format.Format == SampleFormat.Unknown)
        {
            return int.MinValue;
        }

        var channels = checked((int)format.Channels);
        var sampleRate = checked((int)format.SampleRate);

        var formatScore = format.Format switch
        {
            SampleFormat.F32 => 400000,
            SampleFormat.S32 => 300000,
            SampleFormat.S24 => 250000,
            SampleFormat.S16 => 200000,
            SampleFormat.U8 => 100000,
            _ => 0
        };

        var channelScore = channels switch
        {
            1 => 40000,
            2 => 30000,
            _ => Math.Max(0, 20000 - (channels * 1000))
        };

        var sampleRateScore = sampleRate switch
        {
            48000 => 10000,
            44100 => 9000,
            32000 => 8000,
            24000 => 7000,
            16000 => 6000,
            _ when sampleRate > 0 => Math.Min(sampleRate / 10, 5000),
            _ => 0
        };

        return formatScore + channelScore + sampleRateScore;
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

    private static InvalidOperationException CreateRecorderException(string message, Result result)
    {
        return new InvalidOperationException($"{message} {result.Error?.ToString() ?? "Unknown recorder error."}");
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
