using WhisperSTT.Core.Models;

namespace WhisperSTT.Server.Configuration;

public sealed class WhisperServerTranscriptionOptions
{
    public const string SectionName = "Whisper";

    public LanguageMode LanguageMode { get; set; } = LanguageMode.Auto;

    public WhisperRuntimePreference RuntimePreference { get; set; } = WhisperRuntimePreference.Vulkan;

    public ModelPreset RecordingModelPreset { get; set; } = ModelPreset.LargeV3Turbo;

    public ModelPreset FileModelPreset { get; set; } = ModelPreset.LargeV3Turbo;

    public int RecordingThreadCount { get; set; } = 6;

    public int FileThreadCount { get; set; } = 11;

    public string CustomModelPath { get; set; } = @"C:\Publish\Whisper\model\whisper-large-v3-turbo-german-ggml.bin";

    public string OpenVinoRuntimePath { get; set; } = @"C:\Github\STT\OpenVinoExample\OV_runtime";

    public ModelPreset GetModelPreset(RemoteTranscriptionSourceType sourceType)
    {
        return sourceType == RemoteTranscriptionSourceType.File
            ? FileModelPreset
            : RecordingModelPreset;
    }

    public int GetThreadCount(RemoteTranscriptionSourceType sourceType)
    {
        var configuredThreadCount = sourceType == RemoteTranscriptionSourceType.File
            ? FileThreadCount
            : RecordingThreadCount;
        return Math.Max(1, configuredThreadCount);
    }
}
