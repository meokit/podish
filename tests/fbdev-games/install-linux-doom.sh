#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 2 ]]; then
    echo "Usage: $0 X11_ROOTFS GAMES_ROOTFS" >&2
    exit 2
fi

x11_rootfs=$(cd -- "$1" && pwd)
wad="$2/usr/share/games/doom/doom1.wad"
x11_headers=${X11_HEADERS:-/usr/include}
if [[ $(uname -s) == Darwin ]]; then
    x11_headers=${X11_HEADERS:-/opt/X11/include}
fi
if [[ ! -f "$x11_headers/X11/extensions/XShm.h" ||
      ! -f "$x11_rootfs/usr/lib/libX11.so.6" ||
      ! -f "$x11_rootfs/usr/lib/libXext.so.6" ||
      ! -f "$wad" ]]; then
    echo "The X11 rootfs, games rootfs, and X11 development headers are required." >&2
    exit 2
fi

x11_headers=$(cd -- "$x11_headers" && pwd)

work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT

commit=a77dfb96cb91780ca334d0d4cfd86957558007e0
archive_sha256=dc820b68a56cf174750c6c2db27109e4ac62d6694af3bdf6ced0a92cdb80a73b
archive="$work/linux-doom.tar.gz"
curl --fail --silent --show-error --location --retry 3 \
    "https://codeload.github.com/id-Software/DOOM/tar.gz/$commit" --output "$archive"
printf '%s  %s\n' "$archive_sha256" "$archive" | shasum -a 256 --check
tar -xzf "$archive" -C "$work"
source_dir="$work/DOOM-$commit/linuxdoom-1.10"

mkdir -p "$source_dir/linux" "$work/include"
# The published source uses a legacy errno header and declaration.
printf '#include <errno.h>\n' > "$work/include/errnos.h"
export DOOM_BUILD_INCLUDE="$work/include" DOOM_X11_HEADERS="$x11_headers" DOOM_X11_ROOTFS="$x11_rootfs"
make -C "$source_dir" -j 4 \
    'CC=zig cc -target x86-linux-gnu.2.11' \
    'CFLAGS=-O2 -DNORMALUNIX -DLINUX -fcommon -std=gnu89 -Wno-implicit-function-declaration -Wno-int-conversion -Wno-incompatible-pointer-types -include errno.h -I"$$DOOM_BUILD_INCLUDE" -I"$$DOOM_X11_HEADERS"' \
    'LDFLAGS=-L"$$DOOM_X11_ROOTFS/usr/lib" -L"$$DOOM_X11_ROOTFS/lib"' \
    'LIBS="$$DOOM_X11_ROOTFS/usr/lib/libXext.so.6" "$$DOOM_X11_ROOTFS/usr/lib/libX11.so.6" -lm'

mkdir -p "$x11_rootfs/usr/local/bin" "$x11_rootfs/usr/share/games/doom"
install -m 755 "$source_dir/linux/linuxxdoom" "$x11_rootfs/usr/local/bin/linuxxdoom"
install -m 644 "$wad" "$x11_rootfs/usr/share/games/doom/doom1.wad"
