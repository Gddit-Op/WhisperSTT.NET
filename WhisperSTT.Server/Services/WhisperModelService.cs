using Whisper.net.Ggml;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;

namespace WhisperSTT.Server.Services;

public sealed class WhisperModelService
{
    private readonly ApplicationPaths _paths;

    public WhisperModelService(ApplicationPaths paths)
    {
        _paths = paths;
        _paths.EnsureCreated();
    }

    public string ResolveModelPath(AppSettings settings, ModelPreset preset)
    {
        if (!string.IsNullOrWhiteSpace(settings.Transcription.CustomModelPath) &&
            File.Exists(settings.Transcription.CustomModelPath))
        {
            return settings.Transcription.CustomModelPath;
        }

        return Path.Combine(_paths.ModelsDirectory, GetModelFileName(preset));
    }

    public async Task<string> DownloadModelAsync(
        AppSettings settings,
        ModelPreset preset,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var filePath = ResolveModelPath(settings, preset);
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
