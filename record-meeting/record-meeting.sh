#!/bin/sh
set -eu
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
watcher="$script_dir/../bin/record-meeting-watcher"
if [ ! -x "$watcher" ]; then
    printf 'Native watcher is missing: %s\nBuild and install it from the transcribe repository.\n' "$watcher" >&2
    exit 1
fi
exec "$watcher" "$@"
