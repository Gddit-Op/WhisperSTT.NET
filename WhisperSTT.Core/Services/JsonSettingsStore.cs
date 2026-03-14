using System.Text.Json;
using System.Text.Json.Serialization;
using WhisperSTT.Core.Models;

namespace WhisperSTT.Core.Services;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

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

        await using var stream = File.OpenRead(ConfigPath);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);

        if (settings is null)
        {
            var defaults = new AppSettings();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        await using var stream = File.Create(ConfigPath);
        await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
    }
}
