#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 1 || ! -e "${1:-}/lib/ld-linux.so.2" ]]; then
    echo "Usage: $0 I386_ROOTFS (created by build-rootfs.sh)" >&2
    exit 2
fi

script_dir=$(cd -- "$(dirname -- "$0")" && pwd)
rootfs=$(cd -- "$1" && pwd)
staging=$(mktemp -d)
trap 'rm -rf -- "$staging"' EXIT

download() {
    local url=$1 checksum=$2
    local target="$staging/${url##*/}"
    curl --fail --silent --show-error --location --retry 3 "$url" --output "$target"
    printf '%s  %s\n' "$checksum" "$target" | shasum -a 256 --check >&2
    printf '%s\n' "$target"
}

rpm=$(download https://www.libsdl.org/release/SDL-1.0.8-1.i386.rpm \
    8c2d29d85e19c9b29b805de4c0576a8b287f7b562df8e2f2404fa4bd39d9d215)
source=$(download https://www.libsdl.org/release/SDL-1.0.8.tar.gz \
    9587b0134cfca2c793de04eb5eb11f4b88702275611e37d51b6f8bb39fdc7ea1)
audiofile=$(download https://archive.debian.org/debian/pool/main/a/audiofile/libaudiofile0_0.2.6-8_i386.deb \
    66848b8495129ec6dabf4c6797432d951393cd3ef711e8b5574ecc90605cd5c4)
esd=$(download https://archive.debian.org/debian/pool/main/e/esound/libesd0_0.2.41-8_i386.deb \
    f02d8c8c3f2e702296c550a83f4e90ceb41a554ab593b697f41681f4ef591a0c)

bsdtar -xf "$rpm" -C "$rootfs"
tar -xzf "$source" -C "$staging"
for package in "$audiofile" "$esd"; do
    data=$(ar t "$package" | sed -n '/^data\.tar\./p')
    ar p "$package" "$data" | tar -xf - -C "$rootfs"
done

mkdir -p "$rootfs/usr/local/bin"
zig cc -target x86-linux-gnu.2.11 -O2 -I"$staging/SDL-1.0.8/include" \
    "$script_dir/sdl10-probe.c" "$rootfs/usr/lib/libSDL-1.0.so.0" \
    -Wl,--allow-shlib-undefined -o "$rootfs/usr/local/bin/sdl10-probe"
