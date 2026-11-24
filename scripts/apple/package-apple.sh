#!/usr/bin/env bash
#
# Build and package complete macOS (.app + .zip) and iOS (.ipa + simulator .app)
# distributions of Podish Desktop with embedded ANGLE Metal frameworks, guest GLES
# libraries, and indexed Debian i386 LXQt tarfs rootfs.
#
# Usage:
#   bash scripts/apple/package-apple.sh          # package using prepared artifacts
#   bash scripts/apple/package-apple.sh --full   # also build ANGLE, guest GLES, rootfs, and PodishCore
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
XCODEPROJ="${REPO_ROOT}/PodishApp/PodishApp.xcodeproj"
SCHEME="Podish"
CONFIG="${PODISH_XCODE_CONFIG:-Release}"
DIST_DIR="${PODISH_DIST_DIR:-${REPO_ROOT}/dist/apple}"
DERIVED_DATA="${DIST_DIR}/_derived"

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

if [[ "${FULL_BUILD}" -eq 1 ]]; then
    echo "=== [1/4] Preparing ANGLE XCFrameworks ==="
    bash "${SCRIPT_DIR}/prepare-angle.sh"

    echo "=== [2/4] Building guest IA-32 GLES/GBM libraries ==="
    bash "${REPO_ROOT}/graphics/build-guest.sh" "${REPO_ROOT}/PodishApp/Resources/PodishGlesGuest"

    echo "=== [3/4] Building Debian i386 LXQt + Qt6 Launcher tarfs rootfs ==="
    bash "${SCRIPT_DIR}/build-debian-rootfs.sh"

    echo "=== [4/4] Publishing NativeAOT PodishCore.xcframework ==="
    bash "${REPO_ROOT}/Podish.Core.Native/scripts/publish-static.sh"
fi

# Verify prerequisites exist
for req in \
    "${REPO_ROOT}/PodishApp/artifacts/angle/libEGL.xcframework" \
    "${REPO_ROOT}/PodishApp/artifacts/angle/libGLESv2.xcframework" \
    "${REPO_ROOT}/PodishApp/artifacts/podish-native/PodishCore.xcframework" \
    "${REPO_ROOT}/PodishApp/Resources/PodishRootfs/image.json" \
    "${REPO_ROOT}/PodishApp/Resources/PodishRootfs/blobs/rootfs.tar" \
    "${REPO_ROOT}/PodishApp/Resources/PodishRootfs/indexes/rootfs.index.json"; do
    if [[ ! -e "${req}" ]]; then
        echo "error: missing required build artifact: ${req}" >&2
        echo "Run with --full or execute the prerequisite scripts first." >&2
        exit 1
    fi
done

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
    SYMROOT="${DERIVED_DATA}/macos/Products" \
    OBJROOT="${DERIVED_DATA}/macos/Intermediates" \
    CODE_SIGNING_ALLOWED=NO \
    CODE_SIGNING_REQUIRED=NO \
    CODE_SIGN_IDENTITY="" \
    AD_HOC_CODE_SIGNING_ALLOWED=YES \
    build

MAC_APP_SRC="${DERIVED_DATA}/macos/Products/${CONFIG}/Podish.app"
MAC_APP_DST="${DIST_DIR}/macos/Podish.app"
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
    SYMROOT="${DERIVED_DATA}/ios/Products" \
    OBJROOT="${DERIVED_DATA}/ios/Intermediates" \
    CODE_SIGNING_ALLOWED=NO \
    CODE_SIGNING_REQUIRED=NO \
    CODE_SIGN_IDENTITY="" \
    build

IOS_APP_SRC="${DERIVED_DATA}/ios/Products/${CONFIG}-iphoneos/Podish.app"
IOS_PAYLOAD_DIR="${DIST_DIR}/ios/Payload"
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
        SYMROOT="${DERIVED_DATA}/iossim/Products" \
        OBJROOT="${DERIVED_DATA}/iossim/Intermediates" \
        CODE_SIGNING_ALLOWED=NO \
        CODE_SIGNING_REQUIRED=NO \
        CODE_SIGN_IDENTITY="" \
        build

    mkdir -p "${DIST_DIR}/ios-simulator"
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
