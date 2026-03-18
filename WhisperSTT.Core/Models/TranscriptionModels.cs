namespace WhisperSTT.Core.Models;

public sealed record RecordedAudioCapture(
    string? AudioFilePath,
    float[]? AudioSamples,
    int SampleRate,
    int Channels)
{
    public bool HasAudio =>
        (!string.IsNullOrWhiteSpace(AudioFilePath)) ||
        (AudioSamples is { Length: > 0 });
}

public sealed record TranscriptionRequest(
    string AudioFilePath,
    string ModelPath,
    LanguageMode LanguageMode,
    int ThreadCount,
    WhisperRuntimePreference RuntimePreference,
    string OpenVinoRuntimePath = "",
    bool IsLivePreview = false,
    bool EnableDiagnosticLogging = false,
    float[]? AudioSamples = null,
    int AudioSampleRate = 0,
    int AudioChannels = 0);

public sealed record TranscriptionSegment(TimeSpan Start, TimeSpan End, string Text);

public sealed record TranscriptionResult(
    string Text,
    IReadOnlyList<TranscriptionSegment> Segments,
    TimeSpan Duration,
    string ModelPath,
    string? DetectedLanguage = null,
    string? UsedRuntime = null);
