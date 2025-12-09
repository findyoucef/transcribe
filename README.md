# Transcribe

Transcribe WAV audio files to text using Whisper models with progress reporting.

## Requirements
- .NET 10 SDK
- NuGet packages:
  - `System.CommandLine` 2.0.0
  - `Whisper.net.AllRuntimes` 1.9.0

## Build
```
dotnet restore
dotnet build
```

## Usage
```
transcribe <wav> [--model <model>] [--language <code>]
```
Examples:
- `transcribe sample.wav`
- `transcribe sample.wav --model LargeV3`
- `transcribe sample.wav --language fr`

Output:
- Creates a `.txt` file next to the input WAV containing timestamped segments and text.

## Models
Supported values:
- `LargeV3Turbo` (default)
- `LargeV3`
- `BaseEn`
- `TinyEn`

On first run, the selected model is downloaded into the application’s binary directory.

## Notes
- If a transcript file already exists for the input WAV, the app exits with an error.
- Language defaults to `en`. Provide ISO language codes (e.g., `en`, `fr`, `es`).

## Troubleshooting
- Ensure the WAV file is standard PCM with a valid header.
- Check network access for initial model download.
- Use `--model LargeV3` if Turbo is not desired.
