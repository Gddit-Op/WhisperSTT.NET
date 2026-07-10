# WhisperSTT
Arbeite im aktuellen Verzeichnis und erstelle eine lauffähige Windows-Desktop-App für offline Speech-to-Text mit Whisper.

Ziel:
Eine kleine Windows-App in .NET 9, die aus jeder Anwendung heraus per globaler Tastenkombination eine Sprachaufnahme startet und beendet, 
das Gesprochene lokal transkribiert und den erkannten Text in die aktuell fokussierte Anwendung einfügt. 
Erste Sprachen: Deutsch und Englisch. Keine Cloud, keine Online-APIs, komplett offline.

WhisperSTT is a small Windows system-tray app for fully local speech-to-text.

It records from your microphone via global hotkeys, transcribes speech with Whisper
, and pastes the recognized text into the currently focused application.

## Features

- Windows 10/11 desktop tray app 
- Global hotkeys (works while other apps are focused)
- Start/stop recording with one hotkey, cancel with another
- Offline transcription with `https://github.com/sandrohanea/whisper.net`
- Optional near-realtime live transcription preview during recording
- Language mode: `auto`, `de`, `en`
- Model selection presets: `tiny`, `base`, `small`, `medium`, `large-v3`, `large-v3-turbo`
- Separate CPU tuning for microphone/live and file transcription
- Custom faster-whisper model IDs (or local model path) can be entered in Settings
- Settings window in tray menu (language, model, hotkeys, audio, paste, logging)
- Built-in file transcription page for local `*.mp3` and `*.wav` files
- Audio preview controls for selected file (`Play`, `Pause`, `Stop`)
- Model download button in Settings (`Download Selected Model`)
- Clipboard + `Ctrl+V` insertion strategy with clipboard restore
- Optional start/stop/ready feedback sounds
- Optional transcript history file with Settings toggle
- Persistent config in `%APPDATA%/WhisperNET/config.json`
- Status states: `Idle`, `Recording`, `Transcribing`, `Error`
- Automatic microphone fallback to a valid input device
- Modular architecture and basic unit tests

## Privacy

WhisperSTT is designed for **100% offline** use:

- No cloud APIs
- No telemetry
- No network calls from app logic
- Model loading uses local files only by default

## Requirements

- Windows 10 or Windows 11
- .NET 9
- A working microphone

## CPU and Memory Requirements

WhisperNET runs on CPU (`device=cpu`) by default. Performance depends strongly on model size.

- CPU baseline: modern x64 CPU, 4+ cores recommended (6-8 cores preferred for `medium` and larger)
- RAM baseline: 8 GB minimum for small models, 16 GB recommended for larger models

Approximate CPU-only guidance:

- `tiny`, `base`: 8 GB RAM system is usually sufficient
- `small` (default): 8-12 GB RAM recommended
- `medium`: 16 GB RAM recommended
- `large-v3`: 16-32 GB RAM recommended, significantly slower on CPU
- `large-v3-turbo`: 16 GB RAM recommended, usually faster than `large-v3` on CPU

Notes:

- Actual memory usage depends on audio length, parallel system load, and backend/runtime libraries.
- If CPU transcription feels too slow, use `small` or `medium`, or switch to `device=cuda` when a compatible GPU is available.


## Optional CUDA installation

The default install above is CPU-only and works without NVIDIA runtime libraries.

