#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
PROJ="$ROOT_DIR/Podish.Core.Native/Podish.Core.Native.csproj"
RID="${1:-osx-arm64}"
OUT_DIR="${2:-$ROOT_DIR/artifacts/podish-native/$RID}"

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR/include"

dotnet publish "$PROJ" -c Release -r "$RID" -o "$OUT_DIR" --nologo /p:PodishStaticNative=true
cp "$ROOT_DIR/Podish.Core.Native/podish.h" "$OUT_DIR/include/podish.h"

echo "Published static library to: $OUT_DIR"
ls -1 "$OUT_DIR" | sed 's/^/  - /'
