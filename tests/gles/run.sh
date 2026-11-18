#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 3 || ! -d $1 ]]; then
    echo "Usage: $0 IA32_GLIBC_ROOTFS {fbdev|wayland|kmsdrm} {doom|supertux|supertuxkart}" >&2
    exit 2
fi

here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
rootfs=$(cd "$1" && pwd)
cd "$repo"

cli=${PODISH_CLI:-"$repo/Podish.Cli/bin/Release/net10.0/Podish.Cli.dll"}
if [[ ! -f $cli && -z ${PODISH_CLI:-} ]]; then
    dotnet build Podish.Cli -c Release --nologo
fi

case $2 in
    fbdev)
        display=(--fbdev --fbdev-size 640x480
                 -e SDL_VIDEODRIVER=offscreen -e PODISH_EGL_FBDEV_PBUFFER=1)
        ;;
    kmsdrm)
        engine_log=$(mktemp)
        trap 'rm -f "$engine_log"' EXIT
        display=(--kmsdrm --log-level debug --log-file "$engine_log" -e SDL_VIDEODRIVER=kmsdrm)
        ;;
    wayland)
        engine_log=$(mktemp)
        trap 'rm -f "$engine_log"' EXIT
        display=(--wayland-server --log-level debug --log-file "$engine_log" -e SDL_VIDEODRIVER=wayland)
        ;;
    *)
        echo "Unknown display $2" >&2
        exit 2
        ;;
esac

game_env=(-e LD_PRELOAD=/usr/local/lib/podish-gles-game-probe.so)
case $3 in
    doom)
        game=(/usr/games/chocolate-doom -iwad /usr/share/games/doom/doom1.wad -warp 1 1 -nosound)
        ;;
    supertux)
        game=(/usr/games/supertux2 --renderer sdl --disable-sound
              /usr/share/games/supertux2/levels/world1/journey_begins.stl)
        ;;
    supertuxkart)
        if [[ $2 != kmsdrm ]]; then
            echo "The SuperTuxKart profile uses KMSDRM" >&2
            exit 2
        fi
        game_log=$(mktemp)
        trap 'rm -f "$engine_log" "$game_log"' EXIT
        display+=(-m 2G)
        game_env=(-e SDL_AUDIODRIVER=dummy)
        game=(/usr/local/bin/supertuxkart-gles-sse2 --log=nocolor --no-sound --race-now
              --track=lighthouse --kart=tux --numkarts=3 --ai=puffy,gavroche
              --screensize=640x480 --seed=23 --profile-time=30)
        ;;
    *)
        echo "Unknown game $3" >&2
        exit 2
        ;;
esac

run=(dotnet "$cli" run --rm --gles --rootfs "$rootfs" "${display[@]}" \
    -e SDL_VIDEO_GL_DRIVER=libGLESv2.so.2 -e SDL_RENDER_DRIVER=opengles2 -e SDL_RENDER_VSYNC=0 \
    -e ALSOFT_DRIVERS=null "${game_env[@]}" -- "${game[@]}")

if [[ $3 == supertuxkart ]]; then
    "${run[@]}" 2>&1 | tee "$game_log"
    if ! rg -q 'profile: Number of frames:' "$game_log"; then
        echo "SuperTuxKart did not complete its 30-second race profile" >&2
        exit 1
    fi
else
    "${run[@]}"
fi

if [[ $2 == wayland ]]; then
    if ! rg -q 'Wayland shm to_frame.*format=Xrgb8888' "$engine_log"; then
        echo "No GLES XRGB8888 frame reached the Wayland compositor" >&2
        exit 1
    fi
    echo "Wayland compositor received GLES frames"
fi

if [[ $2 == kmsdrm ]]; then
    frames=$(rg -c 'KMS EGL presented a GPU frame' "$engine_log" || true)
    if [[ ${frames:-0} -lt 8 ]]; then
        echo "Expected at least eight host KMS GPU swaps, observed ${frames:-0}" >&2
        tail -30 "$engine_log" >&2
        exit 1
    fi
    echo "Host EGL presented $frames KMS frames"
fi
