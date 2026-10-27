#!/bin/bash
set -euo pipefail

if [[ $# -lt 3 || $# -gt 4 ]]; then
    echo "Usage: $0 PUBLISH_DIRECTORY EXECUTABLE OUTPUT.app [VERSION]" >&2
    exit 1
fi

publish_dir="$1"
executable="$2"
bundle="$3"
version="${4:-1.0.0}"
if [[ "$executable" != tx && "$executable" != txgui ]]; then
    echo "Executable must be tx or txgui." >&2
    exit 1
fi
if [[ "$bundle" != *.app || -e "$bundle" ]]; then
    echo "Choose a new output path ending in .app (existing bundles are not overwritten)." >&2
    exit 1
fi
if [[ ! -f "$publish_dir/$executable" || ! -f "$publish_dir/libtranscribe_audio.dylib" ]]; then
    echo "Publish on macOS first; the executable and native audio library must both be present." >&2
    exit 1
fi

mkdir -p "$bundle/Contents/MacOS"
ditto "$publish_dir" "$bundle/Contents/MacOS"
chmod +x "$bundle/Contents/MacOS/$executable"

plist="$bundle/Contents/Info.plist"
plutil -create xml1 "$plist"
plutil -insert CFBundleIdentifier -string "org.transcribe.$executable" "$plist"
plutil -insert CFBundleName -string "Transcribe" "$plist"
plutil -insert CFBundleDisplayName -string "Transcribe" "$plist"
plutil -insert CFBundleExecutable -string "$executable" "$plist"
plutil -insert CFBundlePackageType -string APPL "$plist"
plutil -insert CFBundleShortVersionString -string "$version" "$plist"
plutil -insert CFBundleVersion -string "$version" "$plist"
plutil -insert LSMinimumSystemVersion -string 12.0 "$plist"
plutil -insert NSHighResolutionCapable -bool true "$plist"
plutil -insert NSAudioCaptureUsageDescription -string "Transcribe records system audio to turn speech from your apps into text." "$plist"
plutil -insert NSMicrophoneUsageDescription -string "Transcribe records your microphone when you choose microphone or conversation transcription." "$plist"
if [[ "$executable" == tx ]]; then
    plutil -insert LSBackgroundOnly -bool true "$plist"
fi

# A local ad-hoc signature gives the bundle integrity and a bundle identity.
# Public distribution still needs Developer ID signing and notarization.
codesign --force --deep --sign - "$bundle"
codesign --verify --deep --strict "$bundle"
echo "Created $bundle"
