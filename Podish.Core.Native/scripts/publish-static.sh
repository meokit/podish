#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
PROJ="$ROOT_DIR/Podish.Core.Native/Podish.Core.Native.csproj"
RID="${1:-osx-arm64}"
OUT_DIR="${2:-$ROOT_DIR/artifacts/podish-native/$RID}"
NUGET_ROOT="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
TFM="net10.0"

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR/include"

export MSBUILDDISABLENODEREUSE=1
dotnet build "$ROOT_DIR/Fiberish.X86/Fiberish.X86.csproj" -c Release --nologo /p:PodishStaticNative=true /p:RuntimeIdentifier="$RID"
dotnet restore "$PROJ" -r "$RID" --nologo /p:TargetFramework="$TFM" /p:CheckEolTargetFramework=false /p:CheckEolWorkloads=false
dotnet publish "$PROJ" -f "$TFM" -c Release -r "$RID" -o "$OUT_DIR" --nologo /p:PodishStaticNative=true /p:CheckEolTargetFramework=false /p:CheckEolWorkloads=false

cp "$ROOT_DIR/Podish.Core.Native/podish.h" "$OUT_DIR/include/podish.h"

FIBERCPU_STATIC="$ROOT_DIR/Fiberish.X86/build_native/$RID/libfibercpu.a"
if [ ! -f "$FIBERCPU_STATIC" ]; then
  echo "error: missing fibercpu static archive: $FIBERCPU_STATIC" >&2
  exit 1
fi
cp "$FIBERCPU_STATIC" "$OUT_DIR/libfibercpu.a"

if [ ! -f "$OUT_DIR/podishcore.a" ]; then
  echo "error: missing podishcore.a in publish output ($OUT_DIR). current RID/TFM did not produce a native static library." >&2
  exit 1
fi

PKG_BASE="$NUGET_ROOT/microsoft.netcore.app.runtime.nativeaot.$RID"
if [ -d "$PKG_BASE" ]; then
  PKG_DIR="$(ls -d "$PKG_BASE"/* 2>/dev/null | sort -V | tail -n 1)"
  NATIVE_DIR="$PKG_DIR/runtimes/$RID/native"
  SDK_NATIVE_DIR=""
else
  PKG_BASE="$NUGET_ROOT/runtime.$RID.microsoft.dotnet.ilcompiler"
  if [ ! -d "$PKG_BASE" ]; then
    echo "error: NativeAOT package not found for RID '$RID' (checked nativeaot + ilcompiler layouts)." >&2
    exit 1
  fi
  PKG_DIR="$(ls -d "$PKG_BASE"/* 2>/dev/null | sort -V | tail -n 1)"
  NATIVE_DIR="$PKG_DIR/framework"
  SDK_NATIVE_DIR="$PKG_DIR/sdk"
fi

if [ ! -d "$NATIVE_DIR" ]; then
  echo "error: native runtime directory missing: $NATIVE_DIR" >&2
  exit 1
fi

BOOTSTRAPPER_OBJ=""
if [ -f "$NATIVE_DIR/libbootstrapperdll.o" ]; then
  BOOTSTRAPPER_OBJ="$NATIVE_DIR/libbootstrapperdll.o"
elif [ -f "$NATIVE_DIR/libbootstrapper.o" ]; then
  BOOTSTRAPPER_OBJ="$NATIVE_DIR/libbootstrapper.o"
fi
if [ -n "$SDK_NATIVE_DIR" ] && [ -d "$SDK_NATIVE_DIR" ] && [ -f "$SDK_NATIVE_DIR/libbootstrapperdll.o" ]; then
  BOOTSTRAPPER_OBJ="$SDK_NATIVE_DIR/libbootstrapperdll.o"
elif [ -n "$SDK_NATIVE_DIR" ] && [ -d "$SDK_NATIVE_DIR" ] && [ -f "$SDK_NATIVE_DIR/libbootstrapper.o" ]; then
  BOOTSTRAPPER_OBJ="$SDK_NATIVE_DIR/libbootstrapper.o"
fi
if [ -z "$BOOTSTRAPPER_OBJ" ]; then
  ILC_BASE="$NUGET_ROOT/runtime.$RID.microsoft.dotnet.ilcompiler"
  if [ -d "$ILC_BASE" ]; then
    ILC_DIR="$(ls -d "$ILC_BASE"/* 2>/dev/null | sort -V | tail -n 1)"
    if [ -f "$ILC_DIR/sdk/libbootstrapperdll.o" ]; then
      BOOTSTRAPPER_OBJ="$ILC_DIR/sdk/libbootstrapperdll.o"
    elif [ -f "$ILC_DIR/sdk/libbootstrapper.o" ]; then
      BOOTSTRAPPER_OBJ="$ILC_DIR/sdk/libbootstrapper.o"
    fi
  fi
fi
if [ -z "$BOOTSTRAPPER_OBJ" ]; then
  echo "error: libbootstrapperdll.o not found for RID '$RID'." >&2
  exit 1
fi

FRAMEWORK_DIR="$OUT_DIR/PodishCore.framework"
mkdir -p "$FRAMEWORK_DIR/Headers" "$FRAMEWORK_DIR/Modules"
cp "$OUT_DIR/include/podish.h" "$FRAMEWORK_DIR/Headers/podish.h"

TMP_ARCHIVES="$(mktemp)"
cleanup() {
  rm -f "$TMP_ARCHIVES"
}
trap cleanup EXIT

add_archive() {
  local path="$1"
  [ -f "$path" ] || return 0
  echo "$path" >> "$TMP_ARCHIVES"
}

# Core archives first.
add_archive "$OUT_DIR/podishcore.a"
add_archive "$OUT_DIR/libfibercpu.a"

# Bundle NativeAOT runtime static libs into the framework archive.
for a in "$NATIVE_DIR"/*.a; do
  [ -f "$a" ] || continue
  add_archive "$a"
done
if [ -n "$SDK_NATIVE_DIR" ] && [ -d "$SDK_NATIVE_DIR" ]; then
  for a in "$SDK_NATIVE_DIR"/*.a; do
    [ -f "$a" ] || continue
    add_archive "$a"
  done
fi

# Deduplicate while preserving order.
DEDUP_ARCHIVES="$(awk '!seen[$0]++' "$TMP_ARCHIVES")"
if [ -z "$DEDUP_ARCHIVES" ]; then
  echo "error: no static archives found to package." >&2
  exit 1
fi

# Build a static framework binary (archive payload), no extra dynamic relink step.
rm -f "$FRAMEWORK_DIR/PodishCore"
# shellcheck disable=SC2086
libtool -static -o "$FRAMEWORK_DIR/PodishCore" $DEDUP_ARCHIVES
cp "$BOOTSTRAPPER_OBJ" "$OUT_DIR/libbootstrapperdll.o"

cat > "$FRAMEWORK_DIR/Modules/module.modulemap" <<'MMAP'
framework module PodishCore {
  umbrella header "podish.h"
  export *
  module * { export * }
}
MMAP

cat > "$FRAMEWORK_DIR/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key>
  <string>dev.podish.PodishCore</string>
  <key>CFBundleName</key>
  <string>PodishCore</string>
  <key>CFBundlePackageType</key>
  <string>FMWK</string>
  <key>CFBundleShortVersionString</key>
  <string>1.0</string>
  <key>CFBundleVersion</key>
  <string>1</string>
</dict>
</plist>
PLIST

echo "Published native static framework to: $FRAMEWORK_DIR"
ls -1 "$OUT_DIR" | sed 's/^/  - /'
