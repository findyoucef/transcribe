# Transcribe

Command-line transcription using Whisper. Supports batch WAV transcription, real-time mic capture, speaker/output capture, optional dual-source (speaker + mic) transcription, and post-run summarization via Ollama. Live modes show audio levels and write timestamped text.

## Requirements
- .NET 10 SDK
- Windows audio stack (NAudio WASAPI) for live mic/speaker capture
- Optional: running Ollama server at `http://localhost:11434/` with model `tx-agent:latest` for summaries

## Build
```bash
dotnet restore
dotnet build
```

## Usage
Build (or publish) the app, then run the binary (`./transcribe` on Linux/macOS, `transcribe.exe` on Windows). Binaries live under `transcribe/bin/<Config>/net10.0/`. Models are downloaded on first use and cached next to the binary.

## GUI
This repo also includes a cross-platform desktop GUI (Windows/macOS/Linux) built with Avalonia.

Run from source:
```bash
dotnet run --project transcribe.gui
```

Publish (example, self-contained):
```bash
dotnet publish transcribe.gui -c Release -r win-x64 --self-contained true
dotnet publish transcribe.gui -c Release -r linux-x64 --self-contained true
dotnet publish transcribe.gui -c Release -r osx-arm64 --self-contained true
```

Notes:
- File transcription works cross-platform.
- Live mic/speaker capture is Windows-only (NAudio WASAPI).

### Commands
- `file`: transcribe a WAV file.
- `mic`: real-time microphone transcription.
- `speaker`: real-time system-output transcription, with optional mic capture for conversations.
- `list-devices`: list microphones.
- `list-speakers`: list output devices.

### Transcribe a WAV file
```bash
./transcribe file input.wav [--model <name>] [--language <code>]
```
- Writes `input.txt` (timestamped segments) to the current working directory.
- Fails if that transcript already exists to avoid overwriting.
- Examples:
  - `./transcribe file sample.wav`
  - `./transcribe file sample.wav --model LargeV3 --language fr`
  - `./transcribe file sample.wav --model TinyEn`
  - `./transcribe file sample.wav --output-dir ./out`

### Live microphone transcription
```bash
./transcribe mic [--device <number>] [--chunk-size <seconds>] [--model <name>] [--language <code>] [--summarize]
```
- Shows a live audio-level bar; press `Ctrl+C` to stop.
- Saves a timestamped transcript to a new GUID-named `.txt` file in the working directory.
- Defaults: device `0`, chunk size `3s`.
- Add `--summarize` to stream a summary via Ollama after transcription finishes.
- List devices first with `./transcribe list-devices`.
 - Use `--output-dir <folder>` to write the transcript somewhere other than the working directory.

### Live speaker/output transcription
```bash
./transcribe speaker [--device <number>] [--chunk-size <seconds>] [--model <name>] [--language <code>] [--mic-device <number>] [--summarize]
```
- Captures system output via WASAPI loopback.
- Optionally capture your mic simultaneously (`--mic-device <n>`, default `-1` disables). Speaker and mic audio are processed separately and merged chronologically into one transcript.
- Same defaults and output behavior as microphone mode (chunk `3s`, device `0`, summary disabled by default).
- Add `--summarize` to stream a summary via Ollama after transcription finishes.
- List speakers first with `./transcribe list-speakers`.
 - Use `--output-dir <folder>` to write the transcript somewhere other than the working directory.

### Models
Supported values (downloaded on first use into the app’s binary directory):
- `LargeV3Turbo`
- `LargeV3`
- `BaseEn`
- `TinyEn` (CLI default)

### Languages
- Default is `en`. Pass ISO codes (e.g., `en`, `fr`, `es`) or `auto` for language detection.

### Speaker diarization (offline)
Enable diarization to add speaker labels (e.g., `[S1]`, `[SPK:S2]`) to the transcript output.
- Requires a speaker-embedding ONNX model (default expected at `models/wespeaker_en_voxceleb_resnet293_LM.onnx` next to the binary).
- Use `--diarize` to enable it for `file`, `mic`, or `speaker` commands.
- Optional: `--diarization-model <path>` overrides the local model path.
- Optional: `--diarization-model-url <url>` downloads the model if it is missing.
- Optional: `--diarization-config <path>` uses a custom config JSON (default `models/ecapa-tdnn.json`).

Examples:
```bash
./transcribe file sample.wav --diarize --diarization-model .\models\wespeaker_en_voxceleb_resnet293_LM.onnx
./transcribe mic --diarize --diarization-model-url https://example.com/wespeaker_en_voxceleb_resnet293_LM.onnx
./transcribe speaker --diarize --mic-device 1
```

## Notes
- Network is required only for the initial model download (and Ollama summaries if enabled).
- Standard PCM WAV input works best; malformed headers will skip progress reporting.
- Live modes write transcript files in the working directory using GUID names; make sure it is writable.
- Audio processing uses NAudio WASAPI. If a device index is invalid, the app will list the range to help pick a valid one.
