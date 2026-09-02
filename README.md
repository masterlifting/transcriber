# Transcriber

Records two-track call audio (microphone + target application) and transcribes it
locally with faster-whisper. No cloud, no API keys.

## Layout

```
D:\transcriber\
├── recorder\          C# / .NET 10 solution
│   ├── src\Recorder\  native WASAPI capture (mic + process-loopback) -> me.wav / remote.wav
│   ├── src\Core\      shared orchestration (paths, state, recording + transcription services)
│   ├── src\Cli\       console entry point (transcriber.exe)
│   └── src\Desktop\   WPF UI (Transcriber.Desktop.exe, Material Design)
├── pipeline\          Python 3 (faster-whisper) transcription, sync and merge
├── calls\             development calls root (me.wav / remote.wav / session.json / transcripts)
├── artifacts\portable\  portable build output (ZIP + staging)
├── build-portable.ps1   builds the portable win-x64 package
└── legacy\            Phase-1 prototype artifacts (superseded)
```

## Quick start (development)

```powershell
dotnet build D:\transcriber\recorder\Transcriber.Recorder.sln -c Release

# Desktop UI
D:\transcriber\recorder\src\Desktop\bin\Release\net10.0-windows\Transcriber.Desktop.exe

# CLI
D:\transcriber\recorder\src\Cli\bin\Release\net10.0-windows\transcriber.exe record --process Telegram --mic "Microphone Array"
```

Development mode resolves the Python environment from `.venv` and keeps calls in
`D:\transcriber\calls`.

## Portable package

```powershell
.\build-portable.ps1
```

Produces `artifacts\portable\win-x64\Transcriber-win-x64-full.zip`
(self-contained .NET, bundled Python 3.12 + pinned packages, bundled Whisper
`turbo` model — works offline after extraction; no installation required).

## Validated targets

- Telegram Desktop (`--process Telegram`)
- Google Meet via Firefox (`--process Firefox`)

Known limitation: Bluetooth HFP call audio may be unreliable; use non-Bluetooth
audio paths.

## Data locations

- Calls: `%USERPROFILE%\Documents\Transcriber\Calls` (portable; configurable in the UI)
- Settings / logs / crash log: `%LOCALAPPDATA%\Transcriber`
- Development calls: `D:\transcriber\calls`

## Commands (CLI)

```
list-inputs                      enumerate microphones
list-processes [filter]          enumerate processes
record --process <name> [--pid N] [--mic <device>] [--duration-seconds N] [--no-transcribe]
transcribe <call-directory>      (re)run transcription for a recorded call
diag                             print resolved runtime paths
```

Exit codes: 0 success | 1 general | 2 invalid args | 3 recording failure |
4 transcription failure | 5 dependency failure.
