#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
OUT_DIR="${PODISH_ANGLE_XCFRAMEWORK_DIR:-${REPO_ROOT}/PodishApp/artifacts/angle}"
CACHE_DIR="${OUT_DIR}/_cache"
STAGE_DIR="${OUT_DIR}/_stage"
TAG="${PODISH_ANGLE_RELEASE_TAG:-chromium-7151_rev1}"
BASE_URL="https://github.com/kivy/angle-builder/releases/download/${TAG}"

mkdir -p "${CACHE_DIR}" "${OUT_DIR}"
rm -rf "${STAGE_DIR}" "${OUT_DIR}/libEGL.xcframework" "${OUT_DIR}/libGLESv2.xcframework"
mkdir -p "${STAGE_DIR}"/{macos,ios,iossim,mac-fw}

fetch_tarball() {
    local name="$1"
    local dest="${CACHE_DIR}/${name}"
    if [[ -f "${dest}" ]]; then
        echo "Using cached ${name}"
        return 0
    fi
    if [[ -f "/tmp/angle-probe/${name}" ]]; then
        cp "/tmp/angle-probe/${name}" "${dest}"
        echo "Copied ${name} from /tmp/angle-probe"
        return 0
    fi
    echo "Downloading ${BASE_URL}/${name}..."
    curl -fL --retry 3 "${BASE_URL}/${name}" -o "${dest}"
}

fetch_tarball "angle-macos-arm64.tar.gz"
fetch_tarball "angle-iphoneos-arm64.tar.gz"
fetch_tarball "angle-iphonesimulator-arm64.tar.gz"

tar -xzf "${CACHE_DIR}/angle-macos-arm64.tar.gz" -C "${STAGE_DIR}/macos"
tar -xzf "${CACHE_DIR}/angle-iphoneos-arm64.tar.gz" -C "${STAGE_DIR}/ios"
tar -xzf "${CACHE_DIR}/angle-iphonesimulator-arm64.tar.gz" -C "${STAGE_DIR}/iossim"

wrap_macos_framework() {
    local name="$1"
    local bundle_id="$2"
    local dylib="${STAGE_DIR}/macos/${name}.dylib"
    local fw="${STAGE_DIR}/mac-fw/${name}.framework"

    mkdir -p "${fw}/Versions/A/Resources"
    cp "${dylib}" "${fw}/Versions/A/${name}"
    install_name_tool -id "@rpath/${name}.framework/Versions/A/${name}" "${fw}/Versions/A/${name}"

    cat > "${fw}/Versions/A/Resources/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleExecutable</key>
    <string>${name}</string>
    <key>CFBundleIdentifier</key>
    <string>${bundle_id}</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>${name}</string>
    <key>CFBundlePackageType</key>
    <string>FMWK</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>CFBundleVersion</key>
    <string>1</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
</dict>
</plist>
EOF

    ln -s A "${fw}/Versions/Current"
    ln -s "Versions/Current/${name}" "${fw}/${name}"
    ln -s "Versions/Current/Resources" "${fw}/Resources"
}

wrap_macos_framework "libEGL" "org.chromium.angle.libEGL"
wrap_macos_framework "libGLESv2" "org.chromium.angle.libGLESv2"

# macOS ANGLE libEGL dynamically dlopens libGLESv2.dylib relative to its own binary directory.
ln -s "../../../libGLESv2.framework/Versions/A/libGLESv2" \
    "${STAGE_DIR}/mac-fw/libEGL.framework/Versions/A/libGLESv2.dylib"

# Ensure macOS libEGL references libGLESv2 via @rpath if linked, though ANGLE dlopens/Metal-links directly.
for fw in libEGL libGLESv2; do
    xcodebuild -create-xcframework \
        -framework "${STAGE_DIR}/mac-fw/${fw}.framework" \
        -framework "${STAGE_DIR}/ios/${fw}.framework" \
        -framework "${STAGE_DIR}/iossim/${fw}.framework" \
        -output "${OUT_DIR}/${fw}.xcframework"
done

cp "${STAGE_DIR}/macos/LICENSE" "${OUT_DIR}/LICENSE.ANGLE"
rm -rf "${STAGE_DIR}"

echo "Prepared ANGLE XCFrameworks in ${OUT_DIR}:"
ls -ld "${OUT_DIR}/libEGL.xcframework" "${OUT_DIR}/libGLESv2.xcframework"
