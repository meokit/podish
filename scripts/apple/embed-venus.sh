#!/usr/bin/env bash
set -euo pipefail

VENUS_DIR="${PODISH_VENUS_DIR:-${SRCROOT}/artifacts/venus/vulkan}"

embed_framework() {
    local name="$1" directory="$2"
    local source_framework="${directory}/${PLATFORM_NAME}/${name}.framework"
    local destination="${TARGET_BUILD_DIR}/${FRAMEWORKS_FOLDER_PATH}/${name}.framework"
    if [[ ! -f "${source_framework}/${name}" ]]; then
        echo "error: missing ${name} for ${PLATFORM_NAME} at ${source_framework}" >&2
        exit 1
    fi
    rm -rf "${destination}"
    ditto "${source_framework}" "${destination}"
    if [[ "${CODE_SIGNING_ALLOWED:-YES}" == YES && -n "${EXPANDED_CODE_SIGN_IDENTITY:-}" ]]; then
        codesign --force --sign "${EXPANDED_CODE_SIGN_IDENTITY}" "${destination}"
    fi
}

embed_framework PodishKosmicKrisp "${VENUS_DIR}"
embed_framework PodishVenus "${VENUS_DIR}"
if [[ -n "${PODISH_HOST_ZINK_DIR:-}" ]]; then
    embed_framework PodishHostZink "${PODISH_HOST_ZINK_DIR}"
fi
