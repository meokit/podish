#!/usr/bin/env bash
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
out=${1:-"$here/../artifacts/podish-gles/guest"}
mkdir -p "$out"
out=$(cd "$out" && pwd)
cd "$here/guest"

cc=("${ZIG:-zig}" cc -target x86-linux-gnu.2.11 -O2 -std=gnu11 -Wall -Wextra
    -fPIC -fvisibility=hidden -shared -Wl,-z,defs -I include)
"${cc[@]}" -Wl,-soname,libGLESv2.so.2 transport.c gles.generated.c -pthread -o "$out/libGLESv2.so.2"
"${cc[@]}" -Wl,-soname,libEGL.so.1,-z,nodelete egl.c wayland.c "$out/libGLESv2.so.2" -ldl -pthread -o "$out/libEGL.so.1"
ln -sf libGLESv2.so.2 "$out/libGLESv2.so"
ln -sf libEGL.so.1 "$out/libEGL.so"
