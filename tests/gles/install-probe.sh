#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 1 || ! -d $1 ]]; then
    echo "Usage: $0 IA32_GLIBC_ROOTFS" >&2
    exit 2
fi

here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
guest=${PODISH_GLES_GUEST_DIR:-"$repo/artifacts/podish-gles/guest"}
dest=$1/usr/local/bin
mkdir -p "$dest"
cc=("${ZIG:-zig}" cc -target x86-linux-gnu.2.11 -O2 -Wall -Wextra
    -I "$repo/graphics/guest/include")
"${cc[@]}" "$here/fbdev.c" -L "$guest" -lEGL -lGLESv2 -ldl -o "$dest/podish-gles-fbdev-probe"
lib=$1/usr/local/lib
mkdir -p "$lib"
"${cc[@]}" -fPIC -shared -fvisibility=hidden "$here/game-probe.c" -ldl -o "$lib/podish-gles-game-probe.so"
