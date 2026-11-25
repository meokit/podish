#!/usr/bin/env bash
#
# Build macOS and iOS packages with one Apple desktop graphics backend.
#
# Usage:
#   bash scripts/apple/package-apple.sh --full
#   PODISH_GRAPHICS_BACKEND=gles bash scripts/apple/package-apple.sh --full
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
XCODEPROJ="${REPO_ROOT}/PodishApp/PodishApp.xcodeproj"
SCHEME="Podish"
CONFIG="${PODISH_XCODE_CONFIG:-Release}"
BACKEND="${PODISH_GRAPHICS_BACKEND:-vulkan}"
case "${BACKEND}" in
    vulkan|gles) ;;
    *) echo "error: PODISH_GRAPHICS_BACKEND must be vulkan or gles" >&2; exit 2 ;;
esac
export PODISH_GRAPHICS_BACKEND="${BACKEND}"
HOST_ZINK="${PODISH_HOST_ZINK:-0}"
VARIANT="${BACKEND}"
case "${BACKEND}:${HOST_ZINK}" in
    vulkan:1) VARIANT=vulkan-host-zink ;;
    vulkan:0|gles:0) ;;
    *) echo 'error: PODISH_HOST_ZINK must be 0, or 1 with the Vulkan backend.' >&2; exit 2 ;;
esac
DIST_DIR="${PODISH_DIST_DIR:-${REPO_ROOT}/dist/apple}/${VARIANT}"
DERIVED_DATA="${DIST_DIR}/_derived"
VENUS_DIR="${PODISH_VENUS_DIR:-${REPO_ROOT}/PodishApp/artifacts/venus/${VARIANT}}"
HOST_ZINK_DIR="${PODISH_HOST_ZINK_DIR:-${REPO_ROOT}/PodishApp/artifacts/host-zink}"
ANGLE_DIR="${PODISH_ANGLE_XCFRAMEWORK_DIR:-${REPO_ROOT}/PodishApp/artifacts/angle}"
NATIVE_DIR="${REPO_ROOT}/PodishApp/artifacts/podish-native/${BACKEND}"
RESOURCES_DIR="${REPO_ROOT}/PodishApp/Resources/${VARIANT}"
ROOTFS_DIR="${PODISH_ROOTFS_STORE_DIR:-${RESOURCES_DIR}/PodishRootfs}"

FULL_BUILD=0
SKIP_SIM=0
for arg in "$@"; do
    case "$arg" in
        --full) FULL_BUILD=1 ;;
        --skip-simulator) SKIP_SIM=1 ;;
        *)
            echo "Unknown argument: $arg" >&2
            exit 2
            ;;
    esac
done

if [[ "$BACKEND" == vulkan ]]; then
    # Vulkan builds require a Metal 4 GPU.
    SKIP_SIM=1
fi

APPLE_PLATFORMS=(macosx iphoneos)
if [[ "${SKIP_SIM}" -eq 0 ]]; then
    APPLE_PLATFORMS+=(iphonesimulator)
fi

if [[ "${FULL_BUILD}" -eq 1 ]]; then
    if [[ "${BACKEND}" == vulkan ]]; then
        echo "=== Building Apple Venus Vulkan renderer ==="
        PODISH_VENUS_DIR="${VENUS_DIR}" bash "${SCRIPT_DIR}/prepare-venus.sh" "${APPLE_PLATFORMS[@]}"
        if [[ "${HOST_ZINK}" == 1 ]]; then
            PODISH_VENUS_DIR="${VENUS_DIR}" PODISH_HOST_ZINK_DIR="${HOST_ZINK_DIR}" \
                bash "${SCRIPT_DIR}/prepare-host-zink.sh" "${APPLE_PLATFORMS[@]}"
        fi
    else
        echo "=== Preparing ANGLE XCFrameworks and guest IA-32 GLES libraries ==="
        PODISH_ANGLE_XCFRAMEWORK_DIR="${ANGLE_DIR}" bash "${SCRIPT_DIR}/prepare-angle.sh"
        bash "${REPO_ROOT}/graphics/build-guest.sh" "${RESOURCES_DIR}/PodishGlesGuest"
    fi

    echo "=== Building Debian i386 LXQt + Qt6 Launcher tarfs rootfs (${BACKEND}) ==="
    PODISH_ROOTFS_STORE_DIR="${ROOTFS_DIR}" bash "${SCRIPT_DIR}/build-debian-rootfs.sh"

    echo "=== Publishing NativeAOT PodishCore.xcframework (${BACKEND}) ==="
    bash "${REPO_ROOT}/Podish.Core.Native/scripts/publish-static.sh"
fi

# Verify prerequisites exist
for req in \
    "${NATIVE_DIR}/PodishCore.xcframework" \
    "${ROOTFS_DIR}/image.json" \
    "${ROOTFS_DIR}/blobs/rootfs.tar" \
    "${ROOTFS_DIR}/indexes/rootfs.index.json"; do
    if [[ ! -e "${req}" ]]; then
        echo "error: missing required build artifact: ${req}" >&2
        echo "Run with --full or execute the prerequisite scripts first." >&2
        exit 1
    fi
done
for marker in "${NATIVE_DIR}/graphics-backend" "${ROOTFS_DIR}/graphics-backend"; do
    if [[ ! -f "${marker}" || $(cat "${marker}") != "${BACKEND}" ]]; then
        echo "error: expected ${BACKEND} artifact at ${marker}. Run with --full." >&2
        exit 1
    fi
done
BUILD_SETTINGS=("PODISH_GRAPHICS_BACKEND=${BACKEND}" "PODISH_ROOTFS_STORE_DIR=$(cd "${ROOTFS_DIR}" && pwd)")
if [[ "${BACKEND}" == vulkan ]]; then
    for platform in "${APPLE_PLATFORMS[@]}"; do
        if [[ ! -f "${VENUS_DIR}/${platform}/PodishVenus.framework/PodishVenus" ||
              ! -f "${VENUS_DIR}/${platform}/PodishKosmicKrisp.framework/PodishKosmicKrisp" ]]; then
            echo "error: missing Venus or KosmicKrisp framework for ${platform}. Run with --full." >&2
            exit 1
        fi
    done
    BUILD_SETTINGS+=("PODISH_VENUS_DIR=$(cd "${VENUS_DIR}" && pwd)")
    if [[ "${HOST_ZINK}" == 1 ]]; then
        for platform in "${APPLE_PLATFORMS[@]}"; do
            if [[ ! -f "${HOST_ZINK_DIR}/${platform}/PodishHostZink.framework/PodishHostZink" ]]; then
                echo "error: missing Host Zink framework for ${platform}. Run with --full." >&2
                exit 1
            fi
        done
        BUILD_SETTINGS+=("PODISH_HOST_ZINK_DIR=$(cd "${HOST_ZINK_DIR}" && pwd)")
    fi
else
    if [[ ! -d "${ANGLE_DIR}/libEGL.xcframework" || ! -d "${ANGLE_DIR}/libGLESv2.xcframework" ||
          ! -f "${RESOURCES_DIR}/PodishGlesGuest/libEGL.so.1" ]]; then
        echo "error: missing ANGLE XCFrameworks or guest GLES libraries. Run with --full." >&2
        exit 1
    fi
    BUILD_SETTINGS+=("PODISH_ANGLE_XCFRAMEWORK_DIR=$(cd "${ANGLE_DIR}" && pwd)")
fi

verify_app_graphics() {
    local app="$1" bundle_root="$1" executable="$1/Podish" dependencies
    if [[ -d "${app}/Contents" ]]; then
        bundle_root="${app}/Contents"
        executable="${bundle_root}/MacOS/Podish"
    fi
    local frameworks="${bundle_root}/Frameworks" resources="${bundle_root}"
    if [[ "${bundle_root}" != "${app}" ]]; then
        resources="${bundle_root}/Resources"
    fi
    dependencies="$(otool -L "${executable}")"
    if [[ "${BACKEND}" == vulkan ]]; then
        local venus="${frameworks}/PodishVenus.framework/PodishVenus"
        local kosmickrisp="${frameworks}/PodishKosmicKrisp.framework/PodishKosmicKrisp"
        if [[ ! -f "${venus}" || ! -f "${kosmickrisp}" ||
              -e "${frameworks}/libEGL.framework" || -e "${frameworks}/libGLESv2.framework" ||
              -e "${resources}/PodishGlesGuest" || "${dependencies}" == *libEGL.framework* ||
              "${dependencies}" == *libGLESv2.framework* ]]; then
            echo "error: expected a Venus-only app bundle at ${app}" >&2
            return 1
        fi
        if [[ "$(otool -L "${venus}")" != *@rpath/PodishKosmicKrisp.framework/* ]]; then
            echo "error: PodishVenus in ${app} must load the packaged KosmicKrisp driver." >&2
            return 1
        fi
        if [[ "${HOST_ZINK}" == 1 && ! -f "${frameworks}/PodishHostZink.framework/PodishHostZink" ]]; then
            echo "error: expected the Host Zink framework in ${app}" >&2
            return 1
        fi
    elif [[ -e "${frameworks}/PodishVenus.framework" || "${dependencies}" == *PodishVenus.framework* ||
             -e "${frameworks}/PodishKosmicKrisp.framework" || "${dependencies}" == *PodishKosmicKrisp.framework* ||
             ! -f "${frameworks}/libEGL.framework/libEGL" || ! -f "${frameworks}/libGLESv2.framework/libGLESv2" ||
             ! -f "${resources}/PodishGlesGuest/libEGL.so.1" ]]; then
        echo "error: expected an ANGLE-only app bundle at ${app}" >&2
        return 1
    fi
}

rm -rf "${DIST_DIR}/macos" "${DIST_DIR}/ios" "${DIST_DIR}/ios-simulator" \
       "${DIST_DIR}/Podish-macOS-arm64.zip" "${DIST_DIR}/Podish-iOS-arm64.ipa"
mkdir -p "${DIST_DIR}/macos" "${DIST_DIR}/ios" "${DERIVED_DATA}"

echo "=== Building macOS (${CONFIG}, arm64) ==="
xcodebuild \
    -project "${XCODEPROJ}" \
    -target "${SCHEME}" \
    -configuration "${CONFIG}" \
    -sdk macosx \
    -arch arm64 \
    "${BUILD_SETTINGS[@]}" \
    SYMROOT="${DERIVED_DATA}/macos/Products" \
    OBJROOT="${DERIVED_DATA}/macos/Intermediates" \
    CLANG_MODULE_CACHE_PATH="${DERIVED_DATA}/macos/ModuleCache.noindex" \
    CODE_SIGNING_ALLOWED=NO \
    CODE_SIGNING_REQUIRED=NO \
    CODE_SIGN_IDENTITY="" \
    AD_HOC_CODE_SIGNING_ALLOWED=YES \
    build

MAC_APP_SRC="${DERIVED_DATA}/macos/Products/${CONFIG}/Podish.app"
MAC_APP_DST="${DIST_DIR}/macos/Podish.app"
verify_app_graphics "${MAC_APP_SRC}"
cp -R "${MAC_APP_SRC}" "${MAC_APP_DST}"

# Ad-hoc sign frameworks and app bundle so macOS arm64 executes locally without Gatekeeper/AMFI rejection.
for fw in "${MAC_APP_DST}/Contents/Frameworks/"*.framework; do
    [[ -d "${fw}" ]] && codesign --force --sign - "${fw}"
done
codesign --force --sign - "${MAC_APP_DST}"

echo "Creating macOS zip archive..."
ditto -c -k --sequesterRsrc --keepParent "${MAC_APP_DST}" "${DIST_DIR}/Podish-macOS-arm64.zip"

echo "=== Building iOS Device (${CONFIG}, iphoneos arm64) ==="
xcodebuild \
    -project "${XCODEPROJ}" \
    -target "${SCHEME}" \
    -configuration "${CONFIG}" \
    -sdk iphoneos \
    -arch arm64 \
    "${BUILD_SETTINGS[@]}" \
    SYMROOT="${DERIVED_DATA}/ios/Products" \
    OBJROOT="${DERIVED_DATA}/ios/Intermediates" \
    CLANG_MODULE_CACHE_PATH="${DERIVED_DATA}/ios/ModuleCache.noindex" \
    CODE_SIGNING_ALLOWED=NO \
    CODE_SIGNING_REQUIRED=NO \
    CODE_SIGN_IDENTITY="" \
    build

IOS_APP_SRC="${DERIVED_DATA}/ios/Products/${CONFIG}-iphoneos/Podish.app"
IOS_PAYLOAD_DIR="${DIST_DIR}/ios/Payload"
verify_app_graphics "${IOS_APP_SRC}"
mkdir -p "${IOS_PAYLOAD_DIR}"
cp -R "${IOS_APP_SRC}" "${IOS_PAYLOAD_DIR}/Podish.app"

echo "Packaging iOS .ipa..."
(cd "${DIST_DIR}/ios" && zip -qry -1 "${DIST_DIR}/Podish-iOS-arm64.ipa" Payload)

if [[ "${SKIP_SIM}" -eq 0 ]]; then
    echo "=== Building iOS Simulator (${CONFIG}, iphonesimulator arm64) ==="
    xcodebuild \
        -project "${XCODEPROJ}" \
        -target "${SCHEME}" \
        -configuration "${CONFIG}" \
        -sdk iphonesimulator \
        -arch arm64 \
        "${BUILD_SETTINGS[@]}" \
        SYMROOT="${DERIVED_DATA}/iossim/Products" \
        OBJROOT="${DERIVED_DATA}/iossim/Intermediates" \
        CLANG_MODULE_CACHE_PATH="${DERIVED_DATA}/iossim/ModuleCache.noindex" \
        CODE_SIGNING_ALLOWED=NO \
        CODE_SIGNING_REQUIRED=NO \
        CODE_SIGN_IDENTITY="" \
        build

    mkdir -p "${DIST_DIR}/ios-simulator"
    verify_app_graphics "${DERIVED_DATA}/iossim/Products/${CONFIG}-iphonesimulator/Podish.app"
    cp -R "${DERIVED_DATA}/iossim/Products/${CONFIG}-iphonesimulator/Podish.app" \
          "${DIST_DIR}/ios-simulator/Podish.app"
fi

echo
echo "=== Packaging Complete ==="
ls -lh "${DIST_DIR}/Podish-macOS-arm64.zip" "${DIST_DIR}/Podish-iOS-arm64.ipa"
echo "macOS app:         ${DIST_DIR}/macos/Podish.app"
echo "macOS archive:     ${DIST_DIR}/Podish-macOS-arm64.zip"
echo "iOS IPA:           ${DIST_DIR}/Podish-iOS-arm64.ipa"
if [[ "${SKIP_SIM}" -eq 0 ]]; then
    echo "iOS Simulator app: ${DIST_DIR}/ios-simulator/Podish.app"
fi
