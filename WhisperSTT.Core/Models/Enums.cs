using System.Text.Json.Serialization;

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

public enum WhisperRuntimePreference
{
    Auto,
    Cpu,
    OpenVino,
    Vulkan,
    Cuda
}

public enum TranscriptionTarget
{
    Local,
    Server
}

public enum RemoteTranscriptionAudioFormat
{
    FileBytes,
    Float32Samples
}

public enum RemoteTranscriptionSourceType
{
    Recording,
    File
}
