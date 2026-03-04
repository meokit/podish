#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
PROJ="$ROOT_DIR/Podish.Core.Native/Podish.Core.Native.csproj"
FIBERCPU_STATIC="$ROOT_DIR/Fiberish.X86/build_native/libfibercpu.a"
RID="${1:-osx-arm64}"
OUT_DIR="${2:-$ROOT_DIR/artifacts/podish-native/$RID}"
NUGET_ROOT="${NUGET_PACKAGES:-$HOME/.nuget/packages}"

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR/include"

export MSBUILDDISABLENODEREUSE=1
dotnet restore "$PROJ" -r "$RID" --nologo
dotnet publish "$PROJ" -c Release -r "$RID" -o "$OUT_DIR" --nologo --no-restore /p:PodishStaticNative=true
cp "$ROOT_DIR/Podish.Core.Native/podish.h" "$OUT_DIR/include/podish.h"

if [ ! -f "$FIBERCPU_STATIC" ]; then
  echo "error: required static library missing: $FIBERCPU_STATIC" >&2
  exit 1
fi
cp "$FIBERCPU_STATIC" "$OUT_DIR/"

# Fold fibercpu archive into podishcore archive so app-side only links podishcore.
if [ ! -f "$OUT_DIR/podishcore.a" ]; then
  echo "error: required static library missing: $OUT_DIR/podishcore.a" >&2
  exit 1
fi
if [[ "$RID" == osx* ]]; then
  /usr/bin/libtool -static -o "$OUT_DIR/podishcore.merged.a" "$OUT_DIR/podishcore.a" "$OUT_DIR/libfibercpu.a"
  mv "$OUT_DIR/podishcore.merged.a" "$OUT_DIR/podishcore.a"
else
  echo "error: archive merge for RID '$RID' is not implemented yet" >&2
  exit 1
fi
rm -f "$OUT_DIR/libfibercpu.a"

# SwiftPM `.linkedLibrary("podishcore")` maps to `libpodishcore.a`.
# NativeAOT output is `podishcore.a`; provide a conventional alias.
if [ -f "$OUT_DIR/podishcore.a" ] && [ ! -e "$OUT_DIR/libpodishcore.a" ]; then
  ln -s "podishcore.a" "$OUT_DIR/libpodishcore.a" 2>/dev/null || cp "$OUT_DIR/podishcore.a" "$OUT_DIR/libpodishcore.a"
fi

ILC_PKG_BASE="$NUGET_ROOT/runtime.$RID.microsoft.dotnet.ilcompiler"
if [ ! -d "$ILC_PKG_BASE" ]; then
  echo "error: NativeAOT runtime pack not found: $ILC_PKG_BASE" >&2
  exit 1
fi
ILC_PKG_DIR="$(ls -d "$ILC_PKG_BASE"/* 2>/dev/null | sort -V | tail -n 1)"
if [ -z "${ILC_PKG_DIR:-}" ] || [ ! -d "$ILC_PKG_DIR" ]; then
  echo "error: NativeAOT runtime pack version not found under: $ILC_PKG_BASE" >&2
  exit 1
fi

FRAMEWORK_DIR="$ILC_PKG_DIR/framework"
SDK_DIR="$ILC_PKG_DIR/sdk"

# NativeAOT static runtime/object dependencies required when embedding into Swift app.
for f in \
  "$FRAMEWORK_DIR/libSystem.Native.a" \
  "$FRAMEWORK_DIR/libSystem.IO.Compression.Native.a" \
  "$FRAMEWORK_DIR/libSystem.Net.Security.Native.a" \
  "$FRAMEWORK_DIR/libSystem.Security.Cryptography.Native.Apple.a" \
  "$FRAMEWORK_DIR/libSystem.Security.Cryptography.Native.OpenSsl.a" \
  "$SDK_DIR/libRuntime.WorkstationGC.a" \
  "$SDK_DIR/libeventpipe-disabled.a" \
  "$SDK_DIR/libstdc++compat.a" \
  "$SDK_DIR/libbootstrapperdll.o"
do
  if [ ! -f "$f" ]; then
    echo "error: required NativeAOT artifact missing: $f" >&2
    exit 1
  fi
  cp "$f" "$OUT_DIR/"
done

echo "Published static library to: $OUT_DIR"
ls -1 "$OUT_DIR" | sed 's/^/  - /'
