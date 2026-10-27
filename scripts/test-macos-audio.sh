#!/bin/bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
test_dir="$(mktemp -d)"
trap 'rm -rf "$test_dir"' EXIT
xcrun clang -fobjc-arc -g -fsanitize=address,undefined -Wall -Wextra -Wno-unused-parameter \
    -mmacosx-version-min=14.2 -framework Foundation -framework CoreAudio -framework AudioToolbox \
    "$repo_dir/transcribe.tests/MacAudioCaptureTests.m" -o "$test_dir/audio-tests"
"$test_dir/audio-tests"
