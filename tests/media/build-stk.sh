#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 1 ]]; then
    echo "Usage: bash $0 OUTPUT_DIRECTORY" >&2
    exit 2
fi
here=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$1"
out=$(cd "$1" && pwd)
engine=${CONTAINER_ENGINE:-docker}
archive=SuperTuxKart-1.4-src.tar.xz
context=$(mktemp -d)
container=
cleanup() {
    if [[ -n $container ]]; then
        "$engine" rm "$container" >/dev/null
    fi
    rm -rf -- "$context"
}
trap cleanup EXIT

if [[ ! -f $out/$archive ]]; then
    curl --fail --location --retry 3 --continue-at - \
        "https://github.com/supertuxkart/stk-code/releases/download/1.4/$archive" -o "$out/$archive.part"
    mv "$out/$archive.part" "$out/$archive"
fi
(cd "$out" && echo "9890392419baf4715313f14d5ad60746f276eed36eb580636caf44e2532c0f03  $archive" | sha256sum -c -)
# Docker/Podman need the archive inside the build context.
ln "$out/$archive" "$context/$archive" 2>/dev/null || cp "$out/$archive" "$context/"
cp "$here/Dockerfile.stk" "$context/"
"$engine" build --platform linux/386 --build-arg "BUILD_JOBS=${BUILD_JOBS:-6}" \
    --iidfile "$context/image-id" -f "$context/Dockerfile.stk" "$context"
container=$("$engine" create --platform linux/386 "$(cat "$context/image-id")")
"$engine" export -o "$out/stk-rootfs.tar.part" "$container"
mv "$out/stk-rootfs.tar.part" "$out/stk-rootfs.tar"
