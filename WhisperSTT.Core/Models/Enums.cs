namespace WhisperSTT.Core.Models;

public enum AppStatus
{
    Idle,
    Recording,
    Transcribing,
    Error
}

public enum LanguageMode
{
    Auto,
    De,
    En
}

public enum ModelPreset
{
    Tiny,
    Base,
    Small,
    Medium,
    LargeV3,
    LargeV3Turbo
}
