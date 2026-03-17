namespace WhisperSTT.Core.Models;

public sealed class AppSettings
{
    public HotkeySettings Hotkeys { get; set; } = new();

    public AudioSettings Audio { get; set; } = new();

    public TranscriptionSettings Transcription { get; set; } = new();

    public PasteSettings Paste { get; set; } = new();

    public LoggingSettings Logging { get; set; } = new();
}

public sealed class HotkeySettings
{
    public string ToggleRecordingGesture { get; set; } = "Ctrl+Alt+Space";

    public string CancelRecordingGesture { get; set; } = "Ctrl+Alt+Escape";
}

public sealed class AudioSettings
{
    public int? PreferredInputDeviceNumber { get; set; }

    public bool EnableFeedbackSounds { get; set; } = true;

    public bool EnableLivePreview { get; set; }
}

public sealed class TranscriptionSettings
{
    public LanguageMode LanguageMode { get; set; } = LanguageMode.Auto;

    public WhisperRuntimePreference RuntimePreference { get; set; } = WhisperRuntimePreference.Auto;

    public ModelPreset RecordingModelPreset { get; set; } = ModelPreset.Small;

    public ModelPreset FileModelPreset { get; set; } = ModelPreset.Small;

    public int RecordingThreadCount { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    public int FileThreadCount { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);

    public string CustomModelId { get; set; } = string.Empty;

    public string CustomModelPath { get; set; } = string.Empty;

    public string OpenVinoRuntimePath { get; set; } = string.Empty;

    public string LastFilePath { get; set; } = string.Empty;
}

public sealed class PasteSettings
{
    public bool RestoreClipboardAfterPaste { get; set; } = true;
}

public sealed class LoggingSettings
{
    public bool EnableLogging { get; set; } = true;

    public bool WriteTranscriptHistory { get; set; } = true;
}
