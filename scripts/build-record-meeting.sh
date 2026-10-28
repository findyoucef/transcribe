#!/bin/bash
set -euo pipefail

repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
publish_dir="${1:-$repo_dir/published/record-meeting}"
if [[ "$(uname -s)" != Darwin ]]; then
    echo 'Build the meeting watcher on macOS.' >&2
    exit 1
fi
case "$(uname -m)" in
    arm64) runtime=osx-arm64 ;;
    x86_64) runtime=osx-x64 ;;
    *) echo 'Unsupported Mac architecture.' >&2; exit 1 ;;
esac

# Homebrew's SDK uses system OpenSSL/Brotli at link time. Microsoft's SDK
# carries its own build dependencies. Unused dylibs are stripped from the result.
if command -v brew >/dev/null 2>&1; then
    for dependency in openssl@3 brotli; do
        dependency_prefix="$(brew --prefix "$dependency" 2>/dev/null || true)"
        if [[ -d "$dependency_prefix/lib" ]]; then
            export LIBRARY_PATH="$dependency_prefix/lib${LIBRARY_PATH:+:$LIBRARY_PATH}"
        fi
    done
fi

dotnet publish "$repo_dir/record-meeting" -c Release -r "$runtime" -o "$publish_dir" --nologo
codesign --force --sign - "$publish_dir/record-meeting-watcher"
codesign --verify --strict "$publish_dir/record-meeting-watcher"
"$publish_dir/record-meeting-watcher" self-test
