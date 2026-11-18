#!/usr/bin/env bash
set -euo pipefail
if [[ $# -lt 1 || $# -gt 2 ]]; then
    echo "Usage: $0 ROOTFS [desktop|smoke|doom]" >&2
    exit 2
fi
repo=$(cd -- "$(dirname -- "$0")/../.." && pwd)
if [[ -n ${PODISH_CLI:-} ]]; then
    cli=(dotnet "$PODISH_CLI")
else
    cli=(dotnet run --project "$repo/Podish.Cli/Podish.Cli.csproj" --configuration Release --)
fi
exec "${cli[@]}" run --rm --fbdev --fbdev-size 800x600 --rootfs "$1" -- /usr/local/bin/x11-session "${2:-desktop}"
