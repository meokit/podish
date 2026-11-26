#!/usr/bin/env bash
set -euo pipefail

case "$(uname -m)" in
    arm64|aarch64) ;;
    *) printf '%s\n' 'The native differential test requires an AArch64 host.' >&2; exit 77 ;;
esac

source_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_dir="$(cd -- "$source_dir/../.." && pwd)"
build_dir="$(mktemp -d)"
trap 'rm -rf -- "$build_dir"' EXIT

"${CXX:-clang++}" -std=c++23 -O2 -Wall -Wextra -Wpedantic \
    -I"$repo_dir/libfibercpu/src" \
    "$repo_dir/libfibercpu/src/aarch64/aarch64.cpp" \
    "$source_dir/native-differential.cpp" -o "$build_dir/native-differential"
"$build_dir/native-differential"
