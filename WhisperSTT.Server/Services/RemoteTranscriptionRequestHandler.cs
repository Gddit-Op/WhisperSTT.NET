using WhisperSTT.Core.Contracts;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;
using WhisperSTT.Server.Configuration;

namespace WhisperSTT.Server.Services;

public sealed class RemoteTranscriptionRequestHandler
{
    private readonly WhisperServerTranscriptionService _transcriptionService;
    private readonly WhisperModelService _modelService;
    private readonly ServerOptions _serverOptions;
    private readonly WhisperServerTranscriptionOptions _whisperOptions;
    private readonly ApplicationPaths _paths;
    private readonly IActivityLogService _activityLogService;

    public RemoteTranscriptionRequestHandler(
        WhisperServerTranscriptionService transcriptionService,
        WhisperModelService modelService,
        ServerOptions serverOptions,
        WhisperServerTranscriptionOptions whisperOptions,
        ApplicationPaths paths,
        IActivityLogService activityLogService)
    {
        _transcriptionService = transcriptionService;
        _modelService = modelService;
        _serverOptions = serverOptions;
        _whisperOptions = whisperOptions;
        _paths = paths;
        _activityLogService = activityLogService;
    }

    public async Task<RemoteTranscriptionResultMessage> ProcessAsync(
        RemoteTranscriptionStartMessage metadata,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _activityLogService
                .WriteAsync(
                    $"Received remote transcription request {metadata.RequestId} ({metadata.SourceType}, {metadata.AudioFormat}, {payload.LongLength} bytes).",
                    cancellationToken)
                .ConfigureAwait(false);

            if (payload.LongLength != metadata.PayloadLength)
            {
                throw new InvalidOperationException(
                    $"Expected {metadata.PayloadLength} bytes but received {payload.LongLength}.");
            }

            var tempAudioPath = string.Empty;
            try
            {
                tempAudioPath = await MaterializeAudioAsync(metadata, payload, cancellationToken).ConfigureAwait(false);
                var modelPath = ResolveModelPath(metadata);
                await _activityLogService
                    .WriteAsync(
                        $"Starting transcription for request {metadata.RequestId}. ModelPath={modelPath}; PayloadBytes={payload.LongLength}; SourceType={metadata.SourceType}; AudioFormat={metadata.AudioFormat}.",
                        cancellationToken)
                    .ConfigureAwait(false);

                var transcriptionRequest = CreateTranscriptionRequest(
                    metadata,
                    tempAudioPath,
                    payload,
                    modelPath,
                    _serverOptions,
                    _whisperOptions);
                var result = await _transcriptionService.TranscribeAsync(transcriptionRequest, cancellationToken).ConfigureAwait(false);

                await _activityLogService
                    .WriteAsync(
                        $"Completed transcription for request {metadata.RequestId}. TextLength={result.Text.Length}; DetectedLanguage={result.DetectedLanguage ?? "unknown"}.",
                        cancellationToken)
                    .ConfigureAwait(false);

                return new RemoteTranscriptionResultMessage(
                    WebRtcProtocolConstants.TranscriptionResultMessageType,
                    metadata.RequestId,
                    true,
                    result);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempAudioPath) && File.Exists(tempAudioPath))
                {
                    File.Delete(tempAudioPath);
                }
            }
        }
        catch (Exception exception)
        {
            await _activityLogService
                .WriteAsync(
                    $"Remote transcription request {metadata.RequestId} failed: {exception.GetType().Name}: {exception.Message}",
                    cancellationToken)
                .ConfigureAwait(false);

            return new RemoteTranscriptionResultMessage(
                WebRtcProtocolConstants.TranscriptionResultMessageType,
                metadata.RequestId,
                false,
                null,
                exception.Message);
        }
    }

    private string ResolveModelPath(RemoteTranscriptionStartMessage metadata)
    {
        if (_serverOptions.PreferServerWhisperConfiguration)
        {
            return _modelService.ResolveModelPath(_whisperOptions.GetModelPreset(metadata.SourceType));
        }

        if (!string.IsNullOrWhiteSpace(metadata.PreferredModelPath) &&
            File.Exists(metadata.PreferredModelPath))
        {
            return metadata.PreferredModelPath;
        }

        return _modelService.ResolveModelPath(metadata.RequestedModelPreset);
    }

    private async Task<string> MaterializeAudioAsync(
        RemoteTranscriptionStartMessage metadata,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (metadata.AudioFormat == RemoteTranscriptionAudioFormat.Float32Samples)
        {
            return string.Empty;
        }

        var extension = Path.GetExtension(metadata.FileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".wav";
        }

        var tempFilePath = Path.Combine(_paths.TempDirectory, $"{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(tempFilePath, payload, cancellationToken).ConfigureAwait(false);
        return tempFilePath;
    }

    private static TranscriptionRequest CreateTranscriptionRequest(
        RemoteTranscriptionStartMessage metadata,
        string tempAudioPath,
        byte[] payload,
        string modelPath,
        ServerOptions serverOptions,
        WhisperServerTranscriptionOptions whisperOptions)
    {
        var useServerSettings = serverOptions.PreferServerWhisperConfiguration;
        var languageMode = useServerSettings ? whisperOptions.LanguageMode : metadata.LanguageMode;
        var runtimePreference = useServerSettings ? whisperOptions.RuntimePreference : metadata.RuntimePreference;
        var openVinoRuntimePath = useServerSettings ? whisperOptions.OpenVinoRuntimePath : metadata.OpenVinoRuntimePath;
        var threadCount = useServerSettings ? whisperOptions.GetThreadCount(metadata.SourceType) : metadata.ThreadCount;
        var modelPreset = useServerSettings ? whisperOptions.GetModelPreset(metadata.SourceType) : metadata.RequestedModelPreset;

        if (metadata.AudioFormat == RemoteTranscriptionAudioFormat.Float32Samples)
        {
            if (payload.Length % sizeof(float) != 0)
            {
                throw new InvalidOperationException("Float32 sample payload length is invalid.");
            }

            var samples = new float[payload.Length / sizeof(float)];
            Buffer.BlockCopy(payload, 0, samples, 0, payload.Length);

            return new TranscriptionRequest(
                string.Empty,
                modelPath,
                languageMode,
                threadCount,
                runtimePreference,
                openVinoRuntimePath,
                metadata.IsLivePreview,
                metadata.EnableDiagnosticLogging,
                samples,
                metadata.SampleRate,
                metadata.Channels,
                modelPreset,
                metadata.SourceType);
        }

        return new TranscriptionRequest(
            tempAudioPath,
            modelPath,
            languageMode,
            threadCount,
            runtimePreference,
            openVinoRuntimePath,
            metadata.IsLivePreview,
            metadata.EnableDiagnosticLogging,
            null,
            0,
            0,
            modelPreset,
            metadata.SourceType);
    }
}
