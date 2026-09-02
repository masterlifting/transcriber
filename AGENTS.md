# Repository guidance

## Layout

- Windows-only local call transcription: `recorder/` is the .NET 10 solution; `pipeline/` is the Python faster-whisper pipeline.
- Keep orchestration, runtime paths, and recording/transcription coordination in `recorder/src/Core/`; `Cli/` and `Desktop/` are front ends, and `Recorder/` owns native WASAPI capture.
- `pipeline` expects each call directory to contain `me.wav`, `remote.wav`, and (unless `--no-session` is used) `session.json`; it overwrites `transcript.json` and `transcript.md`.
- Treat `calls/` audio, session metadata, and transcripts as user data. `artifacts/` is generated portable output; do not hand-edit it.

## Commands

- Build all .NET projects: `dotnet build recorder/Transcriber.Recorder.sln -c Release`
- Run the model-free Python merge checks: `.venv\Scripts\python.exe pipeline\tests\test_merge.py`
- Run the pipeline manually: `.venv\Scripts\python.exe -m pipeline <call-directory>`.

## Gotchas

- Sources and documentation currently assume the checkout is `D:\transcriber`; `build-portable.ps1` has this path hard-coded. Do not use it from another location without first correcting that assumption.
- `build-portable.ps1` deletes and recreates `artifacts\portable\win-x64`, downloads dependencies, and requires a cached Whisper model. Treat packaging as destructive and networked.
- Hardware recording verification requires Windows audio devices and a target application; validated targets are Telegram Desktop and Google Meet in Firefox. Bluetooth HFP is known unreliable.
