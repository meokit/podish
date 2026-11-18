#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 ]]; then
    echo "Usage: $0 ROOTFS {doom|supertux} [GAME_ARGS...]" >&2
    exit 2
fi

rootfs=$1
game=$2
shift 2

case "$game" in
    doom)
        game_command=(/usr/games/prboom -iwad /usr/share/games/doom/doom1.wad
            -width 640 -height 480 -vidmode 8bit -nosound)
        ;;
    supertux)
        game_command=(/usr/games/supertux --sdl --fullscreen --disable-sound --disable-music)
        ;;
    *)
        echo "Unknown game: $game" >&2
        exit 2
        ;;
esac

repo=$(cd -- "$(dirname -- "$0")/../.." && pwd)
if [[ -n ${PODISH_CLI:-} ]]; then
    cli=(dotnet "$PODISH_CLI")
else
    cli=(dotnet run --project "$repo/Podish.Cli/Podish.Cli.csproj" --configuration Release --)
fi

exec "${cli[@]}" run --rm --fbdev --fbdev-size 640x480 --rootfs "$rootfs" \
    -e SDL_VIDEODRIVER=fbcon -e SDL_FBDEV=/dev/fb0 -e SDL_AUDIODRIVER=dummy \
    -- "${game_command[@]}" "$@"
