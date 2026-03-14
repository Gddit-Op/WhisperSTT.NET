namespace WhisperSTT.Core.Models;

public sealed record TranscriptionRequest(
    string AudioFilePath,
    string ModelPath,
    LanguageMode LanguageMode,
    int ThreadCount,
    WhisperRuntimePreference RuntimePreference,
    bool IsLivePreview = false);

public sealed record TranscriptionSegment(TimeSpan Start, TimeSpan End, string Text);

public sealed record TranscriptionResult(
    string Text,
    IReadOnlyList<TranscriptionSegment> Segments,
    TimeSpan Duration,
    string ModelPath,
    string? DetectedLanguage = null,
    string? UsedRuntime = null);
