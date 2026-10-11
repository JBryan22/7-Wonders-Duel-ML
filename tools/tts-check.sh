#!/bin/sh
# Reads the Tabletop Simulator table (read-only) and checks it against the committed practice save, place by place
# (ticket 32). First load docs/research/tts-mod/ml-practice.json in TTS. The snapshot is saved to runs/tts-live-snapshot.json.
root="$(dirname "$0")/.."
mkdir -p "$root/runs"
exec dotnet run --project "$root/engine/src/Advisor" -c Release -- tts-snapshot \
  --compare "$root/tts/fixtures/ml-practice.snapshot.json" --count 5 --save "$root/runs/tts-live-snapshot.json" "$@"
