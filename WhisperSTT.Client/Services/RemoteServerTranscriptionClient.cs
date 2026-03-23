using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using WhisperSTT.Core.Contracts;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;

namespace WhisperSTT.Client.Services;

public sealed class RemoteServerTranscriptionClient : ITranscriptionService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IActivityLogService? _activityLogService;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private bool _disposed;

    public RemoteServerTranscriptionClient(HttpClient httpClient, IActivityLogService? activityLogService = null)
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
            throw new InvalidOperationException("Remote server URL is required for remote transcription.");
        }

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = await LoadPayloadAsync(request, cancellationToken).ConfigureAwait(false);
            var requestId = Guid.NewGuid().ToString("N");

            var startMessage = new RemoteTranscriptionStartMessage(
                RemoteTranscriptionProtocolConstants.TranscriptionStartMessageType,
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

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, request.RemoteTimeoutSeconds)));
            var response = await PostTranscriptionAsync(
                    request.RemoteServerUrl,
                    startMessage,
                    payload,
                    timeoutCts.Token)
                .ConfigureAwait(false);
            if (!response.Success || response.Result is null)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.ErrorMessage)
                    ? "The remote server did not return a transcription result."
                    : response.ErrorMessage);
            }

            await TryLogAsync(
                $"Received remote transcription result for {requestId} ({request.SourceType}, {payload.AudioFormat}, {payload.Bytes.LongLength} bytes).")
                .ConfigureAwait(false);
            return response.Result;
        }
        catch (Exception exception)
        {
            await TryLogAsync($"Remote transcription failed: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
            throw;
        }
        finally
        {
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
    }

    private async Task<RemoteTranscriptionResultMessage> PostTranscriptionAsync(
        string remoteServerUrl,
        RemoteTranscriptionStartMessage metadata,
        PayloadData payload,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var metadataJson = JsonSerializer.Serialize(
            metadata,
            RemoteTranscriptionClientJsonContext.Default.RemoteTranscriptionStartMessage);
        content.Add(new StringContent(metadataJson, Encoding.UTF8, "application/json"), "metadata");

        using var payloadContent = new ByteArrayContent(payload.Bytes);
        payloadContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(payloadContent, "payload", payload.FileName);

        var endpoint = BuildTranscriptionEndpoint(remoteServerUrl);
        using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                string.IsNullOrWhiteSpace(errorBody)
                    ? $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase})."
                    : errorBody,
                null,
                response.StatusCode);
        }

        var result = await response.Content
            .ReadFromJsonAsync(RemoteTranscriptionClientJsonContext.Default.RemoteTranscriptionResultMessage, cancellationToken)
            .ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("Remote transcription response was empty.");
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

    private static Uri BuildTranscriptionEndpoint(string remoteServerUrl)
    {
        var baseUri = remoteServerUrl.EndsWith("/", StringComparison.Ordinal)
            ? new Uri(remoteServerUrl, UriKind.Absolute)
            : new Uri($"{remoteServerUrl}/", UriKind.Absolute);
        return new Uri(baseUri, RemoteTranscriptionProtocolConstants.TranscriptionEndpoint.TrimStart('/'));
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
