# WhisperSTT

WhisperSTT is a Windows 10/11 system tray app for fully local speech-to-text with Whisper.

It records from your microphone via global hotkeys, transcribes the audio offline, and pastes the recognized text into the currently focused application by using a clipboard + `Ctrl+V` strategy with optional clipboard restore. A manual `Copy` action is also available for the latest transcript when automatic paste is blocked.

## Status

This repository contains a runnable first implementation in `.NET 9`.

Implemented:

- Avalonia Desktop app with tray behavior
- Global hotkeys for start/stop and cancel
- Microphone recording via `NAudio`
- Offline file and microphone transcription via `Whisper.net`
- Language mode selection: `auto`, `de`, `en`
- Explicit Whisper language detection when `auto` is selected
- Runtime selection: `Auto`, `Cpu`, `OpenVino`, `Vulkan`, `Cuda`
- Model preset selection
- Model download button for preset Whisper models
- File transcription page for `*.wav` and `*.mp3`
- Audio preview controls: `Play`, `Pause`, `Stop`
- Manual `Copy` button for the latest transcript
- Dynamic tray/taskbar status icons: green `Idle`, red `Recording`, blue `Transcribing`
- Persistent config in `%APPDATA%/WhisperNET/config.json`
- Transcript history and app log files

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
- Runtime mode: `Auto`, `Cpu`, `OpenVino`, `Vulkan`, `Cuda`
- Automatic language detection for `auto` mode with detected language shown in the UI and written to `app.log`
- Model presets: `tiny`, `base`, `small`, `medium`, `large-v3`, `large-v3-turbo`
- Separate thread settings for microphone and file transcription
- Optional custom local model path
- Settings UI for language, model, hotkeys, audio, paste, and logging
- Press-to-capture hotkey editing in Settings
- Built-in local file transcription for `*.mp3` and `*.wav`
- Audio preview for selected files
- Clipboard restore after paste
- Manual transcript copy-to-clipboard button as fallback
- Optional feedback sounds
- Optional transcript history file
- Status states: `Idle`, `Recording`, `Transcribing`, `Error`
- Dynamic tray and taskbar icons highlighted by status color
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

WhisperSTT can run with CPU or supported accelerated Whisper runtimes.

Approximate guidance:

- `tiny`, `base`: usually fine on 8 GB RAM systems
- `small`: 8-12 GB RAM recommended
- `medium`: 16 GB RAM recommended
- `large-v3`: 16-32 GB RAM recommended and significantly slower on CPU
- `large-v3-turbo`: about 16 GB RAM recommended and often faster than `large-v3`

Notes:

- Performance depends on CPU cores, audio length, and system load.
- If transcription feels too slow, use `small` or `medium`.
- `Auto` currently prefers `Cuda`, then `Vulkan`, then `OpenVino`, then `Cpu`.
- `Cuda`, `OpenVino`, and `Vulkan` still require the matching drivers and native prerequisites on the machine.

## Tech Stack

- `.NET 9`
- `Avalonia.Desktop`
- `CommunityToolkit.Mvvm`
- `NAudio` for microphone capture and audio handling
- `Whisper.net` for offline Whisper inference

## Solution Layout

- `WhisperSTTClassic.sln`: main solution file to use
- `WhisperSTT.App`: Avalonia tray application and Windows-specific integrations
- `WhisperSTT.Core`: settings, models, paths, and service contracts
- `Task.md`: original product/task specification

## Build

Use the classic solution file:

```powershell
dotnet restore .\WhisperSTTClassic.sln
dotnet build .\WhisperSTTClassic.sln -c Debug
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
9. If automatic paste does not work in a target app, use the `Copy` button in `Latest Transcript`.

Default hotkeys:

- Start/Stop recording: `Ctrl+Alt+Space`
- Cancel recording: `Ctrl+Alt+Escape`

## How It Works

Microphone dictation flow:

1. A global hotkey starts recording.
2. Audio is captured to a temporary `.wav` file in `%APPDATA%/WhisperNET/temp`.
3. Stopping the recording starts Whisper transcription.
4. If `Language Mode` is `auto`, Whisper language detection is enabled explicitly.
5. The configured runtime preference is applied before Whisper is created.
6. The recognized text is copied to the clipboard.
7. `Ctrl+V` is sent to the focused application.
8. The previous clipboard content is restored if that option is enabled.
9. The detected language is shown in the UI and written to `app.log`.

File transcription flow:

1. Select a local `*.wav` or `*.mp3` file.
2. Optionally preview the audio.
3. Start transcription from the `File Transcription` page.
4. The transcript is shown in the UI and optionally written to history.
5. The latest live transcript can be copied manually with the `Copy` button.

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
    "RuntimePreference": "Auto",
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
- `RuntimePreference` controls the `Whisper.net` runtime order globally before each transcription.

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

## UI Notes

- The app starts hidden to the system tray.
- The `Settings` page includes a runtime selector for `Auto`, `Cpu`, `OpenVino`, `Vulkan`, and `Cuda`.
- The tray icon and the window/taskbar icon change color by state:
- `Idle` = green
- `Recording` = red
- `Transcribing` = blue
- The `Latest Transcript` panel includes a `Copy` button that copies the most recent text directly to the clipboard without trying to paste it.
- Hotkey fields in `Settings` are captured by pressing the desired key combination instead of typing free-form text.

## Logging

The activity log is stored at:

- `%APPDATA%/WhisperNET/app.log`

Current relevant log entries include:

- `Configured runtime: Vulkan.`
- `Detected language: de.`
- `Transcription completed with model ...`
- Clipboard and runtime errors

## Known Limitations

- Live preview is currently only a stored setting and not a streaming transcription pipeline.
- Hotkeys are parsed from strings, but invalid combinations are not surfaced with strong UI validation yet.
- The app currently defaults to microphone device `0` when the configured device is unavailable.
- The `CustomModelId` field is stored but not yet used for download or resolution logic.
- There is no installer or packaging yet.
- Automatic paste still depends on clipboard ownership and the focused target application; the manual `Copy` button is the fallback.
- Status icons are generated dynamically in code rather than loaded from static `.ico` assets.

## Verified Commands

These commands were run successfully in this repository:

```powershell
dotnet build .\WhisperSTT.App\WhisperSTT.App.csproj -c Debug
dotnet build .\WhisperSTTClassic.sln -c Debug
```

## Main Source Files

- [WhisperSTT.App/App.Avalonia.cs](C:/Github/STT/Whisper.NET/WhisperSTT.App/App.Avalonia.cs)
- [WhisperSTT.App/App.axaml](C:/Github/STT/Whisper.NET/WhisperSTT.App/App.axaml)
- [WhisperSTT.App/MainWindow.axaml](C:/Github/STT/Whisper.NET/WhisperSTT.App/MainWindow.axaml)
- [WhisperSTT.App/MainWindow.Avalonia.cs](C:/Github/STT/Whisper.NET/WhisperSTT.App/MainWindow.Avalonia.cs)
- [WhisperSTT.App/ViewModels/MainViewModel.cs](C:/Github/STT/Whisper.NET/WhisperSTT.App/ViewModels/MainViewModel.cs)
- [WhisperSTT.App/Services/GlobalHotkeyService.cs](C:/Github/STT/Whisper.NET/WhisperSTT.App/Services/GlobalHotkeyService.cs)
- [WhisperSTT.App/Services/NAudioRecorderService.cs](C:/Github/STT/Whisper.NET/WhisperSTT.App/Services/NAudioRecorderService.cs)
- [WhisperSTT.App/Services/WhisperTranscriptionService.cs](C:/Github/STT/Whisper.NET/WhisperSTT.App/Services/WhisperTranscriptionService.cs)
- [WhisperSTT.App/Services/ClipboardPasteService.cs](C:/Github/STT/Whisper.NET/WhisperSTT.App/Services/ClipboardPasteService.cs)
- [WhisperSTT.App/Services/StatusIconFactory.cs](C:/Github/STT/Whisper.NET/WhisperSTT.App/Services/StatusIconFactory.cs)
- [WhisperSTT.Core/Services/JsonSettingsStore.cs](C:/Github/STT/Whisper.NET/WhisperSTT.Core/Services/JsonSettingsStore.cs)

## Upstream Library

Whisper inference is powered by `Whisper.net`:

- https://github.com/sandrohanea/whisper.net
