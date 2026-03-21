using Whisper.net.Ggml;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;
using WhisperSTT.Server.Configuration;

namespace WhisperSTT.Server.Services;

public sealed class WhisperModelService
{
    private readonly ApplicationPaths _paths;
    private readonly WhisperServerTranscriptionOptions _options;

    public WhisperModelService(
        ApplicationPaths paths,
        WhisperServerTranscriptionOptions options)
    {
        _paths = paths;
        _options = options;
        _paths.EnsureCreated();
    }

    public string ResolveModelPath(ModelPreset preset)
    {
        if (!string.IsNullOrWhiteSpace(_options.CustomModelPath) &&
            File.Exists(_options.CustomModelPath))
        {
            return _options.CustomModelPath;
        }

        return Path.Combine(_paths.ModelsDirectory, GetModelFileName(preset));
    }

    public async Task<string> DownloadModelAsync(
        ModelPreset preset,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var filePath = ResolveModelPath(preset);
        if (File.Exists(filePath))
        {
            return filePath;
        }

        await using var modelStream = await WhisperGgmlDownloader.Default
            .GetGgmlModelAsync(GetGgmlType(preset))
            .ConfigureAwait(false);
        await using var fileStream = File.Create(filePath);
        await modelStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);

        return filePath;
    }

    private static string GetModelFileName(ModelPreset preset) => preset switch
    {
        ModelPreset.Tiny => "ggml-tiny.bin",
        ModelPreset.Base => "ggml-base.bin",
        ModelPreset.Small => "ggml-small.bin",
        ModelPreset.Medium => "ggml-medium.bin",
        ModelPreset.LargeV3 => "ggml-large-v3.bin",
        ModelPreset.LargeV3Turbo => "ggml-large-v3-turbo.bin",
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
    };

    private static GgmlType GetGgmlType(ModelPreset preset) => preset switch
    {
        ModelPreset.Tiny => GgmlType.Tiny,
        ModelPreset.Base => GgmlType.Base,
        ModelPreset.Small => GgmlType.Small,
        ModelPreset.Medium => GgmlType.Medium,
        ModelPreset.LargeV3 => GgmlType.LargeV3,
        ModelPreset.LargeV3Turbo => GgmlType.LargeV3Turbo,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
    };
}
