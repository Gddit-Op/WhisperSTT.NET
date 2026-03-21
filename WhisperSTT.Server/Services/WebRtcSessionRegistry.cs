using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SIPSorcery.Net;
using WhisperSTT.Core.Contracts;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;
using WhisperSTT.Server.Configuration;

namespace WhisperSTT.Server.Services;

public sealed class WebRtcSessionRegistry
{
    private readonly ConcurrentDictionary<Guid, WebRtcSession> _sessions = new();
    private readonly WhisperServerTranscriptionService _transcriptionService;
    private readonly WhisperModelService _modelService;
    private readonly ServerOptions _serverOptions;
    private readonly WhisperServerTranscriptionOptions _whisperOptions;
    private readonly ApplicationPaths _paths;
    private readonly IActivityLogService _activityLogService;

    public WebRtcSessionRegistry(
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

    public async Task<WebRtcOfferResponse> CreateSessionAsync(
        WebRtcOfferRequest request,
        CancellationToken cancellationToken)
    {
        var session = new WebRtcSession(
            Guid.NewGuid(),
            _transcriptionService,
            _modelService,
            _serverOptions,
            _whisperOptions,
            _paths,
            _activityLogService,
            RemoveSession);

        var response = await session.AcceptOfferAsync(request, cancellationToken).ConfigureAwait(false);
        _sessions[session.Id] = session;
        return response;
    }

    private void RemoveSession(Guid sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.Dispose();
        }
    }

    private sealed class WebRtcSession : IDisposable
    {
        private readonly object _sync = new();
        private readonly WhisperServerTranscriptionService _transcriptionService;
        private readonly WhisperModelService _modelService;
        private readonly ServerOptions _serverOptions;
        private readonly WhisperServerTranscriptionOptions _whisperOptions;
        private readonly ApplicationPaths _paths;
        private readonly IActivityLogService _activityLogService;
        private readonly Action<Guid> _removeSession;
        private PendingTranscriptionRequest? _pendingRequest;
        private RTCPeerConnection? _peerConnection;
        private RTCDataChannel? _dataChannel;
        private bool _disposed;

        public WebRtcSession(
            Guid id,
            WhisperServerTranscriptionService transcriptionService,
            WhisperModelService modelService,
            ServerOptions serverOptions,
            WhisperServerTranscriptionOptions whisperOptions,
            ApplicationPaths paths,
            IActivityLogService activityLogService,
            Action<Guid> removeSession)
        {
            Id = id;
            _transcriptionService = transcriptionService;
            _modelService = modelService;
            _serverOptions = serverOptions;
            _whisperOptions = whisperOptions;
            _paths = paths;
            _activityLogService = activityLogService;
            _removeSession = removeSession;
        }

        public Guid Id { get; }

        public async Task<WebRtcOfferResponse> AcceptOfferAsync(
            WebRtcOfferRequest request,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var peerConnection = new RTCPeerConnection(CreateConfiguration(request.IceServerUrl));
            peerConnection.ondatachannel += AttachDataChannel;
            peerConnection.onconnectionstatechange += state =>
            {
                if (state.ToString().Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                    state.ToString().Equals("closed", StringComparison.OrdinalIgnoreCase) ||
                    state.ToString().Equals("disconnected", StringComparison.OrdinalIgnoreCase))
                {
                    _removeSession(Id);
                }
            };

            var remoteDescriptionResult = peerConnection.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = Enum.Parse<RTCSdpType>(request.Offer.Type, ignoreCase: true),
                sdp = request.Offer.Sdp
            });

            if (!remoteDescriptionResult.ToString().Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                peerConnection.Dispose();
                throw new InvalidOperationException($"Failed to apply incoming SDP offer: {remoteDescriptionResult}.");
            }

            var answer = peerConnection.createAnswer(null);
            await peerConnection.setLocalDescription(answer).ConfigureAwait(false);
            await WaitForIceGatheringCompleteAsync(peerConnection, cancellationToken).ConfigureAwait(false);

            _peerConnection = peerConnection;
            await TryLogAsync($"Accepted WebRTC transcription session {Id}.").ConfigureAwait(false);

            return new WebRtcOfferResponse(
                Id,
                new WebRtcSessionDescription(answer.type.ToString(), answer.sdp));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (_sync)
            {
                _pendingRequest?.Dispose();
                _pendingRequest = null;
            }

            _dataChannel = null;
            _peerConnection?.Close("session disposed");
            _peerConnection?.Dispose();
            _peerConnection = null;
        }

        private void AttachDataChannel(RTCDataChannel dataChannel)
        {
            _dataChannel = dataChannel;
            dataChannel.onmessage += (_, _, data) => _ = HandleMessageAsync(data);
            dataChannel.onclose += () => _removeSession(Id);
            dataChannel.onerror += error => _ = SendFailureAsync(string.Empty, $"WebRTC data channel error: {error}");
        }

        private async Task HandleMessageAsync(byte[] data)
        {
            try
            {
                if (TryDeserializeControlMessage(
                        data,
                        WebRtcServerJsonContext.Default.RemoteTranscriptionStartMessage,
                        out RemoteTranscriptionStartMessage? startMessage) &&
                    startMessage is not null &&
                    startMessage.IsValid)
                {
                    await BeginRequestAsync(startMessage).ConfigureAwait(false);
                    return;
                }

                if (TryDeserializeControlMessage(
                        data,
                        WebRtcServerJsonContext.Default.RemoteTranscriptionEndMessage,
                        out RemoteTranscriptionEndMessage? endMessage) &&
                    endMessage is not null &&
                    endMessage.IsValid)
                {
                    await CompleteRequestAsync(endMessage).ConfigureAwait(false);
                    return;
                }

                AppendChunk(data);
            }
            catch (Exception exception)
            {
                await SendFailureAsync(CurrentRequestId, exception.Message).ConfigureAwait(false);
                ClearPendingRequest();
            }
        }

        private Task BeginRequestAsync(RemoteTranscriptionStartMessage message)
        {
            lock (_sync)
            {
                _pendingRequest?.Dispose();
                _pendingRequest = new PendingTranscriptionRequest(message);
            }

            return Task.CompletedTask;
        }

        private void AppendChunk(byte[] chunk)
        {
            lock (_sync)
            {
                if (_pendingRequest is null)
                {
                    throw new InvalidOperationException("Received binary data without an active transcription request.");
                }

                _pendingRequest.Buffer.Write(chunk, 0, chunk.Length);
            }
        }

        private async Task CompleteRequestAsync(RemoteTranscriptionEndMessage message)
        {
            PendingTranscriptionRequest pendingRequest;
            lock (_sync)
            {
                pendingRequest = _pendingRequest ?? throw new InvalidOperationException("Received request end without a matching transcription request.");
                if (!string.Equals(pendingRequest.Metadata.RequestId, message.RequestId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Request end does not match the active transcription request.");
                }

                _pendingRequest = null;
            }

            using var requestScope = pendingRequest;

            var payload = pendingRequest.Buffer.ToArray();
            if (payload.LongLength != pendingRequest.Metadata.PayloadLength)
            {
                throw new InvalidOperationException(
                    $"Expected {pendingRequest.Metadata.PayloadLength} bytes but received {payload.LongLength}.");
            }

            var tempAudioPath = string.Empty;
            try
            {
                tempAudioPath = await MaterializeAudioAsync(pendingRequest.Metadata, payload).ConfigureAwait(false);
                var modelPath = ResolveModelPath(pendingRequest.Metadata);
                var transcriptionRequest = CreateTranscriptionRequest(
                    pendingRequest.Metadata,
                    tempAudioPath,
                    payload,
                    modelPath,
                    _serverOptions,
                    _whisperOptions);
                var result = await _transcriptionService.TranscribeAsync(transcriptionRequest).ConfigureAwait(false);
                await SendSuccessAsync(pendingRequest.Metadata.RequestId, result).ConfigureAwait(false);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempAudioPath) && File.Exists(tempAudioPath))
                {
                    File.Delete(tempAudioPath);
                }
            }
        }

        private static bool TryDeserializeControlMessage<T>(
            byte[] data,
            JsonTypeInfo<T> jsonTypeInfo,
            out T? message)
        {
            message = default;
            if (data.Length == 0 || data[0] != (byte)'{')
            {
                return false;
            }

            try
            {
                message = JsonSerializer.Deserialize(data, jsonTypeInfo);
                return message is not null;
            }
            catch (JsonException)
            {
                return false;
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
            byte[] payload)
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
            await File.WriteAllBytesAsync(tempFilePath, payload).ConfigureAwait(false);
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

        private async Task SendSuccessAsync(string requestId, TranscriptionResult result)
        {
            await SendMessageAsync(new RemoteTranscriptionResultMessage(
                WebRtcProtocolConstants.TranscriptionResultMessageType,
                requestId,
                true,
                result)).ConfigureAwait(false);
        }

        private async Task SendFailureAsync(string requestId, string message)
        {
            await SendMessageAsync(new RemoteTranscriptionResultMessage(
                WebRtcProtocolConstants.TranscriptionResultMessageType,
                requestId,
                false,
                null,
                message)).ConfigureAwait(false);
        }

        private Task SendMessageAsync(RemoteTranscriptionResultMessage message)
        {
            var json = JsonSerializer.Serialize(
                message,
                WebRtcServerJsonContext.Default.RemoteTranscriptionResultMessage);
            _dataChannel?.send(json);
            return Task.CompletedTask;
        }

        private void ClearPendingRequest()
        {
            lock (_sync)
            {
                _pendingRequest?.Dispose();
                _pendingRequest = null;
            }
        }

        private string CurrentRequestId
        {
            get
            {
                lock (_sync)
                {
                    return _pendingRequest?.Metadata.RequestId ?? string.Empty;
                }
            }
        }

        private static RTCConfiguration CreateConfiguration(string? iceServerUrl)
        {
            if (string.IsNullOrWhiteSpace(iceServerUrl))
            {
                return new RTCConfiguration();
            }

            return new RTCConfiguration
            {
                iceServers =
                [
                    new RTCIceServer
                    {
                        urls = iceServerUrl
                    }
                ]
            };
        }

        private static async Task WaitForIceGatheringCompleteAsync(
            RTCPeerConnection peerConnection,
            CancellationToken cancellationToken)
        {
            if (peerConnection.iceGatheringState.ToString().Equals("complete", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStateChanged(RTCIceGatheringState state)
            {
                if (state.ToString().Equals("complete", StringComparison.OrdinalIgnoreCase))
                {
                    source.TrySetResult(true);
                }
            }

            peerConnection.onicegatheringstatechange += OnStateChanged;
            using var registration = cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));

            try
            {
                await source.Task.ConfigureAwait(false);
            }
            finally
            {
                peerConnection.onicegatheringstatechange -= OnStateChanged;
            }
        }

        private Task TryLogAsync(string message)
        {
            return _activityLogService.WriteAsync(message);
        }

        private sealed class PendingTranscriptionRequest : IDisposable, IAsyncDisposable
        {
            public PendingTranscriptionRequest(RemoteTranscriptionStartMessage metadata)
            {
                Metadata = metadata;
                Buffer = metadata.PayloadLength > 0 && metadata.PayloadLength <= int.MaxValue
                    ? new MemoryStream((int)metadata.PayloadLength)
                    : new MemoryStream();
            }

            public RemoteTranscriptionStartMessage Metadata { get; }

            public MemoryStream Buffer { get; }

            public void Dispose()
            {
                Buffer.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                Buffer.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
