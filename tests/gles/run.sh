#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 3 || ! -d $1 ]]; then
    echo "Usage: $0 IA32_GLIBC_ROOTFS {fbdev|wayland|kmsdrm|cage} {doom|supertux|supertuxkart}" >&2
    exit 2
fi

if [[ $2 == cage && $3 != supertuxkart ]]; then
    echo "The Cage run currently supports SuperTuxKart" >&2
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
    kmsdrm|cage)
        engine_log=$(mktemp)
        trap 'rm -f "$engine_log"' EXIT
        display=(--kmsdrm --log-level debug --log-file "$engine_log")
        if [[ $2 == cage ]]; then
            display+=(--user 1000:1000 -e LIBSEAT_BACKEND=builtin -e SEATD_VTBOUND=0 -e WLR_BACKENDS=drm
                      -e WLR_DRM_DEVICES=/dev/dri/card0 -e WLR_RENDERER=gles2 -e SDL_VIDEODRIVER=wayland)
            present='KMS EGL presented a compositor texture'
        else
            display+=(-e SDL_VIDEODRIVER=kmsdrm)
            present='KMS EGL presented a GPU frame'
        fi
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
        if [[ $2 != kmsdrm && $2 != cage ]]; then
            echo "The SuperTuxKart profile uses KMSDRM or Cage" >&2
            exit 2
        fi
        game_log=$(mktemp)
        trap 'rm -f "$engine_log" "$game_log"' EXIT
        display+=(-m 2G)
        game_env=(-e SDL_AUDIODRIVER=dummy)
        game=(/usr/local/bin/supertuxkart-gles-sse2 --log=nocolor --no-sound --race-now
              --track=lighthouse --kart=tux --numkarts=3 --ai=puffy,gavroche
              --screensize=640x480 --seed=23 --profile-time=30)
        if [[ $2 == cage ]]; then
            cage_tmp=$(mktemp -d)
            trap 'rm -f "$engine_log" "$game_log"; rm -rf "$cage_tmp"' EXIT
            mkdir -m 1777 "$cage_tmp/tmp" "$cage_tmp/tmp/.X11-unix"
            mkdir -m 0777 "$cage_tmp/tmp/runtime" "$cage_tmp/tmp/home"
            display+=(-v "$cage_tmp/tmp:/tmp" -e XDG_RUNTIME_DIR=/tmp/runtime)
            game_env+=(-e HOME=/tmp/home -e SUPERTUXKART_SAVEDIR=/tmp/home)
            game=(/usr/bin/cage -d -- "${game[@]}")
        fi
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

if [[ $2 == kmsdrm || $2 == cage ]]; then
    frames=$(rg -c "$present" "$engine_log" || true)
    if [[ ${frames:-0} -lt 8 ]]; then
        echo "Expected at least eight host KMS GPU swaps, observed ${frames:-0}" >&2
        tail -30 "$engine_log" >&2
        exit 1
    fi
    echo "Host EGL presented $frames KMS frames"
    if [[ $2 == cage ]]; then
        gpu_frames=$(rg -c 'Wayland EGL submitted a GPU buffer' "$engine_log" || true)
        echo "Guest Wayland EGL submitted ${gpu_frames:-0} GPU frames"
        if [[ ${PODISH_REQUIRE_WAYLAND_GPU:-0} == 1 && ${gpu_frames:-0} -lt 8 ]]; then
            echo "Expected at least eight GPU submissions to Cage" >&2
            exit 1
        fi
    fi
fi
