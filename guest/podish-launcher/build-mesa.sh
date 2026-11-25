#!/usr/bin/env bash
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="$(mktemp -d -t podish-zink-XXXXXX)"
trap 'rm -rf "$work"' EXIT

version=25.0.7
archive="$work/mesa-$version.tar.xz"
curl --fail --location --retry 3 --output "$archive" "https://archive.mesa3d.org/mesa-$version.tar.xz"
printf '%s  %s\n' 592272df3cf01e85e7db300c449df5061092574d099da275d19e97ef0510f8a6 "$archive" | sha256sum --check
tar -xJf "$archive" -C "$work"

source="$work/mesa-$version"
patch -d "$source" -p1 < "$here/mesa-zink-wayland.patch"
patch -d "$source" -p1 < "$here/mesa-venus-explicit-sync.patch"
patch -d "$source" -p1 < "$here/mesa-virgl-wayland.patch"
patch --batch --forward --fuzz=0 -d "$source" -p1 < "$here/mesa-venus-ring-order.patch"
patch --batch --forward --fuzz=0 -d "$source" -p1 < "$here/mesa-zink-gl43-core.patch"
prefix=/usr/local/lib/podish-zink
meson setup "$work/build" "$source" \
    --buildtype=release --prefix="$prefix" --libdir=lib --strip \
    -Dplatforms=wayland -Dgallium-drivers=zink,virgl -Dvulkan-drivers=virtio -Dglx=disabled \
    -Dopengl=true -Degl=enabled -Dgles1=disabled -Dgles2=disabled \
    -Dglvnd=enabled -Dglvnd-vendor-name=podish_zink -Dgbm=disabled \
    -Dllvm=disabled -Dvalgrind=disabled -Dvideo-codecs=
meson compile -C "$work/build"
meson install -C "$work/build" --tags runtime --no-rebuild

# Meson names the manifest after the build's CPU spelling (e.g. i686).
icd_dir="$prefix/share/vulkan/icd.d"
mv "$icd_dir"/virtio_icd.*.json "$icd_dir/virtio_icd.json"
