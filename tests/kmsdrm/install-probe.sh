#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 1 || ! -d $1 ]]; then
    echo "Usage: $0 IA32_GLIBC_ROOTFS" >&2
    exit 2
fi

here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
rootfs=$(cd "$1" && pwd)
guest=${PODISH_GLES_GUEST_DIR:-"$repo/artifacts/podish-gles/guest"}
drm_headers=${DRM_HEADERS:-"$rootfs/usr/include"}
gbm_headers=${GBM_HEADERS:-"$rootfs/usr/include"}
drm_library=${DRM_LIBRARY:-"$rootfs/usr/lib/i386-linux-gnu/libdrm.so.2"}
dest="$rootfs/usr/local/bin"
mkdir -p "$dest"
"${ZIG:-zig}" cc -target x86-linux-gnu.2.11 -O2 -Wall -Wextra \
    -I "$drm_headers" -I "$drm_headers/libdrm" -I "$gbm_headers" \
    "$here/probe.c" -Wl,--allow-shlib-undefined "$drm_library" -L "$guest" -lgbm -o "$dest/podish-kmsdrm-probe"
"${ZIG:-zig}" cc -target x86-linux-gnu.2.11 -O2 -Wall -Wextra \
    -I "$drm_headers" -I "$drm_headers/libdrm" -I "$gbm_headers" -I "$repo/graphics/guest/include" \
    "$here/egl-probe.c" -Wl,--allow-shlib-undefined "$drm_library" -L "$guest" -lgbm -lEGL -lGLESv2 \
    -o "$dest/podish-kmsdrm-egl-probe"
"${ZIG:-zig}" cc -target x86-linux-gnu.2.11 -O2 -Wall -Wextra \
    -I "$drm_headers" -I "$drm_headers/libdrm" -I "$gbm_headers" -I "$repo/graphics/guest/include" \
    "$here/image-probe.c" -Wl,--allow-shlib-undefined "$drm_library" -L "$guest" -lgbm -lEGL -lGLESv2 \
    -o "$dest/podish-kmsdrm-image-probe"
"${ZIG:-zig}" cc -target x86-linux-gnu.2.11 -O2 -Wall -Wextra \
    -I "$gbm_headers" -I "$repo/graphics/guest/include" \
    "$here/external-image-probe.c" -Wl,--allow-shlib-undefined -L "$guest" -lgbm -lEGL -lGLESv2 \
    -o "$dest/podish-kmsdrm-external-image-probe"
"${ZIG:-zig}" cc -target x86-linux-gnu.2.11 -O2 -Wall -Wextra \
    "$here/compositor-probe.c" -o "$dest/podish-kmsdrm-compositor-probe"
