#!/usr/bin/env bash
set -euo pipefail
if [[ $# -ne 1 || "$(uname -sm)" != "Darwin arm64" ]]; then
    printf 'Usage on native macOS arm64: %s OUTPUT\n' "$0" >&2
    exit 2
fi
output=$(mkdir -p "$1" && cd "$1" && pwd)
commit=ce96e698f1820b9d8db47e87c501ecb89119d7ce
if [[ ! -d "$output/source/.git" ]]; then
    git clone --filter=blob:none https://github.com/ish-app/ish.git "$output/source"
fi
git -C "$output/source" checkout --detach "$commit"
git -C "$output/source" submodule update --init --recursive
CC=${CC:-/usr/bin/clang} meson setup "$output/build" "$output/source" --buildtype=release
meson compile -C "$output/build"
printf '%s\n' "$commit" > "$output/commit"
file "$output/build/ish"
