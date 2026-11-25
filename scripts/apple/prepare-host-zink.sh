#!/usr/bin/env bash
set -euo pipefail

APPLE_SCRIPTS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${APPLE_SCRIPTS}/../.." && pwd)"
OUT_DIR="${PODISH_HOST_ZINK_DIR:-${REPO_ROOT}/PodishApp/artifacts/host-zink}"
PYTHON="${PODISH_ZINK_PYTHON:-python3}"
MESA_VERSION=25.0.7
MESA_SHA256=592272df3cf01e85e7db300c449df5061092574d099da275d19e97ef0510f8a6

if [[ "$#" == 0 ]]; then
    set -- macosx iphoneos
fi
for platform in "$@"; do
    case "${platform}" in
        macosx|iphoneos) ;;
        iphonesimulator) echo "KosmicKrisp requires a physical iOS device with Metal 4." >&2; exit 2 ;;
        *) echo "Unknown Apple SDK: ${platform}" >&2; exit 2 ;;
    esac
done

for tool in meson ninja bison flex curl xcrun; do
    if ! command -v "${tool}" >/dev/null; then
        echo "error: ${tool} is required to build PodishHostZink." >&2
        exit 1
    fi
done
PYTHON="$(command -v "${PYTHON}")"
if ! "${PYTHON}" -c 'import mako, yaml' >/dev/null 2>&1; then
    echo "error: Mesa requires Python with Mako and PyYAML; select it with PODISH_ZINK_PYTHON." >&2
    exit 1
fi
mkdir -p "${OUT_DIR}"
OUT_DIR="$(cd "${OUT_DIR}" && pwd)"
TOOLS_DIR="${OUT_DIR}/_tools"
mkdir -p "${TOOLS_DIR}"
ln -sfn "${PYTHON}" "${TOOLS_DIR}/python3"
export PATH="${TOOLS_DIR}:${PATH}"

archive="${PODISH_ZINK_MESA_ARCHIVE:-${OUT_DIR}/mesa-${MESA_VERSION}.tar.xz}"
if [[ ! -f "${archive}" ]]; then
    curl --fail --location --retry 3 "https://archive.mesa3d.org/mesa-${MESA_VERSION}.tar.xz" -o "${archive}.tmp"
    mv "${archive}.tmp" "${archive}"
fi
if [[ "$(shasum -a 256 "${archive}" | cut -d ' ' -f 1)" != "${MESA_SHA256}" ]]; then
    echo "error: Mesa source archive failed SHA256 verification." >&2
    exit 1
fi

bridge="${APPLE_SCRIPTS}/host-zink"
source_id="$(shasum -a 256 "${bridge}/mesa-apple-zink.patch" | cut -d ' ' -f 1)"
source_parent="${OUT_DIR}/_sources/${source_id}"
source_dir="${source_parent}/mesa-${MESA_VERSION}"
if [[ ! -f "${source_dir}/.podish-source-ready" ]]; then
    mkdir -p "${source_parent}"
    tar -xf "${archive}" -C "${source_parent}"
    GIT_CEILING_DIRECTORIES="${source_parent}" git -C "${source_dir}" apply "${bridge}/mesa-apple-zink.patch"
    touch "${source_dir}/.podish-source-ready"
fi
cp "${bridge}/bridge.c" "${source_dir}/src/egl/podish_host_zink_bridge.c"
cp "${bridge}/bridge.h" "${source_dir}/src/egl/podish_host_zink_bridge.h"
cp "${bridge}/exports.txt" "${source_dir}/src/egl/podish_host_zink_exports.txt"

build_framework() {
    local platform="$1" target sdk sdk_min min_key plist_platform
    local build_dir="${OUT_DIR}/_build-${platform}-${source_id:0:12}"
    local cross_file="${OUT_DIR}/${platform}.ini"
    local staging="${OUT_DIR}/${platform}/_stage/PodishHostZink.framework"
    local framework="${OUT_DIR}/${platform}/PodishHostZink.framework"
    local executable resources install_name
    case "${platform}" in
        macosx)
            target=arm64-apple-macos26.0
            sdk_min=26.0
            min_key=LSMinimumSystemVersion
            plist_platform=MacOSX
            executable="${staging}/Versions/A/PodishHostZink"
            resources="${staging}/Versions/A/Resources"
            install_name=PodishHostZink.framework/Versions/A/PodishHostZink
            ;;
        iphoneos)
            target=arm64-apple-ios26.0
            sdk_min=26.0
            min_key=MinimumOSVersion
            plist_platform=iPhoneOS
            executable="${staging}/PodishHostZink"
            resources="${staging}"
            install_name=PodishHostZink.framework/PodishHostZink
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

    local -a configure=(setup "${build_dir}" "${source_dir}" --cross-file "${cross_file}" --wrap-mode=nodownload
        --buildtype=release -Ddefault_library=static -Dgallium-drivers=zink -Dvulkan-drivers=
        -Dplatforms= -Dglx=disabled -Degl=enabled -Degl-native-platform=surfaceless
        -Dopengl=true -Dgles1=disabled -Dgles2=enabled -Dshared-glapi=enabled -Dglvnd=disabled -Dgbm=disabled
        -Dllvm=disabled -Dvideo-codecs= -Dlibunwind=disabled -Dxmlconfig=disabled -Dexpat=disabled
        -Dzstd=disabled -Dzlib=disabled -Dshader-cache=disabled)
    if [[ -f "${build_dir}/meson-private/coredata.dat" ]]; then
        configure+=(--reconfigure)
    fi
    meson "${configure[@]}"
    meson compile -C "${build_dir}" PodishHostZink

    rm -rf "${staging}"
    mkdir -p "${resources}/Licenses"
    cp "${build_dir}/src/egl/PodishHostZink.dylib" "${executable}"
    xcrun install_name_tool -id "@rpath/${install_name}" "${executable}"
    cp "${bridge}/Info.plist" "${resources}/Info.plist"
    plutil -insert CFBundleSupportedPlatforms -json "[\"${plist_platform}\"]" "${resources}/Info.plist"
    plutil -insert "${min_key}" -string "${sdk_min}" "${resources}/Info.plist"
    cp "${source_dir}/docs/license.rst" "${resources}/Licenses/Mesa.rst"
    if [[ "${platform}" == macosx ]]; then
        ln -s A "${staging}/Versions/Current"
        ln -s Versions/Current/PodishHostZink "${staging}/PodishHostZink"
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
