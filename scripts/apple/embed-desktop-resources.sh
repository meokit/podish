#!/usr/bin/env bash
set -euo pipefail

backend="${PODISH_GRAPHICS_BACKEND:-vulkan}"
case "${backend}" in
    vulkan|gles) ;;
    *) echo "error: PODISH_GRAPHICS_BACKEND must be vulkan or gles" >&2; exit 2 ;;
esac

resource_dir="${TARGET_BUILD_DIR}/${UNLOCALIZED_RESOURCES_FOLDER_PATH}"
framework_dir="${TARGET_BUILD_DIR}/${FRAMEWORKS_FOLDER_PATH}"
rootfs="${PODISH_ROOTFS_STORE_DIR:-${SRCROOT}/Resources/${backend}/PodishRootfs}"
native_marker="${SRCROOT}/artifacts/podish-native/${backend}/graphics-backend"

for marker in "${rootfs}/graphics-backend" "${native_marker}"; do
    if [[ ! -f "${marker}" || $(cat "${marker}") != "${backend}" ]]; then
        echo "error: expected ${backend} artifact at ${marker}. Run scripts/apple/package-apple.sh --full." >&2
        exit 1
    fi
done

mkdir -p "${resource_dir}" "${framework_dir}"
rm -rf "${resource_dir}/PodishRootfs" "${resource_dir}/PodishGlesGuest" \
       "${framework_dir}/PodishVenus.framework" "${framework_dir}/PodishHostZink.framework" \
       "${framework_dir}/PodishKosmicKrisp.framework" "${framework_dir}/libEGL.framework" \
       "${framework_dir}/libGLESv2.framework"
mkdir -p "${resource_dir}/PodishRootfs/blobs" "${resource_dir}/PodishRootfs/indexes"
cp "${rootfs}/image.json" "${rootfs}/graphics-backend" "${resource_dir}/PodishRootfs/"
cp "${rootfs}/blobs/rootfs.tar" "${resource_dir}/PodishRootfs/blobs/"
cp "${rootfs}/indexes/rootfs.index.json" "${resource_dir}/PodishRootfs/indexes/"

if [[ "${PLATFORM_NAME}" != macosx ]]; then
    icon_dir="${SRCROOT}/PodishApp/Assets.xcassets/AppIcon.appiconset"
    cp "${icon_dir}/icon-iphone-60@2x.png" "${resource_dir}/AppIcon60x60@2x.png"
    cp "${icon_dir}/icon-iphone-60@3x.png" "${resource_dir}/AppIcon60x60@3x.png"
    cp "${icon_dir}/icon-ipad-76@2x.png" "${resource_dir}/AppIcon76x76@2x~ipad.png"
    cp "${icon_dir}/icon-ipad-83.5@2x.png" "${resource_dir}/AppIcon83.5x83.5@2x~ipad.png"
fi

if [[ "${backend}" == vulkan ]]; then
    bash "${SRCROOT}/../scripts/apple/embed-venus.sh"
else
    guest_gles="${SRCROOT}/Resources/gles/PodishGlesGuest"
    if [[ ! -f "${guest_gles}/libEGL.so.1" || ! -f "${guest_gles}/libGLESv2.so.2" ]]; then
        echo "error: missing guest GLES libraries at ${guest_gles}" >&2
        exit 1
    fi
    ditto "${guest_gles}" "${resource_dir}/PodishGlesGuest"

    case "${PLATFORM_NAME}" in
        macosx) slice=macos-arm64 ;;
        iphoneos) slice=ios-arm64 ;;
        iphonesimulator) slice=ios-arm64-simulator ;;
        *) echo "error: unsupported Apple platform ${PLATFORM_NAME}" >&2; exit 2 ;;
    esac
    angle_dir="${PODISH_ANGLE_XCFRAMEWORK_DIR:-${SRCROOT}/artifacts/angle}"
    for library in libEGL libGLESv2; do
        source_framework="${angle_dir}/${library}.xcframework/${slice}/${library}.framework"
        destination="${framework_dir}/${library}.framework"
        if [[ ! -f "${source_framework}/${library}" ]]; then
            echo "error: missing ANGLE framework at ${source_framework}" >&2
            exit 1
        fi
        ditto "${source_framework}" "${destination}"
        if [[ "${CODE_SIGNING_ALLOWED:-YES}" == YES && -n "${EXPANDED_CODE_SIGN_IDENTITY:-}" ]]; then
            codesign --force --sign "${EXPANDED_CODE_SIGN_IDENTITY}" "${destination}"
        fi
    done
fi
