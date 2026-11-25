#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
OUT_DIR="${PODISH_VENUS_DIR:-${REPO_ROOT}/PodishApp/artifacts/venus/vulkan}"
mkdir -p "${OUT_DIR}"
OUT_DIR="$(cd "${OUT_DIR}" && pwd)"
CACHE_DIR="${PODISH_VENUS_DEPS_DIR:-${OUT_DIR}/_cache}"
PROJECT_DIR="${OUT_DIR}/_project"
HOST_ZINK="${PODISH_HOST_ZINK:-0}"
case "${HOST_ZINK}" in
    0|1) ;;
    *) echo 'error: PODISH_HOST_ZINK must be 0 or 1.' >&2; exit 2 ;;
esac

VIRGL_REV=a6a179c72ace8e8e1cdbb267a007997be0318ca6
DILIGENT_REV=c5cfdc4a4f08f2726c903b9c35782416c1eaccb7
ABSEIL_REV=af5aede14a0b4013be6890148d34e0fd7b1fac69
EPOXY_REV=c84bc9459357a40e46e2fec0408d04fbdde2c973
VULKAN_HEADERS_REV=6802bb4733b63ed5efd3adb308a6c885ef180ea1

VENUS_PYTHON=/usr/bin/python3
if [[ "${HOST_ZINK}" == 1 ]]; then
    VENUS_PYTHON="${PODISH_VENUS_PYTHON:-$(command -v python3)}"
fi

if [[ "$#" -eq 0 ]]; then
    set -- macosx iphoneos
fi
for platform in "$@"; do
    case "${platform}" in
        macosx|iphoneos) ;;
        iphonesimulator) echo "error: The Podish Vulkan build requires Metal 4 on a physical device." >&2; exit 2 ;;
        *) echo "Unknown Apple SDK: ${platform}" >&2; exit 2 ;;
    esac
done
for tool in cmake glslc meson ninja xcrun; do
    if ! command -v "${tool}" >/dev/null; then
        echo "error: ${tool} is required to build PodishVenus." >&2
        exit 1
    fi
done

mkdir -p "${CACHE_DIR}" "${PROJECT_DIR}/subprojects"
CACHE_DIR="$(cd "${CACHE_DIR}" && pwd)"
checkout_dependency() {
    local name="$1" revision="$2" url="$3" dir="${CACHE_DIR}/$1"
    if [[ ! -e "${dir}/.git" ]]; then
        git init --quiet "${dir}"
        git -C "${dir}" remote add origin "${url}"
    fi
    if ! git -C "${dir}" cat-file -e "${revision}^{commit}" 2>/dev/null; then
        git -C "${dir}" fetch --depth 1 origin "${revision}"
    fi
    if [[ "$(git -C "${dir}" rev-parse --verify HEAD 2>/dev/null || true)" != "${revision}" ]]; then
        git -C "${dir}" checkout --quiet --detach "${revision}"
    fi
}

if [[ "${PODISH_KOSMICKRISP_TEST_DRIVER:-0}" != 1 ]]; then
    PODISH_VENUS_DIR="${OUT_DIR}" PODISH_VENUS_DEPS_DIR="${CACHE_DIR}" \
        bash "${SCRIPT_DIR}/prepare-kosmickrisp.sh" "$@"
else
    for platform in "$@"; do
        if [[ ! -f "${OUT_DIR}/${platform}/PodishKosmicKrisp.framework/PodishKosmicKrisp" ]]; then
            echo "error: Stage the test driver for ${platform} under ${OUT_DIR} first." >&2
            exit 2
        fi
    done
fi
checkout_dependency Vulkan-Headers "${VULKAN_HEADERS_REV}" https://github.com/KhronosGroup/Vulkan-Headers.git
vulkan_headers="${CACHE_DIR}/Vulkan-Headers/include"

checkout_dependency virglrenderer "${VIRGL_REV}" https://github.com/emkey1/virglrenderer.git
checkout_dependency DiligentCore "${DILIGENT_REV}" https://github.com/DiligentGraphics/DiligentCore.git
checkout_dependency abseil-cpp "${ABSEIL_REV}" https://chromium.googlesource.com/chromium/src/third_party/abseil-cpp/

stage_patched_dependency() {
    local name="$1" revision="$2" stage="$3" patch="$4"
    local patch_id="${revision} $(git hash-object "${patch}")"
    local stamp="${stage}/.git/podish-venus-patch-id"
    if [[ -L "${stage}" ]]; then
        rm "${stage}"
    fi
    if [[ ! -d "${stage}/.git" ]]; then
        git clone --quiet --no-checkout "${CACHE_DIR}/${name}" "${stage}"
    fi
    if [[ "$(cat "${stamp}" 2>/dev/null || true)" != "${patch_id}" ]]; then
        git -C "${stage}" fetch --quiet "${CACHE_DIR}/${name}" "${revision}"
        git -C "${stage}" checkout --quiet --force --detach "${revision}"
        # These disposable checkouts can contain files added by an earlier patch.
        git -C "${stage}" clean --quiet -fd
        git -C "${stage}" apply --index --unidiff-zero "${patch}"
        printf '%s\n' "${patch_id}" > "${stamp}"
    fi
}

# Keep patched sources out of the reusable checkouts.
stage_patched_dependency virglrenderer "${VIRGL_REV}" "${PROJECT_DIR}/subprojects/virglrenderer" \
    "${SCRIPT_DIR}/venus/virglrenderer-kosmickrisp.patch"
if [[ "${HOST_ZINK}" == 1 ]]; then
    checkout_dependency libepoxy "${EPOXY_REV}" https://github.com/anholt/libepoxy.git
    stage_patched_dependency libepoxy "${EPOXY_REV}" "${PROJECT_DIR}/subprojects/libepoxy" \
        "${SCRIPT_DIR}/venus/epoxy-host-zink.patch"
fi
diligent_source="${PROJECT_DIR}/DiligentCore"
diligent_dependencies=(ThirdParty/SPIRV-Cross ThirdParty/SPIRV-Headers ThirdParty/Vulkan-Headers
    ThirdParty/volk ThirdParty/xxHash)
for relative in "${diligent_dependencies[@]}"; do
    if [[ -L "${diligent_source}/${relative}" ]]; then
        rm "${diligent_source}/${relative}"
    fi
done
stage_patched_dependency DiligentCore "${DILIGENT_REV}" "${diligent_source}" \
    "${SCRIPT_DIR}/venus/diligent-apple.patch"
git -C "${CACHE_DIR}/DiligentCore" submodule update --init --depth 1 "${diligent_dependencies[@]}"
for relative in "${diligent_dependencies[@]}"; do
    git -C "${diligent_source}" config "submodule.${relative}.url" "${CACHE_DIR}/DiligentCore/${relative}"
done
git -c protocol.file.allow=always -C "${diligent_source}" submodule update --init "${diligent_dependencies[@]}"

cp "${SCRIPT_DIR}/venus/"{bridge.mm,renderer.c,compositor.h,compositor.mm,vulkan-dispatch.h,exports.txt,meson.build,meson_options.txt} "${PROJECT_DIR}/"
if [[ "${HOST_ZINK}" == 1 ]]; then
    cp "${SCRIPT_DIR}/venus/"{virgl.mm,virgl-exports.txt} "${PROJECT_DIR}/"
fi
glslc -O -g -mfmt=num "${SCRIPT_DIR}/venus/compositor.vert" -o "${PROJECT_DIR}/compositor-vertex.spv.inc"
glslc -O -g -mfmt=num "${SCRIPT_DIR}/venus/compositor.frag" -o "${PROJECT_DIR}/compositor-image.spv.inc"
glslc -O -g -mfmt=num -DSHARED_BUFFER "${SCRIPT_DIR}/venus/compositor.frag" \
    -o "${PROJECT_DIR}/compositor-buffer.spv.inc"

build_diligent() {
    local platform="$1" build_dir="$2" minimum="$3"
    local archive="${build_dir}/libPodishDiligent.a"
    local -a apple_options=()
    if [[ "${platform}" != macosx ]]; then
        apple_options+=(-DCMAKE_SYSTEM_NAME=iOS)
    fi
    cmake -S "${diligent_source}" -B "${build_dir}" -G Ninja \
        -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES=arm64 \
        "-DCMAKE_OSX_SYSROOT=${platform}" "-DCMAKE_OSX_DEPLOYMENT_TARGET=${minimum}" \
        "-DFETCHCONTENT_SOURCE_DIR_ABSEIL-CPP=${CACHE_DIR}/abseil-cpp" \
        -DDILIGENT_NO_OPENGL=ON -DDILIGENT_NO_METAL=ON -DDILIGENT_NO_WEBGPU=ON \
        -DDILIGENT_NO_ARCHIVER=ON -DDILIGENT_NO_SUPER_RESOLUTION=ON \
        -DDILIGENT_NO_GLSLANG=ON -DDILIGENT_NO_HLSL=ON \
        -DDILIGENT_BUILD_TESTS=OFF -DDILIGENT_INSTALL_CORE=OFF "${apple_options[@]}"
    cmake --build "${build_dir}" --target Diligent-GraphicsEngineVk-static

    local -a archives=()
    local library
    while IFS= read -r library; do
        archives+=("${library}")
    done < <(find "${build_dir}" -type f \( -name 'libDiligent-*.a' -o -name 'libspirv-cross-core.a' \
        -o -name 'libvolk.a' -o -name 'libxxhash.a' -o -name 'libabsl_*.a' \) | LC_ALL=C sort)
    if [[ ${#archives[@]} == 0 ]]; then
        echo "error: No static Vulkan renderer archives were built." >&2
        exit 1
    fi
    xcrun --sdk "${platform}" libtool -static -o "${archive}.tmp" "${archives[@]}"
    mv "${archive}.tmp" "${archive}"
}

build_framework() {
    local platform="$1" target sdk plist_platform min_key min_version install_path
    local build_dir="${OUT_DIR}/_build-podishvenus-kosmickrisp-${platform}"
    local cross_file="${OUT_DIR}/${platform}.ini"
    local framework="${OUT_DIR}/${platform}/PodishVenus.framework"
    local staging="${OUT_DIR}/${platform}/_stage/PodishVenus.framework"
    local executable resources
    case "${platform}" in
        macosx)
            target=arm64-apple-macos26.0
            plist_platform=MacOSX
            min_key=LSMinimumSystemVersion
            min_version=26.0
            install_path=PodishVenus.framework/Versions/A/PodishVenus
            executable="${staging}/Versions/A/PodishVenus"
            resources="${staging}/Versions/A/Resources"
            ;;
        iphoneos)
            target=arm64-apple-ios26.0
            plist_platform=iPhoneOS
            min_key=MinimumOSVersion
            min_version=26.0
            install_path=PodishVenus.framework/PodishVenus
            executable="${staging}/PodishVenus"
            resources="${staging}"
            ;;
    esac
    sdk="$(xcrun --sdk "${platform}" --show-sdk-path)"
    cat > "${cross_file}" <<MESON
[constants]
apple_args = ['-target', '${target}', '-isysroot', '${sdk}']

[binaries]
c = '$(xcrun --find clang)'
cpp = '$(xcrun --find clang++)'
objc = '$(xcrun --find clang)'
objcpp = '$(xcrun --find clang++)'
ar = '$(xcrun --find ar)'
strip = '$(xcrun --find strip)'
python = '${VENUS_PYTHON}'

[host_machine]
system = 'darwin'
cpu_family = 'aarch64'
cpu = 'aarch64'
endian = 'little'

[properties]
needs_exe_wrapper = true

[built-in options]
c_args = apple_args
c_link_args = apple_args
cpp_args = apple_args
cpp_link_args = apple_args
objc_args = apple_args
objc_link_args = apple_args
objcpp_args = apple_args
objcpp_link_args = apple_args
MESON
    local driver="${OUT_DIR}/${platform}/PodishKosmicKrisp.framework/PodishKosmicKrisp"
    local diligent_build="${OUT_DIR}/_diligent-kosmickrisp-${platform}"
    local -a configure=(setup "${build_dir}" "${PROJECT_DIR}" --cross-file "${cross_file}"
        --wrap-mode=nodownload "-Dapple_platform=${platform}"
        "-Dkosmickrisp_library=${driver}" "-Dvulkan_headers=${vulkan_headers}")
    if [[ "${HOST_ZINK}" == 1 ]]; then
        configure+=(-Dhost_zink=true -Dvirglrenderer:default_library=static -Dvirglrenderer:vrend=true
            '-Dvirglrenderer:platforms=[]')
    else
        configure+=(-Dhost_zink=false -Dvirglrenderer:vrend=false)
    fi
    if [[ -f "${build_dir}/meson-private/coredata.dat" ]]; then
        configure+=(--reconfigure)
    fi
    build_diligent "${platform}" "${diligent_build}" "${min_version}"
    meson "${configure[@]}" "-Ddiligent_source=${diligent_source}" \
        "-Ddiligent_archive=${diligent_build}/libPodishDiligent.a"
    meson compile -C "${build_dir}" PodishVenus

    rm -rf "${staging}"
    mkdir -p "${resources}"
    cp "${build_dir}/PodishVenus.dylib" "${executable}"
    xcrun install_name_tool -id "@rpath/${install_path}" "${executable}"
    cp "${SCRIPT_DIR}/venus/Info.plist" "${resources}/Info.plist"
    plutil -insert CFBundleSupportedPlatforms -json "[\"${plist_platform}\"]" "${resources}/Info.plist"
    plutil -insert "${min_key}" -string "${min_version}" "${resources}/Info.plist"
    mkdir -p "${resources}/Licenses"
    cp "${CACHE_DIR}/virglrenderer/COPYING" "${resources}/Licenses/virglrenderer.txt"
    if [[ "${HOST_ZINK}" == 1 ]]; then
        cp "${CACHE_DIR}/libepoxy/COPYING" "${resources}/Licenses/libepoxy.txt"
    fi
    local diligent_license="${resources}/Licenses/DiligentCore"
    mkdir -p "${diligent_license}"
    cp "${diligent_source}/License.txt" "${diligent_license}/DiligentCore.txt"
    cp "${diligent_source}/ThirdParty/SPIRV-Cross/LICENSE" "${diligent_license}/SPIRV-Cross.txt"
    cp "${diligent_source}/ThirdParty/SPIRV-Headers/LICENSE" "${diligent_license}/SPIRV-Headers.txt"
    cp "${diligent_source}/ThirdParty/Vulkan-Headers/LICENSE.md" "${diligent_license}/Vulkan-Headers.md"
    cp -R "${diligent_source}/ThirdParty/Vulkan-Headers/LICENSES" "${diligent_license}/Vulkan-Headers"
    cp "${diligent_source}/ThirdParty/xxHash/LICENSE" "${diligent_license}/xxHash.txt"
    cp "${diligent_source}/ThirdParty/volk/LICENSE.md" "${diligent_license}/volk.md"
    cp "${CACHE_DIR}/abseil-cpp/LICENSE" "${diligent_license}/Abseil.txt"

    if [[ "${platform}" == macosx ]]; then
        ln -s A "${staging}/Versions/Current"
        ln -s Versions/Current/PodishVenus "${staging}/PodishVenus"
        ln -s Versions/Current/Resources "${staging}/Resources"
    fi
    codesign --force --sign - "${staging}"
    rm -rf "${framework}"
    mv "${staging}" "${framework}"
    rmdir "$(dirname "${staging}")"
    echo "Prepared ${framework}"
}

for platform in "$@"; do
    build_framework "${platform}"
done
