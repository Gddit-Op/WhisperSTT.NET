using System.Text.Json.Serialization;
using WhisperSTT.Core.Contracts;
using WhisperSTT.Core.Models;

namespace WhisperSTT.Client.Services;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemoteTranscriptionStartMessage))]
[JsonSerializable(typeof(RemoteTranscriptionResultMessage))]
[JsonSerializable(typeof(TranscriptionResult))]
[JsonSerializable(typeof(TranscriptionSegment))]
[JsonSerializable(typeof(RemoteTranscriptionSourceType))]
internal sealed partial class WebRtcClientJsonContext : JsonSerializerContext
{
}
