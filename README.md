# WhisperSTT

WhisperSTT is a Windows 10/11 system tray app for fully local speech-to-text with Whisper.

It records from your microphone via global hotkeys, transcribes the audio offline, and pastes the recognized text into the currently focused application by using a clipboard + `Ctrl+V` strategy with optional clipboard restore.

## Status

This repository contains a runnable first implementation in `.NET 9`.

Implemented:

- WPF desktop app with tray behavior
- Global hotkeys for start/stop and cancel
- Microphone recording via `NAudio`
- Offline file and microphone transcription via `Whisper.net`
- Language mode selection: `auto`, `de`, `en`
- Model preset selection
- Model download button for preset Whisper models
- File transcription page for `*.wav` and `*.mp3`
- Audio preview controls: `Play`, `Pause`, `Stop`
- Persistent config in `%APPDATA%/WhisperNET/config.json`
- Transcript history and app log files
- Basic unit tests for settings persistence

Not fully implemented yet:

- True near-realtime live transcription preview during recording
- Dedicated settings validation UX for invalid hotkeys or thread counts
- Full use of custom model IDs; the current implementation uses preset downloads and an optional custom local model path

## Features

- Windows 10/11 desktop tray app
- Global hotkeys that work while other apps are focused
- Start/stop recording with one hotkey, cancel with another
- Offline transcription with `Whisper.net`
- Language mode: `auto`, `de`, `en`
- Model presets: `tiny`, `base`, `small`, `medium`, `large-v3`, `large-v3-turbo`
- Separate thread settings for microphone and file transcription
- Optional custom local model path
- Settings UI for language, model, hotkeys, audio, paste, and logging
- Built-in local file transcription for `*.mp3` and `*.wav`
- Audio preview for selected files
- Clipboard restore after paste
- Optional feedback sounds
- Optional transcript history file
- Status states: `Idle`, `Recording`, `Transcribing`, `Error`
- Automatic fallback to microphone device `0` if the preferred device is invalid

## Privacy

WhisperSTT is designed for offline transcription:

- No cloud transcription APIs
- No telemetry
- No online calls during normal transcription flow
- Local model files are used for transcription

Note:

- The `Download Selected Model` action is a one-time model acquisition step and does require network access when you use it.
- If you place model files locally yourself, transcription remains fully local.

## Requirements

- Windows 10 or Windows 11
- `.NET SDK 9.0.312` or compatible `.NET 9` SDK
- A working microphone for live dictation
- Enough disk space for Whisper model files

## CPU and Memory Guidance

WhisperSTT currently runs on CPU by default.

Approximate guidance:

- `tiny`, `base`: usually fine on 8 GB RAM systems
- `small`: 8-12 GB RAM recommended
- `medium`: 16 GB RAM recommended
- `large-v3`: 16-32 GB RAM recommended and significantly slower on CPU
- `large-v3-turbo`: about 16 GB RAM recommended and often faster than `large-v3`

Notes:

- Performance depends on CPU cores, audio length, and system load.
- If transcription feels too slow, use `small` or `medium`.

## Tech Stack

- `.NET 9`
- `WPF`
- `NAudio` for microphone capture and audio handling
- `Whisper.net` for offline Whisper inference
- `xUnit` for tests

## Solution Layout

- `WhisperSTTClassic.sln`: main solution file to use
- `WhisperSTT.App`: WPF tray application and Windows-specific integrations
- `WhisperSTT.Core`: settings, models, paths, and service contracts
- `WhisperSTT.Tests`: unit tests
- `Task.md`: original product/task specification

## Build

Use the classic solution file:

```powershell
dotnet restore .\WhisperSTTClassic.sln
dotnet build .\WhisperSTTClassic.sln -c Debug
```

Run tests:

```powershell
dotnet test .\WhisperSTTClassic.sln -c Debug
```

Run the app:

```powershell
dotnet run --project .\WhisperSTT.App\WhisperSTT.App.csproj
```

## First Start

1. Start the app.
2. The main window is created and then hidden to the system tray.
3. Open the app from the tray icon.
4. Go to `Settings`.
5. Choose a recording model preset.
6. Click `Download Selected Model` or point `Custom Model Path` to a local Whisper model file.
7. Save settings.
8. Use the configured hotkey to start and stop recording.

Default hotkeys:

- Start/Stop recording: `Ctrl+Alt+Space`
- Cancel recording: `Ctrl+Alt+Escape`

## How It Works

Microphone dictation flow:

1. A global hotkey starts recording.
2. Audio is captured to a temporary `.wav` file in `%APPDATA%/WhisperNET/temp`.
3. Stopping the recording starts Whisper transcription.
4. The recognized text is copied to the clipboard.
5. `Ctrl+V` is sent to the focused application.
6. The previous clipboard content is restored if that option is enabled.

File transcription flow:

1. Select a local `*.wav` or `*.mp3` file.
2. Optionally preview the audio.
3. Start transcription from the `File Transcription` page.
4. The transcript is shown in the UI and optionally written to history.

## Configuration

Main config file:

- `%APPDATA%/WhisperNET/config.json`

Other app data:

- `%APPDATA%/WhisperNET/history.log`
- `%APPDATA%/WhisperNET/app.log`
- `%APPDATA%/WhisperNET/models`
- `%APPDATA%/WhisperNET/temp`

Current settings structure:

```json
{
  "Hotkeys": {
    "ToggleRecordingGesture": "Ctrl+Alt+Space",
    "CancelRecordingGesture": "Ctrl+Alt+Escape"
  },
  "Audio": {
    "PreferredInputDeviceNumber": null,
    "EnableFeedbackSounds": true,
    "EnableLivePreview": false
  },
  "Transcription": {
    "LanguageMode": "Auto",
    "RecordingModelPreset": "Small",
    "FileModelPreset": "Small",
    "RecordingThreadCount": 4,
    "FileThreadCount": 7,
    "CustomModelId": "",
    "CustomModelPath": "",
    "LastFilePath": ""
  },
  "Paste": {
    "RestoreClipboardAfterPaste": true
  },
  "Logging": {
    "EnableLogging": true,
    "WriteTranscriptHistory": true
  }
}
```

Note:

- `RecordingThreadCount` and `FileThreadCount` depend on your CPU and may differ from the example above.

## Model Handling

Preset model downloads are stored under:

- `%APPDATA%/WhisperNET/models`

Current preset file mapping:

- `tiny` -> `ggml-tiny.bin`
- `base` -> `ggml-base.bin`
- `small` -> `ggml-small.bin`
- `medium` -> `ggml-medium.bin`
- `large-v3` -> `ggml-large-v3.bin`
- `large-v3-turbo` -> `ggml-large-v3-turbo.bin`

Model resolution order:

1. `CustomModelPath` if it is set and points to an existing file
2. Preset model path inside `%APPDATA%/WhisperNET/models`

## Known Limitations

- Live preview is currently only a stored setting and not a streaming transcription pipeline.
- Hotkeys are parsed from strings, but invalid combinations are not surfaced with strong UI validation yet.
- The app currently defaults to microphone device `0` when the configured device is unavailable.
- The `CustomModelId` field is stored but not yet used for download or resolution logic.
- There is no installer or packaging yet.
- There is no dedicated tray icon asset yet; the current build uses the default application icon.

## Verified Commands

These commands were run successfully in this repository:

```powershell
dotnet build .\WhisperSTTClassic.sln -c Debug
dotnet test .\WhisperSTTClassic.sln -c Debug --no-build
```

## Main Source Files

- [WhisperSTT.App/App.xaml.cs](C:/Github/STT/Whisper.NET/WhisperSTT.App/App.xaml.cs)
- [WhisperSTT.App/MainWindow.xaml](C:/Github/STT/Whisper.NET/WhisperSTT.App/MainWindow.xaml)
- [WhisperSTT.App/ViewModels/MainViewModel.cs](C:/Github/STT/Whisper.NET/WhisperSTT.App/ViewModels/MainViewModel.cs)
- [WhisperSTT.App/Services/GlobalHotkeyService.cs](C:/Github/STT/Whisper.NET/WhisperSTT.App/Services/GlobalHotkeyService.cs)
- [WhisperSTT.App/Services/NAudioRecorderService.cs](C:/Github/STT/Whisper.NET/WhisperSTT.App/Services/NAudioRecorderService.cs)
- [WhisperSTT.App/Services/WhisperTranscriptionService.cs](C:/Github/STT/Whisper.NET/WhisperSTT.App/Services/WhisperTranscriptionService.cs)
- [WhisperSTT.Core/Services/JsonSettingsStore.cs](C:/Github/STT/Whisper.NET/WhisperSTT.Core/Services/JsonSettingsStore.cs)
- [WhisperSTT.Tests/UnitTest1.cs](C:/Github/STT/Whisper.NET/WhisperSTT.Tests/UnitTest1.cs)

## Upstream Library

Whisper inference is powered by `Whisper.net`:

- https://github.com/sandrohanea/whisper.net
