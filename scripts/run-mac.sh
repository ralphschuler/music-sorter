#!/bin/sh
set -eu
cd "$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd)"
sdk="$HOME/.local/share/music-sorter/dotnet11/dotnet"
if [ ! -x "$sdk" ]; then sdk=dotnet; fi
local_fpcalc="$HOME/.local/share/music-sorter/tools/fpcalc"
if [ -z "${MUSIC_SORTER_FPCALC:-}" ] && [ -x "$local_fpcalc" ]; then
    export MUSIC_SORTER_FPCALC="$local_fpcalc"
fi
exec "$sdk" run --no-restore --project src/MusicSorter.Desktop -- "$@"
