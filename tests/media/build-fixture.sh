#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 1 ]]; then
    echo "Usage: bash $0 OUTPUT_DIRECTORY" >&2
    exit 2
fi
root=$(cd "$(dirname "$0")/../.." && pwd)
mkdir -p "$1"
out=$(cd "$1" && pwd)
engine=${CONTAINER_ENGINE:-docker}
context=$(mktemp -d)
container=
cleanup() {
    if [[ -n $container ]]; then
        "$engine" rm "$container" >/dev/null
    fi
    rm -rf -- "$context"
}
trap cleanup EXIT

# Stage only the fixture sources, not the worktree's build outputs.
mkdir -p "$context"/{tests/{media,kmsdrm,gles},graphics,spec/wayland}
cp "$root"/tests/media/{Dockerfile,*-probe.c} "$context/tests/media/"
cp "$root"/tests/kmsdrm/{probe,egl-probe}.c "$context/tests/kmsdrm/"
cp "$root"/tests/gles/fbdev.c "$context/tests/gles/"
cp -r "$root"/graphics/guest "$context/graphics/"
cp "$root"/spec/wayland/xdg-shell.xml "$context/spec/wayland/"

"$engine" build --platform linux/386 -f "$context/tests/media/Dockerfile" \
    --iidfile "$context/image-id" "$context"
container=$("$engine" create --platform linux/386 "$(cat "$context/image-id")")
"$engine" export -o "$out/rootfs.tar" "$container"
mkdir -p "$out/guest-graphics"
"$engine" cp "$container:/opt/podish-graphics/." "$out/guest-graphics/"
