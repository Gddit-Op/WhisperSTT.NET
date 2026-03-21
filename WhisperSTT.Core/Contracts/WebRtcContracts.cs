using System.Text.Json.Serialization;
using WhisperSTT.Core.Models;

namespace WhisperSTT.Core.Contracts;

public static class WebRtcProtocolConstants
{
    public const string DefaultChannelLabel = "whisper-stt";
    public const string SessionEndpoint = "/api/webrtc/sessions";
    public const string TranscriptionStartMessageType = "transcription-start";
    public const string TranscriptionEndMessageType = "transcription-end";
    public const string TranscriptionResultMessageType = "transcription-result";
    public const int DefaultChunkSize = 16 * 1024;
}

public sealed record WebRtcSessionDescription(string Type, string Sdp);

public sealed record WebRtcOfferRequest(
    WebRtcSessionDescription Offer,
    string? IceServerUrl = null,
    string ChannelLabel = WebRtcProtocolConstants.DefaultChannelLabel);

public sealed record WebRtcOfferResponse(
    Guid SessionId,
    WebRtcSessionDescription Answer);

public sealed record RemoteTranscriptionStartMessage(
    string MessageType,
    string RequestId,
    RemoteTranscriptionSourceType SourceType,
    RemoteTranscriptionAudioFormat AudioFormat,
    string? FileName,
    long PayloadLength,
    string PreferredModelPath,
    ModelPreset RequestedModelPreset,
    LanguageMode LanguageMode,
    int ThreadCount,
    WhisperRuntimePreference RuntimePreference,
    string OpenVinoRuntimePath,
    bool IsLivePreview,
    bool EnableDiagnosticLogging,
    int SampleRate = 0,
    int Channels = 0)
{
    [JsonIgnore]
    public bool IsValid => string.Equals(MessageType, WebRtcProtocolConstants.TranscriptionStartMessageType, StringComparison.Ordinal);
}

public sealed record RemoteTranscriptionEndMessage(
    string MessageType,
    string RequestId)
{
    [JsonIgnore]
    public bool IsValid => string.Equals(MessageType, WebRtcProtocolConstants.TranscriptionEndMessageType, StringComparison.Ordinal);
}

public sealed record RemoteTranscriptionResultMessage(
    string MessageType,
    string RequestId,
    bool Success,
    TranscriptionResult? Result,
    string ErrorMessage = "")
{
    [JsonIgnore]
    public bool IsValid => string.Equals(MessageType, WebRtcProtocolConstants.TranscriptionResultMessageType, StringComparison.Ordinal);
}
