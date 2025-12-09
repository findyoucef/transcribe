# Transcribe

Command-line transcription using Whisper. Supports batch WAV transcription and real-time capture from microphones or speakers, with live audio-level visualization and timestamped outputs.

## Requirements
- .NET 10 SDK
- Windows audio stack (NAudio WASAPI) for live mic/speaker capture

## Build
```bash
dotnet restore
dotnet build
```

## Usage
Run commands via `dotnet run --project transcribe -- <command> [options]`.

### Transcribe a WAV file
```bash
dotnet run --project transcribe -- file input.wav [--model <name>] [--language <code>]
```
- Writes `input.txt` (timestamped segments) to the current working directory.
- Fails if that transcript already exists to avoid overwriting.
- Examples:
  - `dotnet run --project transcribe -- file sample.wav`
  - `dotnet run --project transcribe -- file sample.wav --model LargeV3 --language fr`

### Live microphone transcription
```bash
dotnet run --project transcribe -- mic [--device <number>] [--chunk-size <seconds>] [--model <name>] [--language <code>]
```
- Shows a live audio-level bar; press `Ctrl+C` to stop.
- Saves a timestamped transcript to a new GUID-named `.txt` file in the working directory.
- Defaults: device `0`, chunk size `3s`.
- List devices first with `dotnet run --project transcribe -- list-devices`.

### Live speaker/output transcription
```bash
dotnet run --project transcribe -- speaker [--device <number>] [--chunk-size <seconds>] [--model <name>] [--language <code>]
```
- Captures system output via WASAPI loopback.
- Same defaults and output behavior as microphone mode.
- List speakers first with `dotnet run --project transcribe -- list-speakers`.

### Models
Supported values (downloaded on first use into the app’s binary directory):
- `LargeV3Turbo` (default)
- `LargeV3`
- `BaseEn`
- `TinyEn`

### Languages
- Default is `en`. Pass ISO codes (e.g., `en`, `fr`, `es`) to `--language`.

## Notes
- Network is required only for the initial model download.
- Standard PCM WAV input works best; malformed headers will skip progress reporting.
- Keep the working directory writable so transcript files can be created.
