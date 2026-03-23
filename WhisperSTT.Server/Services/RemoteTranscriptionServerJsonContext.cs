using System.Text.Json.Serialization;
using WhisperSTT.Core.Contracts;
using WhisperSTT.Core.Models;

namespace WhisperSTT.Server.Services;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ServerStatusResponse))]
[JsonSerializable(typeof(RemoteTranscriptionStartMessage))]
[JsonSerializable(typeof(RemoteTranscriptionResultMessage))]
[JsonSerializable(typeof(TranscriptionResult))]
[JsonSerializable(typeof(TranscriptionSegment))]
[JsonSerializable(typeof(RemoteTranscriptionSourceType))]
internal sealed partial class RemoteTranscriptionServerJsonContext : JsonSerializerContext
{
}

internal sealed record ServerStatusResponse(
    string Service,
    string TranscriptionEndpoint,
    string DataRoot);
