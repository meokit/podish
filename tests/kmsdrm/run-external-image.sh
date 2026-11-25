#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 || ! -d $1 ]]; then
    echo "Usage: $0 IA32_GLIBC_ROOTFS [control|shader|state|sampling|cage|all]" >&2
    exit 2
fi

here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
rootfs=$(cd "$1" && pwd)
cd "$repo"

cli=${PODISH_CLI:-"$repo/Podish.Cli/bin/Release/net11.0/Podish.Cli.dll"}
if [[ ! -f $cli && -z ${PODISH_CLI:-} ]]; then
    dotnet build Podish.Cli -c Release --nologo
fi

case ${2:-all} in
    all) checks=(control shader state sampling cage) ;;
    control|shader|state|sampling|cage) checks=("$2") ;;
    *) echo "Unknown external image case $2" >&2; exit 2 ;;
esac

log=$(mktemp)
trap 'rm -f "$log"' EXIT
failed=0
for check in "${checks[@]}"; do
    printf 'Running external image case %s\n' "$check"
    if [[ $check == cage ]]; then
        PODISH_CLI="$cli" PODISH_REQUIRE_WAYLAND_GPU=1 "$repo/tests/gles/run.sh" "$rootfs" cage supertuxkart ||
            failed=1
    elif ! dotnet "$cli" run --rm --kmsdrm --gles --rootfs "$rootfs" \
            -- /usr/local/bin/podish-kmsdrm-external-image-probe "$check" 2>&1 | tee "$log" ||
         ! rg -Fq "External image probe passed ($check)" "$log"; then
        failed=1
    fi
done
exit "$failed"
