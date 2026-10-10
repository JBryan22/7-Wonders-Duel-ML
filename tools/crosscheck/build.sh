#!/usr/bin/env sh
# Builds the cross-check driver (ticket 11): fetches indjev99's C++ engine at a pinned commit into .cache/ (gitignored,
# third-party code kept apart from ours), checks its licence is still MIT, and compiles driver.cpp against its
# src/game/ sources only (no GUI, no agents). Needs git and g++ (C++17). Linux; on Windows run it in a Linux container:
#   docker run --rm -v "<repo>:/repo" -w /repo mcr.microsoft.com/dotnet/sdk:10.0 sh -c \
#     "apt-get update -qq && apt-get install -y -qq g++ >/dev/null && tools/crosscheck/build.sh && \
#      dotnet run --project engine/src/Cli -c Release -- crosscheck --games 10000"
set -eu

REPO_URL=https://github.com/indjev99/7-Wonders-Duel-AI.git
COMMIT=01ed7a5f1b695c410800023ac3d26097600ffc32

here=$(cd "$(dirname "$0")" && pwd)
cache="$here/.cache"
src="$cache/indjev"
mkdir -p "$cache"

if [ ! -d "$src/.git" ]; then
    git clone --quiet "$REPO_URL" "$src"
fi
git -C "$src" fetch --quiet origin "$COMMIT" 2>/dev/null || true
git -C "$src" -c advice.detachedHead=false checkout --quiet "$COMMIT"

if ! head -n 1 "$src/LICENSE" | grep -q "MIT License"; then
    echo "indjev99's engine is no longer MIT-licensed at $COMMIT; stopping" >&2
    exit 1
fi

g++ -std=c++17 -O2 -w -I "$src/src" -o "$cache/7wd-cpp-driver" "$here/driver.cpp" "$src"/src/game/*.cpp
echo "built $cache/7wd-cpp-driver (indjev99/7-Wonders-Duel-AI @ $COMMIT)"
