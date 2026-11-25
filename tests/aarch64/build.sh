#!/usr/bin/env bash
set -euo pipefail

source_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
rootfs="${1:?Usage: build.sh ROOTFS_DIRECTORY}"
build_dir="$(mktemp -d)"
trap 'rm -rf -- "$build_dir"' EXIT
mkdir -p -- "$rootfs"

compiler="${CLANG:-clang}"
linker="${LD_LLD:-ld.lld}"
for name in startup interpreter exec-startup; do
    "$compiler" --target=aarch64-linux-gnu -c "$source_dir/$name.S" -o "$build_dir/$name.o"
done

link() {
    "$linker" -m aarch64linux -e _start "$@"
}

link "$build_dir/startup.o" -o "$rootfs/startup"
link -pie --no-dynamic-linker "$build_dir/startup.o" -o "$rootfs/pie-startup"
link -pie --dynamic-linker /interpreter "$build_dir/startup.o" -o "$rootfs/dynamic-startup"
link -shared "$build_dir/interpreter.o" -o "$rootfs/interpreter"
link "$build_dir/exec-startup.o" -o "$rootfs/exec-startup"

"$compiler" --target=aarch64-linux-gnu -DEXEC_I386 -c "$source_dir/exec-startup.S" -o "$build_dir/exec-i386.o"
link "$build_dir/exec-i386.o" -o "$rootfs/exec-i386"
"$compiler" --target=i386-linux-gnu -c "$source_dir/startup-i386.S" -o "$build_dir/startup-i386.o"
"$compiler" --target=i386-linux-gnu -DEXEC_AARCH64 -c "$source_dir/startup-i386.S" -o "$build_dir/exec-aarch64.o"
"$linker" -m elf_i386 -e _start "$build_dir/startup-i386.o" -o "$rootfs/startup-i386"
"$linker" -m elf_i386 -e _start "$build_dir/exec-aarch64.o" -o "$rootfs/exec-aarch64"
