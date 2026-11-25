#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
PROJ="$ROOT_DIR/Podish.Core.Native/Podish.Core.Native.csproj"
NETSTACK_MANIFEST="$ROOT_DIR/Fiberish.Netstack/Cargo.toml"
TFM="net10.0"
BACKEND="${PODISH_GRAPHICS_BACKEND:-vulkan}"
case "$BACKEND" in
  vulkan|gles) ;;
  *) echo "error: PODISH_GRAPHICS_BACKEND must be vulkan or gles" >&2; exit 2 ;;
esac
OUT_ROOT="$ROOT_DIR/PodishApp/artifacts/podish-native/$BACKEND"
BUILD_ARGS=(--artifacts-path "$OUT_ROOT/dotnet" -p:PodishGraphicsBackend="$BACKEND")
WORK_ROOT="$OUT_ROOT/_staging"
XCFRAMEWORK_OUT="$OUT_ROOT/PodishCore.xcframework"
RIDS=("osx-arm64" "ios-arm64")
if [[ "$BACKEND" == gles ]]; then
  RIDS+=("iossimulator-arm64")
fi

echo "Publishing Podish.Core.Native multiplatform XCFramework ($BACKEND, TFM=$TFM)"
rm -rf "$WORK_ROOT" "$XCFRAMEWORK_OUT"
rm -f "$OUT_ROOT/graphics-backend"
mkdir -p "$WORK_ROOT" "$OUT_ROOT"

export MSBUILDDISABLENODEREUSE=1

require_dotnet_10() {
  local sdk_ver sdk_major
  sdk_ver="$(dotnet --version)"
  sdk_major="${sdk_ver%%.*}"
  if [ -z "$sdk_major" ] || [ "$sdk_major" -lt 10 ]; then
    echo "error: .NET SDK 10+ is required. current=$sdk_ver" >&2
    exit 1
  fi
}

find_native_static_dir() {
  local rid="$1" assets_file="$2"
  # The linker inputs must come from the same pack as the restored NativeAOT compiler project.
  /usr/bin/python3 - "$rid" "$assets_file" "$TFM" <<'PYTHON'
import json
from pathlib import Path
import sys

rid, assets_file, tfm = sys.argv[1:]
with open(assets_file, encoding="utf-8") as handle:
    project = json.load(handle)["project"]
package_id = f"microsoft.netcore.app.runtime.nativeaot.{rid}"
for dependency in project["frameworks"][tfm].get("downloadDependencies", []):
    if dependency["name"].lower() == package_id:
        lower, upper = (version.strip() for version in dependency["version"].strip("[]").split(",", 1))
        if lower != upper:
            raise SystemExit(f"Expected exact runtime pack version for {rid}")
        native = Path(project["restore"]["packagesPath"], package_id, lower, "runtimes", rid, "native")
        if native.is_dir():
            print(native)
            break
else:
    raise SystemExit(f"NativeAOT runtime pack not found for {rid}")
PYTHON
}

cargo_target_for_rid() {
  local rid="$1"
  case "$rid" in
    osx-arm64) echo "aarch64-apple-darwin" ;;
    ios-arm64) echo "aarch64-apple-ios" ;;
    iossimulator-arm64) echo "aarch64-apple-ios-sim" ;;
    *)
      echo "error: unsupported RID for netstack static build: $rid" >&2
      return 1
      ;;
  esac
}

build_library_for_rid() {
  local rid="$1"
  local rid_out="$WORK_ROOT/$rid"
  local headers_dir="$rid_out/include"
  local static_out="$rid_out/libPodishCore.a"
  local fibercpu_static="$ROOT_DIR/Fiberish.X86/build_native/$rid/libfibercpu.a"
  local native_dir sdk_dir assets_file
  local -a merge_inputs
  local archive
  local netstack_target netstack_static

  echo "=== RID: $rid ==="
  rm -rf "$rid_out"
  mkdir -p "$headers_dir"

  # Restore for the concrete RID first so build/publish always resolves RID-specific assets.
  dotnet restore "$ROOT_DIR/Fiberish.X86/Fiberish.X86.csproj" -r "$rid" --nologo "${BUILD_ARGS[@]}"
  dotnet build "$ROOT_DIR/Fiberish.X86/Fiberish.X86.csproj" -c Release -r "$rid" --no-restore --nologo \
    -p:PodishStaticNative=true "${BUILD_ARGS[@]}"
  netstack_target="$(cargo_target_for_rid "$rid")"
  cargo build --manifest-path "$NETSTACK_MANIFEST" --release --target "$netstack_target"
  netstack_static="$ROOT_DIR/Fiberish.Netstack/target/$netstack_target/release/libfiberish_netstack.a"
  dotnet restore "$PROJ" -r "$rid" --nologo -p:TargetFramework="$TFM" \
    -p:_IsPublishing=true -p:PublishAotUsingRuntimePack=true \
    -p:PodishStaticNative=true -p:CheckEolTargetFramework=false -p:CheckEolWorkloads=false "${BUILD_ARGS[@]}"
  dotnet publish "$PROJ" -f "$TFM" -c Release -r "$rid" -o "$rid_out" --no-restore --nologo \
    -p:PodishStaticNative=true -p:CheckEolTargetFramework=false -p:CheckEolWorkloads=false "${BUILD_ARGS[@]}"
  assets_file="$(dotnet msbuild "$PROJ" -nologo -getProperty:ProjectAssetsFile -p:ArtifactsPath="$OUT_ROOT/dotnet")"
  native_dir="$(find_native_static_dir "$rid" "$assets_file")"

  cp "$ROOT_DIR/Podish.Core.Native/podish.h" "$headers_dir/podish.h"
  cp "$native_dir/libbootstrapperdll.o" "$rid_out/podish_bootstrapper.o"

  if [ ! -f "$rid_out/podishcore.a" ]; then
    echo "error: missing podishcore.a for RID '$rid'." >&2
    exit 1
  fi
  if [ ! -f "$fibercpu_static" ]; then
    echo "error: missing libfibercpu.a for RID '$rid': $fibercpu_static" >&2
    exit 1
  fi
  if [ ! -f "$netstack_static" ]; then
    echo "error: missing libfiberish_netstack.a for RID '$rid': $netstack_static" >&2
    exit 1
  fi

  sdk_dir="$native_dir/../sdk"
  merge_inputs=("$rid_out/podishcore.a" "$fibercpu_static" "$netstack_static")
  for archive in "$native_dir"/*.a "$sdk_dir"/*.a; do
    if [[ -f "$archive" ]]; then
      merge_inputs+=("$archive")
    fi
  done
  xcrun libtool -static -o "$static_out" "${merge_inputs[@]}"
}

require_dotnet_10
XCFRAMEWORK_INPUTS=()
for rid in "${RIDS[@]}"; do
  build_library_for_rid "$rid"
  XCFRAMEWORK_INPUTS+=(-library "$WORK_ROOT/$rid/libPodishCore.a" -headers "$WORK_ROOT/$rid/include")
done

xcodebuild -create-xcframework "${XCFRAMEWORK_INPUTS[@]}" -output "$XCFRAMEWORK_OUT"

echo "Published multi-platform XCFramework:"
echo "  $XCFRAMEWORK_OUT"
echo "$TFM" > "$OUT_ROOT/.tfm"
printf '%s\n' "$BACKEND" > "$OUT_ROOT/graphics-backend"
echo "TFM marker written to $OUT_ROOT/.tfm"
