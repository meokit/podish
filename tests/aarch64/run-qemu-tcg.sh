#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 ]]; then
    echo "Usage: run-qemu-tcg.sh QEMU_CHECKOUT dotnet /absolute/path/to/Podish.Cli.dll" >&2
    exit 2
fi

qemu="$(cd -- "$1" && pwd)"
shift
cli=("$@")
tcg="$qemu/tests/tcg"
if [[ ! -f "$tcg/arm/fcvt.c" || ! -f "$tcg/multiarch/fnmsub.c" ]]; then
    echo "QEMU checkout is missing the selected TCG sources" >&2
    exit 2
fi

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
rootfs="$work/rootfs"
mkdir -- "$rootfs"
compiler="${ZIG:-zig}"
flags=(-target aarch64-linux-musl -static -O2 -frounding-math)

case "$(uname -s)/$(uname -m)" in
    Linux/aarch64|Linux/arm64) native_arm64=true ;;
    *) native_arm64=false ;;
esac

run_native() {
    if "$native_arm64"; then
        LC_ALL=C "$rootfs/$1"
    else
        docker --context "$oracle_context" run --rm --pull=never --platform linux/arm64 \
            --env LC_ALL=C --volume "$rootfs:/oracle:ro" --entrypoint "/oracle/$1" "$oracle_image"
    fi
}

if ! "$native_arm64"; then
    oracle_context="${PODISH_ORACLE_DOCKER_CONTEXT:-orbstack}"
    oracle_image="${PODISH_ORACLE_DOCKER_IMAGE:-arm64v8/debian:trixie-slim}"
    oracle_platform="$(docker --context "$oracle_context" info --format '{{.OSType}} {{.Architecture}}')"
    case "$oracle_platform" in
        "linux aarch64"|"linux arm64") ;;
        *)
            echo "The oracle needs a native Linux arm64 Docker daemon. Found $oracle_platform." >&2
            exit 2
            ;;
    esac
fi

build_test() {
    local name="$1"
    shift
    "$compiler" cc "${flags[@]}" "$@" -lm -o "$rootfs/$name"
}

build_test fcvt "$tcg/arm/fcvt.c"
build_test pcalign-a64 "$tcg/aarch64/pcalign-a64.c"
for name in float_convd float_convs float_madds; do
    build_test "$name" "$tcg/multiarch/$name.c" "$tcg/multiarch/libs/float_helpers.c"
done
for name in overflow sha1 sha512; do
    build_test "$name" "$tcg/multiarch/$name.c"
done
# Clang otherwise contracts -fma(...) into a negated fused operation with
# different rounding. The upstream test expects the libm result before negation.
build_test fnmsub -fno-builtin-fma "$tcg/multiarch/fnmsub.c"

cd -- "$work"
failed=0
for name in fcvt float_convd float_convs float_madds fnmsub overflow pcalign-a64 sha1 sha512; do
    native_status=0
    podish_status=0
    run_native "$name" > "$name.native" 2> "$name.native.err" || native_status=$?
    "${cli[@]}" run --rm --env LC_ALL=C --rootfs "$rootfs" "/$name" \
        > "$name.podish.raw" 2> "$name.podish.err" || podish_status=$?
    tr -d '\r' < "$name.podish.raw" > "$name.podish"

    if [[ "$native_status" -ne 0 || "$podish_status" -ne 0 ]]; then
        echo "$name failed (Linux exit $native_status, Podish exit $podish_status)" >&2
        cat "$name.native.err" "$name.podish.err" >&2
        sed -n '1,10p' "$name.native" "$name.podish" >&2
        failed=1
    elif ! diff -u "$name.native" "$name.podish" > "$name.diff"; then
        echo "$name stdout differs" >&2
        sed -n '1,60p' "$name.diff" >&2
        failed=1
    else
        printf '%s passed (%s matching lines)\n' "$name" "$(awk 'END { print NR }' "$name.native")"
    fi
done
exit "$failed"
