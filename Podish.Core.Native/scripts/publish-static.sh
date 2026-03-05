#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
PROJ="$ROOT_DIR/Podish.Core.Native/Podish.Core.Native.csproj"
TFM="net10.0"
NUGET_ROOT="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
OUT_ROOT="$ROOT_DIR/Podish/artifacts/podish-native"
WORK_ROOT="$OUT_ROOT/_staging"
XCFRAMEWORK_OUT="$OUT_ROOT/PodishCore.xcframework"
RIDS=("osx-arm64" "ios-arm64" "iossimulator-arm64")

echo "Publishing Podish.Core.Native multiplatform XCFramework (TFM=$TFM)"
rm -rf "$WORK_ROOT" "$XCFRAMEWORK_OUT"
mkdir -p "$WORK_ROOT" "$OUT_ROOT"

export MSBUILDDISABLENODEREUSE=1

find_bootstrapper_dll_obj() {
  local rid="$1"
  local pkg_base pkg_dir native_dir sdk_dir ilc_base ilc_dir

  pkg_base="$NUGET_ROOT/microsoft.netcore.app.runtime.nativeaot.$rid"
  if [ -d "$pkg_base" ]; then
    pkg_dir="$(ls -d "$pkg_base"/* 2>/dev/null | sort -V | tail -n 1)"
    native_dir="$pkg_dir/runtimes/$rid/native"
    sdk_dir="$pkg_dir/sdk"
    if [ -f "$native_dir/libbootstrapperdll.o" ]; then
      echo "$native_dir/libbootstrapperdll.o"
      return 0
    fi
    if [ -f "$sdk_dir/libbootstrapperdll.o" ]; then
      echo "$sdk_dir/libbootstrapperdll.o"
      return 0
    fi
  fi

  ilc_base="$NUGET_ROOT/runtime.$rid.microsoft.dotnet.ilcompiler"
  if [ -d "$ilc_base" ]; then
    ilc_dir="$(ls -d "$ilc_base"/* 2>/dev/null | sort -V | tail -n 1)"
    if [ -f "$ilc_dir/sdk/libbootstrapperdll.o" ]; then
      echo "$ilc_dir/sdk/libbootstrapperdll.o"
      return 0
    fi
  fi

  return 1
}

find_native_static_dir() {
  local rid="$1"
  local pkg_base pkg_dir

  pkg_base="$NUGET_ROOT/microsoft.netcore.app.runtime.nativeaot.$rid"
  if [ -d "$pkg_base" ]; then
    pkg_dir="$(ls -d "$pkg_base"/* 2>/dev/null | sort -V | tail -n 1)"
    echo "$pkg_dir/runtimes/$rid/native"
    return 0
  fi

  pkg_base="$NUGET_ROOT/runtime.$rid.microsoft.dotnet.ilcompiler"
  if [ -d "$pkg_base" ]; then
    pkg_dir="$(ls -d "$pkg_base"/* 2>/dev/null | sort -V | tail -n 1)"
    echo "$pkg_dir/framework"
    return 0
  fi

  return 1
}

build_library_for_rid() {
  local rid="$1"
  local rid_out="$WORK_ROOT/$rid"
  local headers_dir="$rid_out/include"
  local static_out="$rid_out/libPodishCore.a"
  local fibercpu_static="$ROOT_DIR/Fiberish.X86/build_native/$rid/libfibercpu.a"
  local native_dir sdk_dir tmp_inputs dedup_inputs bootstrapper_dll_obj

  echo "=== RID: $rid ==="
  rm -rf "$rid_out"
  mkdir -p "$headers_dir"

  dotnet build "$ROOT_DIR/Fiberish.X86/Fiberish.X86.csproj" -c Release --nologo /p:PodishStaticNative=true /p:RuntimeIdentifier="$rid"
  dotnet restore "$PROJ" -r "$rid" --nologo /p:TargetFramework="$TFM" /p:CheckEolTargetFramework=false /p:CheckEolWorkloads=false
  dotnet publish "$PROJ" -f "$TFM" -c Release -r "$rid" -o "$rid_out" --nologo /p:PodishStaticNative=true /p:CheckEolTargetFramework=false /p:CheckEolWorkloads=false

  cp "$ROOT_DIR/Podish.Core.Native/podish.h" "$headers_dir/podish.h"
  bootstrapper_dll_obj="$(find_bootstrapper_dll_obj "$rid")" || {
    echo "error: libbootstrapperdll.o not found for RID '$rid'." >&2
    exit 1
  }
  cp "$bootstrapper_dll_obj" "$rid_out/podish_bootstrapper.o"

  if [ ! -f "$rid_out/podishcore.a" ]; then
    echo "error: missing podishcore.a for RID '$rid'." >&2
    exit 1
  fi
  if [ ! -f "$fibercpu_static" ]; then
    echo "error: missing libfibercpu.a for RID '$rid': $fibercpu_static" >&2
    exit 1
  fi

  native_dir="$(find_native_static_dir "$rid")" || {
    echo "error: NativeAOT static dir not found for RID '$rid'." >&2
    exit 1
  }
  sdk_dir="$native_dir/../sdk"

  tmp_inputs="$(mktemp)"
  {
    echo "$rid_out/podishcore.a"
    echo "$fibercpu_static"
    for a in "$native_dir"/*.a; do
      [ -f "$a" ] && echo "$a"
    done
    for a in "$sdk_dir"/*.a; do
      [ -f "$a" ] && echo "$a"
    done
  } > "$tmp_inputs"

  dedup_inputs="$(awk '!seen[$0]++' "$tmp_inputs")"
  rm -f "$tmp_inputs"

  if [ -z "$dedup_inputs" ]; then
    echo "error: no static inputs found for RID '$rid'." >&2
    exit 1
  fi

  rm -f "$static_out"
  # Merge input static archives directly to avoid object name collisions during manual extraction.
  # shellcheck disable=SC2086
  xcrun libtool -static -o "$static_out" $dedup_inputs
}

for rid in "${RIDS[@]}"; do
  build_library_for_rid "$rid"
done

xcodebuild -create-xcframework \
  -library "$WORK_ROOT/osx-arm64/libPodishCore.a" -headers "$WORK_ROOT/osx-arm64/include" \
  -library "$WORK_ROOT/ios-arm64/libPodishCore.a" -headers "$WORK_ROOT/ios-arm64/include" \
  -library "$WORK_ROOT/iossimulator-arm64/libPodishCore.a" -headers "$WORK_ROOT/iossimulator-arm64/include" \
  -output "$XCFRAMEWORK_OUT"

echo "Published multi-platform XCFramework:"
echo "  $XCFRAMEWORK_OUT"
