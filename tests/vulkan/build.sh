#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 1 ]]; then
    echo "Usage: $0 INSTALL_DIRECTORY" >&2
    exit 2
fi

here=$(cd "$(dirname "$0")" && pwd)
output=$1
build=$(mktemp -d)
trap 'rm -rf "$build"' EXIT
protocol="$(pkg-config --variable=pkgdatadir wayland-protocols)/stable/xdg-shell/xdg-shell.xml"
wayland-scanner client-header "$protocol" "$build/xdg-shell-client.h"
wayland-scanner private-code "$protocol" "$build/xdg-shell.c"

mkdir -p "$output"
"${CC:-cc}" -std=c11 -D_GNU_SOURCE -O2 -Wall -Wextra \
    $(pkg-config --cflags libdrm vulkan) "$here/vulkan-probe.c" \
    $(pkg-config --libs libdrm vulkan) -o "$output/podish-vulkan-probe"
"${CC:-cc}" -std=c11 -O2 -Wall -Wextra -I "$build" \
    $(pkg-config --cflags egl wayland-client wayland-egl) "$here/zink-probe.c" "$build/xdg-shell.c" \
    $(pkg-config --libs egl wayland-client wayland-egl) -o "$output/podish-zink-probe"
