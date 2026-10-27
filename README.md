# Transcribe

GUI and CLI transcription using Whisper. Supports batch WAV transcription, real-time mic capture, speaker/output capture, optional dual-source (speaker + mic) transcription, diarization and post-run summarization via Ollama. Live modes show speakers and write timestamped text.

## Website (GitHub Pages)
The project site lives in `docs/`. To publish, go to GitHub Settings -> Pages and set the source to `main` and folder `/docs`. Update `docs/index.html` `data-repo` with your GitHub `owner/repo` so download links resolve.

## Requirements
- .NET 10 SDK
- System audio uses WASAPI on Windows and native Core Audio taps on macOS 14.2+. Microphone capture on macOS uses bundled PortAudio, or Apple voice processing when echo cancellation is enabled. Linux live capture uses bundled PortAudio.
- Building native macOS capture requires Xcode Command Line Tools (`xcode-select --install`). Build macOS releases on a Mac; the audio bridge is built for both Apple Silicon and Intel automatically.
- Optional: running Ollama server at `http://localhost:11434/` with model `tx-agent:latest` for summaries

## Build
```bash
dotnet restore
dotnet build
```

## Usage
Build (or publish) the app, then run the binary (`./tx` on Linux/macOS, `tx.exe` on Windows). Binaries live under `transcribe/bin/<Config>/net10.0/`. Models are downloaded on first use: macOS stores new downloads in `~/Library/Application Support/Transcribe/models`; other platforms cache them next to the binary. Existing bundled models are reused.

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
- Live mic/speaker capture works on Windows/macOS/Linux.
- macOS 14.2+ captures system audio natively, without BlackHole or manual audio routing. Older Macs use the BlackHole fallback below.
- Linux arm64 users need to supply their own PortAudio native library.
- On macOS and Windows (e.g., Git Bash/MSYS), you may need to mark `txgui` as executable: `chmod +x txgui`.

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
- Captures system output via WASAPI loopback on Windows, Core Audio taps on macOS 14.2+, or a loopback input on older Macs/Linux.
- Optionally capture your mic simultaneously (`--mic-device <n>`, default `-1` disables). Speaker and mic audio are processed separately and merged chronologically into one transcript.
- Same defaults and output behavior as microphone mode (chunk `3s`, device `0`, summary disabled by default).
- Add `--summarize` to stream a summary via Ollama after transcription finishes.
- List speakers first with `./transcribe list-speakers`.
 - Use `--output-dir <folder>` to write the transcript somewhere other than the working directory.

### macOS native system audio capture (14.2+)

1) Open `Transcribe.app` and select **Speaker > System Audio (Native)**, or run `./tx speaker` from the CLI release.
2) Allow system audio recording when macOS asks. If permission was denied, enable it in **System Settings > Privacy & Security > Screen & System Audio Recording**, then restart the app. Command-line runs may require permission for your terminal app instead.
3) Play audio normally through your speakers or headphones. To also transcribe your voice, select a microphone or add `--mic-device <index>`; microphone access is a separate permission.

`list-speakers` shows one native source, device `0`, containing the system audio mix. Old BlackHole device indices are not valid in native mode. The app creates and removes a private Core Audio tap and capture device internally; it does not change your system output. If you previously used BlackHole, switch your output back to your normal speakers/headphones in Sound settings.

If changing audio devices changes the capture format, recording reports an error; stop and start a new recording after selecting the output. If audible speech produces only silence or blank output, check recording permission and restart.

To build a permission-aware app bundle locally:

```bash
dotnet publish transcribe.gui -c Release -r osx-arm64 --self-contained true -o published/macos-gui
bash scripts/package-macos.sh published/macos-gui txgui 'published/Transcribe.app'
open 'published/Transcribe.app'

dotnet publish transcribe -c Release -r osx-arm64 --self-contained true -o published/macos-cli
bash scripts/package-macos.sh published/macos-cli tx 'published/Transcribe CLI.app'
'published/Transcribe CLI.app/Contents/MacOS/tx' speaker
```

Use `osx-x64` for Intel. The packaging script adds both permission descriptions and an ad-hoc signature for local use. Public distribution still needs Developer ID signing and Apple notarization. Native capture is also available with `dotnet run`; permission attribution then depends on the terminal/host application. The packaged app is the recommended way to test the GUI's permission flow.

The macOS GUI saves live transcripts to `~/Documents/Transcribe` by default. Choose a different folder under **Save to** as needed. CLI transcripts use the current directory or `--output-dir`.

Validation on a Mac:

```bash
dotnet test transcribe.tests
bash scripts/test-macos-audio.sh
# Opt-in hardware test: plays two short tones and records them in memory.
TRANSCRIBE_TEST_MAC_AUDIO=1 dotnet test transcribe.tests --filter TestCategory=MacAudioLive
```

### Microphone echo cancellation on macOS

On macOS 14.2+, the **Speaker** tab has an optional **Reduce speaker echo in the microphone** setting. Select a physical microphone under **Mic (Optional)**, then enable it when listening through speakers. From the CLI:

```bash
./tx list-devices
./tx speaker --mic-device 1 --echo-cancellation
```

Replace `1` with your microphone's index. Echo cancellation is off by default; ordinary microphone capture continues to use PortAudio. When enabled, Apple's VoiceProcessingIO captures the selected mic and reduces sound leaking into it from the Mac's current default output. Cleaned mic audio and the direct system-audio stream are transcribed separately.

This requires **Microphone** permission in addition to system-audio recording permission. Choose hardware input/output devices; virtual devices and aggregate/Multi-Output Devices are rejected in this mode. Restart recording after changing the output device. Unsupported hardware produces an error rather than silently recording without cancellation.

Apple voice processing also applies noise processing and can lower playback volume, including the level of the system-audio capture. Transcribe requests minimum playback ducking and disables automatic mic gain. Leave echo cancellation off for headphones or when you want unprocessed microphone audio. It reduces echo; it cannot guarantee complete removal in every room or with every device. See [Apple's voice processing overview](https://developer.apple.com/videos/play/wwdc2023/10235/).

The optional acoustic test plays the same speech twice and compares raw and processed microphone pickup. Run in a quiet room with speakers audible; the reduction is normalized against the system-audio level to account for playback ducking:

```bash
TRANSCRIBE_TEST_MAC_AEC_DEVICE=1 dotnet test transcribe.tests --filter TestCategory=MacAecLive
```

### Older macOS system audio capture (BlackHole)
On macOS before 14.2, install a loopback device and route output to it.

1) Install BlackHole (GUI includes a bundled PKG under `installers/` or use `./transcribe install-blackhole`).
2) Open `Audio MIDI Setup` and create a `Multi-Output Device`.
3) In the Multi-Output Device, check both your speakers/headphones and `BlackHole 2ch`.
4) Set the Multi-Output Device as the system output in System Settings > Sound.
5) In Transcribe, select `BlackHole 2ch` as the speaker device.
6) Ensure sample rates match (usually 48k) for the Multi-Output Device and BlackHole in Audio MIDI Setup.

### Linux system audio capture (PulseAudio/PipeWire)
System audio capture on Linux uses a loopback/monitor input.

1) Install `pavucontrol` (or your desktop audio control panel).
2) Play audio, then open the device list and select a source named `Monitor of ...` for the speaker capture input.
3) If you do not see a monitor input, confirm PulseAudio/PipeWire is running and that your output device exposes a monitor source.
4) Use `./transcribe list-speakers` to find the monitor device index.

### Models
Supported values (downloaded on first use):
- `LargeV3Turbo`
- `LargeV3`
- `BaseEn`
- `TinyEn` (CLI default)

### Languages
- Default is `en`. Pass ISO codes (e.g., `en`, `fr`, `es`) or `auto` for language detection.

### Speaker diarization (offline)
Enable diarization to add speaker labels (e.g., `[S1]`, `[SPK:S2]`) to the transcript output.
- Requires a speaker-embedding ONNX model (default expected at `models/voxceleb_resnet293_LM.onnx` next to the binary).
- Use `--diarize` to enable it for `file`, `mic`, or `speaker` commands.
- Optional: `--diarization-model <path>` overrides the local model path.
- Optional: `--diarization-model-url <url>` downloads the model if it is missing.
- Optional: `--diarization-config <path>` uses a custom config JSON (default `models/speaker_diarization.json`).

Examples:
```bash
./transcribe file sample.wav --diarize --diarization-model .\models\voxceleb_resnet293_LM.onnx
./transcribe mic --diarize --diarization-model-url https://wespeaker-1256283475.cos.ap-shanghai.myqcloud.com/models/voxceleb/voxceleb_resnet293_LM.onnx
./transcribe speaker --diarize --mic-device 1
```

## Notes
- Network is required only for the initial model download (and Ollama summaries if enabled).
- Standard PCM WAV input works best; malformed headers will skip progress reporting.
- CLI live modes write transcript files in the working directory using GUID names; make sure it is writable. The macOS GUI defaults to `~/Documents/Transcribe`.
- On macOS, grant microphone permissions to the app if prompted.
- Audio capture uses WASAPI (Windows), Core Audio taps (macOS 14.2+ system audio), Apple voice processing (macOS echo cancellation), or PortAudio (ordinary macOS microphones, older Macs, and Linux).
