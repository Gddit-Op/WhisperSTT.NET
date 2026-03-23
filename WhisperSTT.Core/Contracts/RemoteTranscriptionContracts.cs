using System.Text.Json.Serialization;
using WhisperSTT.Core.Models;

namespace WhisperSTT.Core.Contracts;

public static class RemoteTranscriptionProtocolConstants
{
    public const string TranscriptionEndpoint = "/api/transcriptions";
    public const string TranscriptionStartMessageType = "transcription-start";
    public const string TranscriptionResultMessageType = "transcription-result";
}

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
    public bool IsValid => string.Equals(MessageType, RemoteTranscriptionProtocolConstants.TranscriptionStartMessageType, StringComparison.Ordinal);
}

public sealed record RemoteTranscriptionResultMessage(
    string MessageType,
    string RequestId,
    bool Success,
    TranscriptionResult? Result,
    string ErrorMessage = "")
{
    [JsonIgnore]
    public bool IsValid => string.Equals(MessageType, RemoteTranscriptionProtocolConstants.TranscriptionResultMessageType, StringComparison.Ordinal);
}
