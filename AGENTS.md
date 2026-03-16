# Repository Guidelines

## Project Structure & Module Organization

- `WhisperSTT.App/`: Avalonia Desktop UI, Windows tray integration, hotkeys, clipboard, audio preview, and Whisper runtime wiring.
- `WhisperSTT.Core/`: shared models, settings, paths, and service interfaces.
- `README.md`: user-facing setup and feature documentation.
- `Task.md`: original product brief.
- `WhisperSTTClassic.sln`: main solution file. Prefer this over `WhisperSTT.slnx`.

Note: New UI work should go into `*.axaml` and `*.Avalonia.cs`.

## Build, Test, and Development Commands

- `dotnet restore .\WhisperSTTClassic.sln`: restore NuGet packages.
- `dotnet build .\WhisperSTTClassic.sln -c Debug`: build the full solution.
- `dotnet build .\WhisperSTT.App\WhisperSTT.App.csproj -c Debug`: build the desktop app only.
- `dotnet run --project .\WhisperSTT.App\WhisperSTT.App.csproj`: launch the app locally.

There is currently no active test project in the repository, so `dotnet test` is not part of the default workflow until tests are restored.

## Coding Style & Naming Conventions

- Use 4-space indentation and standard C# conventions.
- `PascalCase` for types, public members, and files.
- `camelCase` for local variables and private fields; prefix private fields with `_`.
- Keep platform-specific code in `WhisperSTT.App/Services/`.
- Prefer concise, focused services over large code-behind files.
- Follow the existing MVVM pattern with `CommunityToolkit.Mvvm`.

## Testing Guidelines

- Add tests when reintroducing a test project; use `xUnit` to match prior setup.
- Name test files after the target type, e.g. `JsonSettingsStoreTests.cs`.
- Prefer behavior-focused names such as `LoadAsync_creates_default_config_when_missing`.

## Commit & Pull Request Guidelines

- Recent history uses short, imperative commit messages, often in German, e.g. `clipboard verbessert`, `icons verbessert`.
- Keep commits focused on a single change area.
- PRs should include:
- a short summary of user-visible changes
- build status (`dotnet build ...`)
- screenshots for UI changes
- notes about clipboard, hotkey, or transcription regressions if relevant

## Security & Configuration Tips

- App data is stored under `%APPDATA%\WhisperNET`.
- Do not commit local model files, logs, or generated outputs.
- Whisper model paths and clipboard behavior are sensitive integration points; test them manually after changes.
