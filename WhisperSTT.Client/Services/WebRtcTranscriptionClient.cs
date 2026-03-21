using System.Net.Http.Json;
using System.Text.Json;
using SIPSorcery.Net;
using WhisperSTT.Core.Contracts;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;

namespace WhisperSTT.Client.Services;

public sealed class WebRtcTranscriptionClient : ITranscriptionService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IActivityLogService? _activityLogService;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _sync = new();
    private TaskCompletionSource<bool>? _dataChannelOpenSource;
    private TaskCompletionSource<RemoteTranscriptionResultMessage>? _responseSource;
    private RTCPeerConnection? _peerConnection;
    private RTCDataChannel? _dataChannel;
    private string _remoteServerUrl = string.Empty;
    private string _iceServerUrl = string.Empty;
    private bool _disposed;

    public WebRtcTranscriptionClient(HttpClient httpClient, IActivityLogService? activityLogService = null)
    {
        _httpClient = httpClient;
        _activityLogService = activityLogService;
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(request.RemoteServerUrl))
        {
            throw new InvalidOperationException("Remote server URL is required for WebRTC transcription.");
        }

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(request, cancellationToken).ConfigureAwait(false);

            var payload = await LoadPayloadAsync(request, cancellationToken).ConfigureAwait(false);
            var requestId = Guid.NewGuid().ToString("N");
            var responseSource = new TaskCompletionSource<RemoteTranscriptionResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_sync)
            {
                _responseSource = responseSource;
            }

            var startMessage = new RemoteTranscriptionStartMessage(
                WebRtcProtocolConstants.TranscriptionStartMessageType,
                requestId,
                request.SourceType,
                payload.AudioFormat,
                payload.FileName,
                payload.Bytes.LongLength,
                request.ModelPath,
                request.RequestedModelPreset,
                request.LanguageMode,
                Math.Max(1, request.ThreadCount),
                request.RuntimePreference,
                request.OpenVinoRuntimePath,
                request.IsLivePreview,
                request.EnableDiagnosticLogging,
                request.AudioSampleRate,
                request.AudioChannels);

            SendJson(startMessage);

            foreach (var chunk in Chunk(payload.Bytes, WebRtcProtocolConstants.DefaultChunkSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                GetRequiredDataChannel().send(chunk);
            }

            SendJson(new RemoteTranscriptionEndMessage(
                WebRtcProtocolConstants.TranscriptionEndMessageType,
                requestId));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, request.RemoteTimeoutSeconds)));
            var response = await WaitForResponseAsync(responseSource.Task, timeoutCts.Token).ConfigureAwait(false);
            if (!response.Success || response.Result is null)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.ErrorMessage)
                    ? "The remote server did not return a transcription result."
                    : response.ErrorMessage);
            }

            return response.Result;
        }
        catch
        {
            await ResetConnectionAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_sync)
            {
                _responseSource = null;
            }

            _requestGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _requestGate.Dispose();
        DisposeConnection();
    }

    private async Task EnsureConnectedAsync(TranscriptionRequest request, CancellationToken cancellationToken)
    {
        var requiresReconnect =
            _peerConnection is null ||
            _dataChannel is null ||
            !_dataChannel.readyState.ToString().Equals("open", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_remoteServerUrl, request.RemoteServerUrl, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_iceServerUrl, request.WebRtcIceServerUrl, StringComparison.OrdinalIgnoreCase);

        if (!requiresReconnect)
        {
            return;
        }

        await ResetConnectionAsync().ConfigureAwait(false);

        var peerConnection = new RTCPeerConnection(CreateConfiguration(request.WebRtcIceServerUrl));
        var openSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dataChannel = await peerConnection.createDataChannel(WebRtcProtocolConstants.DefaultChannelLabel).ConfigureAwait(false);

        AttachPeerConnection(peerConnection, openSource);
        AttachDataChannel(dataChannel, openSource);

        var offer = peerConnection.createOffer(null);
        await peerConnection.setLocalDescription(offer).ConfigureAwait(false);
        await WaitForIceGatheringCompleteAsync(peerConnection, cancellationToken).ConfigureAwait(false);

        var endpoint = BuildSessionEndpoint(request.RemoteServerUrl);
        var offerResponse = await PostOfferAsync(endpoint, new WebRtcOfferRequest(
            new WebRtcSessionDescription(offer.type.ToString(), offer.sdp),
            request.WebRtcIceServerUrl)).ConfigureAwait(false);

        var answer = new RTCSessionDescriptionInit
        {
            type = ParseSdpType(offerResponse.Answer.Type),
            sdp = offerResponse.Answer.Sdp
        };

        var remoteDescriptionResult = peerConnection.setRemoteDescription(answer);
        if (!remoteDescriptionResult.ToString().Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Failed to apply remote SDP answer: {remoteDescriptionResult}.");
        }

        using var channelTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        channelTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, request.RemoteTimeoutSeconds)));
        await WaitForOpenAsync(openSource.Task, channelTimeout.Token).ConfigureAwait(false);

        lock (_sync)
        {
            _peerConnection = peerConnection;
            _dataChannel = dataChannel;
            _dataChannelOpenSource = openSource;
            _remoteServerUrl = request.RemoteServerUrl;
            _iceServerUrl = request.WebRtcIceServerUrl;
        }

        await TryLogAsync($"Connected WebRTC transcription client to {request.RemoteServerUrl}.").ConfigureAwait(false);
    }

    private void AttachPeerConnection(RTCPeerConnection peerConnection, TaskCompletionSource<bool> openSource)
    {
        peerConnection.onconnectionstatechange += state =>
        {
            if (state.ToString().Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                state.ToString().Equals("closed", StringComparison.OrdinalIgnoreCase) ||
                state.ToString().Equals("disconnected", StringComparison.OrdinalIgnoreCase))
            {
                openSource.TrySetException(new InvalidOperationException($"WebRTC connection closed: {state}."));
            }
        };

        peerConnection.ondatachannel += channel => AttachDataChannel(channel, openSource);
    }

    private void AttachDataChannel(RTCDataChannel dataChannel, TaskCompletionSource<bool> openSource)
    {
        dataChannel.onopen += () => openSource.TrySetResult(true);
        dataChannel.onclose += () => openSource.TrySetException(new InvalidOperationException("WebRTC data channel closed."));
        dataChannel.onerror += error => openSource.TrySetException(new InvalidOperationException($"WebRTC data channel error: {error}"));
        dataChannel.onmessage += (_, _, data) =>
        {
            RemoteTranscriptionResultMessage? response;
            try
            {
                response = JsonSerializer.Deserialize(
                    data,
                    WebRtcClientJsonContext.Default.RemoteTranscriptionResultMessage);
            }
            catch (JsonException)
            {
                return;
            }

            if (response is null || !response.IsValid)
            {
                return;
            }

            TaskCompletionSource<RemoteTranscriptionResultMessage>? responseSource;
            lock (_sync)
            {
                responseSource = _responseSource;
            }

            responseSource?.TrySetResult(response);
        };
    }

    private async Task<WebRtcOfferResponse> PostOfferAsync(Uri endpoint, WebRtcOfferRequest offerRequest)
    {
        using var content = JsonContent.Create(
            offerRequest,
            WebRtcClientJsonContext.Default.WebRtcOfferRequest);
        using var response = await _httpClient.PostAsync(endpoint, content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var offerResponse = await response.Content
            .ReadFromJsonAsync(WebRtcClientJsonContext.Default.WebRtcOfferResponse)
            .ConfigureAwait(false);
        return offerResponse ?? throw new InvalidOperationException("WebRTC offer response was empty.");
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

    private static async Task<T> WaitForResponseAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state =>
        {
            var source = (TaskCompletionSource<bool>)state!;
            source.TrySetResult(true);
        }, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

        var completedTask = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        if (completedTask != task)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return await task.ConfigureAwait(false);
    }

    private static async Task WaitForOpenAsync(Task openTask, CancellationToken cancellationToken)
    {
        var completedTask = await Task.WhenAny(openTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        if (completedTask != openTask)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        await openTask.ConfigureAwait(false);
    }

    private static Uri BuildSessionEndpoint(string remoteServerUrl)
    {
        var baseUri = remoteServerUrl.EndsWith("/", StringComparison.Ordinal)
            ? new Uri(remoteServerUrl, UriKind.Absolute)
            : new Uri($"{remoteServerUrl}/", UriKind.Absolute);
        return new Uri(baseUri, WebRtcProtocolConstants.SessionEndpoint.TrimStart('/'));
    }

    private static RTCSdpType ParseSdpType(string type)
    {
        return Enum.Parse<RTCSdpType>(type, ignoreCase: true);
    }

    private void SendJson(RemoteTranscriptionStartMessage message)
    {
        var json = JsonSerializer.Serialize(
            message,
            WebRtcClientJsonContext.Default.RemoteTranscriptionStartMessage);
        GetRequiredDataChannel().send(json);
    }

    private void SendJson(RemoteTranscriptionEndMessage message)
    {
        var json = JsonSerializer.Serialize(
            message,
            WebRtcClientJsonContext.Default.RemoteTranscriptionEndMessage);
        GetRequiredDataChannel().send(json);
    }

    private RTCDataChannel GetRequiredDataChannel()
    {
        lock (_sync)
        {
            return _dataChannel ?? throw new InvalidOperationException("WebRTC data channel is not available.");
        }
    }

    private static IEnumerable<byte[]> Chunk(byte[] source, int chunkSize)
    {
        for (var offset = 0; offset < source.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, source.Length - offset);
            var chunk = new byte[count];
            Buffer.BlockCopy(source, offset, chunk, 0, count);
            yield return chunk;
        }
    }

    private static async Task<PayloadData> LoadPayloadAsync(TranscriptionRequest request, CancellationToken cancellationToken)
    {
        if (request.AudioSamples is { Length: > 0 })
        {
            var bytes = new byte[request.AudioSamples.Length * sizeof(float)];
            Buffer.BlockCopy(request.AudioSamples, 0, bytes, 0, bytes.Length);
            return new PayloadData(
                RemoteTranscriptionAudioFormat.Float32Samples,
                "capture.f32",
                bytes);
        }

        if (string.IsNullOrWhiteSpace(request.AudioFilePath) || !File.Exists(request.AudioFilePath))
        {
            throw new FileNotFoundException("Audio file not found.", request.AudioFilePath);
        }

        return new PayloadData(
            RemoteTranscriptionAudioFormat.FileBytes,
            Path.GetFileName(request.AudioFilePath),
            await File.ReadAllBytesAsync(request.AudioFilePath, cancellationToken).ConfigureAwait(false));
    }

    private async Task ResetConnectionAsync()
    {
        DisposeConnection();
        await TryLogAsync("Reset WebRTC transcription client connection.").ConfigureAwait(false);
    }

    private void DisposeConnection()
    {
        lock (_sync)
        {
            _dataChannel = null;
            _dataChannelOpenSource = null;
            _responseSource = null;
            _peerConnection?.Close("dispose");
            _peerConnection?.Dispose();
            _peerConnection = null;
            _remoteServerUrl = string.Empty;
            _iceServerUrl = string.Empty;
        }
    }

    private Task TryLogAsync(string message)
    {
        return _activityLogService is null
            ? Task.CompletedTask
            : _activityLogService.WriteAsync(message);
    }

    private sealed record PayloadData(
        RemoteTranscriptionAudioFormat AudioFormat,
        string FileName,
        byte[] Bytes);
}
