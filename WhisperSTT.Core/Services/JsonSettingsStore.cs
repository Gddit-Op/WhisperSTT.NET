using System.Text.Json;
using WhisperSTT.Core.Models;

namespace WhisperSTT.Core.Services;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly ApplicationPaths _paths;

    public JsonSettingsStore(ApplicationPaths paths)
    {
        _paths = paths;
        _paths.EnsureCreated();
    }

    public string ConfigPath => _paths.ConfigPath;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConfigPath))
        {
            var defaults = new AppSettings();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        var rawJson = await File.ReadAllTextAsync(ConfigPath, cancellationToken).ConfigureAwait(false);
        var normalizedJson = NormalizeLegacySettingsJson(rawJson);
        var settings = JsonSerializer.Deserialize(
            normalizedJson,
            AppSettingsJsonContext.Default.AppSettings);

        if (settings is null)
        {
            var defaults = new AppSettings();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        if (!string.Equals(rawJson, normalizedJson, StringComparison.Ordinal))
        {
            await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        await using var stream = File.Create(ConfigPath);
        await JsonSerializer.SerializeAsync(stream, settings, AppSettingsJsonContext.Default.AppSettings, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string NormalizeLegacySettingsJson(string json)
    {
        return json
            .Replace("\"WebRtc\"", "\"Server\"", StringComparison.Ordinal)
            .Replace("\"WebRtcIceServerUrl\"", "\"LegacyWebRtcIceServerUrl\"", StringComparison.Ordinal);
    }
}
