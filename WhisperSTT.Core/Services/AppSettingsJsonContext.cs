using System.Text.Json.Serialization;
using WhisperSTT.Core.Models;

namespace WhisperSTT.Core.Services;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}
