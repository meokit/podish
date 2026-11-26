#!/usr/bin/env bash
set -euo pipefail

if [[ $# -eq 0 ]]; then
    echo "Usage: run.sh dotnet /absolute/path/to/Podish.Cli.dll" >&2
    exit 2
fi

source_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
test_dir="$(mktemp -d)"
trap 'rm -rf -- "$test_dir"' EXIT
cli=("$@")
"$source_dir/build.sh" "$test_dir/rootfs"
cd -- "$test_dir"

run_guest() {
    local expected="$1"
    shift
    local output
    output=$("${cli[@]}" run --rm --rootfs "$test_dir/rootfs" "$@" | tr -d '\r')
    if [[ "$output" != "$expected" ]]; then
        echo "$1 returned '$output', expected '$expected'" >&2
        return 1
    fi
    echo "$1 passed"
}

run_guest "AArch64 startup ok" /startup ok
run_guest "AArch64 startup ok" /high-startup ok
run_guest "AArch64 startup ok" /pie-startup ok
run_guest "AArch64 startup ok" /dynamic-startup ok
run_guest "AArch64 startup ok" /exec-startup
run_guest "AArch64 startup ok" /exec-aarch64
run_guest "i386 startup ok" /exec-i386
