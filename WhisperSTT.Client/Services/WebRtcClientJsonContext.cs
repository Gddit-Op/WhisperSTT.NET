using System.Text.Json.Serialization;
using WhisperSTT.Core.Contracts;
using WhisperSTT.Core.Models;

namespace WhisperSTT.Client.Services;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(WebRtcSessionDescription))]
[JsonSerializable(typeof(WebRtcOfferRequest))]
[JsonSerializable(typeof(WebRtcOfferResponse))]
[JsonSerializable(typeof(RemoteTranscriptionStartMessage))]
[JsonSerializable(typeof(RemoteTranscriptionEndMessage))]
[JsonSerializable(typeof(RemoteTranscriptionResultMessage))]
[JsonSerializable(typeof(TranscriptionResult))]
[JsonSerializable(typeof(TranscriptionSegment))]
internal sealed partial class WebRtcClientJsonContext : JsonSerializerContext
{
}
