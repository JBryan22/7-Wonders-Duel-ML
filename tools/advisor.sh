#!/bin/sh
# Starts the Advisor Window (spec A8) and opens it in the browser: tools/advisor.sh [--port N] [--data DIR] [--no-browser]
# Ctrl+C stops it; games are saved after every entry.
exec dotnet run --project "$(dirname "$0")/../engine/src/Advisor" -c Release -- "$@"
