#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
OUT_DIR="${PODISH_VENUS_DIR:-${REPO_ROOT}/PodishApp/artifacts/venus/vulkan}"
mkdir -p "${OUT_DIR}"
OUT_DIR="$(cd "${OUT_DIR}" && pwd)"
CACHE_DIR="${PODISH_VENUS_DEPS_DIR:-${OUT_DIR}/_cache}"
mkdir -p "${CACHE_DIR}"
CACHE_DIR="$(cd "${CACHE_DIR}" && pwd)"

MESA_VERSION=26.2.7
MESA_SHA256=04294f75d22e9f61cd1e050206bc48b3d39366e788f1803a3368e3d5a872ce18
SPIRV_HEADERS_REV=496543121ce6419f23d6fa5d7194ba66c36212d2
SPIRV_TOOLS_REV=ef96ed763b43b59b33b31b362f09a02b729fa1c9
SPIRV_TRANSLATOR_REV=1d83c989929dbb73a08c701d25e02569c6348726
VULKAN_HEADERS_REV=6802bb4733b63ed5efd3adb308a6c885ef180ea1

mesa_patches=("${SCRIPT_DIR}/venus/kosmickrisp-ios-metal-wsi.patch"
    "${SCRIPT_DIR}/venus/kosmickrisp-metal-texture-capabilities.patch"
    "${SCRIPT_DIR}/venus/kosmickrisp-drawable-ownership.patch"
    "${SCRIPT_DIR}/venus/kosmickrisp-drawable-residency.patch")

prepare_mesa_source() (
    local destination="$1"
    local archive="${CACHE_DIR}/mesa-${MESA_VERSION}.tar.xz"
    local staging source download mesa_patch
    staging="$(mktemp -d "${CACHE_DIR}/.mesa-${MESA_VERSION}.XXXXXX")"
    trap 'find "${staging}" -depth -delete' EXIT
    source="${staging}/mesa-${MESA_VERSION}"

    download="${archive}"
    if [[ ! -f "${archive}" ]]; then
        download="${staging}/mesa.tar.xz"
        curl --fail --location --retry 2 \
            "https://archive.mesa3d.org/mesa-${MESA_VERSION}.tar.xz" \
            --output "${download}"
    fi
    if [[ "$(shasum -a 256 "${download}" | cut -d ' ' -f 1)" != "${MESA_SHA256}" ]]; then
        echo "error: Mesa archive checksum did not match: ${download}" >&2
        exit 1
    fi
    if [[ "${download}" != "${archive}" ]]; then
        mv -f "${download}" "${archive}"
    fi
    tar -xJf "${archive}" -C "${staging}"
    for mesa_patch in "${mesa_patches[@]}"; do
        patch --batch --forward --fuzz=0 -d "${source}" -p1 < "${mesa_patch}"
    done

    if [[ -n "${destination}" ]]; then
        # Publish only after all patches apply. A shared cache can keep the first complete tree.
        if ln -sn "${source#"${CACHE_DIR}/"}" "${destination}" 2>/dev/null; then
            trap - EXIT
        else
            [[ -d "${destination}" ]]
        fi
    fi
)

if [[ "${1:-}" == --check-patches ]]; then
    if [[ "$#" -ne 1 ]]; then
        echo 'usage: prepare-kosmickrisp.sh --check-patches' >&2
        exit 2
    fi
    prepare_mesa_source ""
    echo "KosmicKrisp patches apply to Mesa ${MESA_VERSION}."
    exit 0
fi

NATIVE_SDK="$(xcrun --sdk macosx --show-sdk-path)"
CLANG="$(xcrun --sdk macosx --find clang)"
CLANGXX="$(xcrun --sdk macosx --find clang++)"

if [[ "$#" -eq 0 ]]; then
    set -- macosx iphoneos
fi
for platform in "$@"; do
    case "${platform}" in
        macosx|iphoneos) ;;
        *) echo "error: KosmicKrisp requires Metal 4 on Apple Silicon macOS or on an iOS device." >&2; exit 2 ;;
    esac
done
if [[ "$(uname -m)" != arm64 ]]; then
    echo "error: Use an Apple Silicon macOS host to build PodishKosmicKrisp." >&2
    exit 1
fi
for tool in cmake curl git meson ninja patch pkg-config; do
    if ! command -v "${tool}" >/dev/null; then
        echo "error: ${tool} is required to build PodishKosmicKrisp." >&2
        exit 1
    fi
done

checkout() {
    local name="$1" revision="$2" repo="$3" dir="${CACHE_DIR}/$1"
    if [[ ! -e "${dir}/.git" ]]; then
        git init --quiet "${dir}"
        git -C "${dir}" remote add origin "https://github.com/KhronosGroup/${repo}.git"
    fi
    if ! git -C "${dir}" cat-file -e "${revision}^{commit}" 2>/dev/null; then
        git -C "${dir}" fetch --depth 1 origin "${revision}"
    fi
    if [[ "$(git -C "${dir}" rev-parse --verify HEAD 2>/dev/null || true)" != "${revision}" ]]; then
        git -C "${dir}" checkout --quiet --detach "${revision}"
    fi
}

checkout Vulkan-Headers "${VULKAN_HEADERS_REV}" Vulkan-Headers

source_id="$(
    {
        printf '%s\n' "${MESA_SHA256}"
        shasum -a 256 "${mesa_patches[@]}" | cut -d ' ' -f 1
    } | shasum -a 256 | cut -d ' ' -f 1
)"
mesa_source="${CACHE_DIR}/mesa-${MESA_VERSION}-${source_id}"
if [[ ! -d "${mesa_source}" ]]; then
    prepare_mesa_source "${mesa_source}"
fi

llvm_config="${PODISH_KOSMICKRISP_LLVM_CONFIG:-$(command -v llvm-config || true)}"
if [[ -z "${llvm_config}" ]] && command -v brew >/dev/null; then
    llvm_config="$(brew --prefix llvm@21)/bin/llvm-config"
fi
if [[ ! -x "${llvm_config}" ]] || [[ "$("${llvm_config}" --version)" != 21.1.* ]]; then
    echo "error: LLVM and Clang 21.1 are required for Mesa's native shader tools. Point PODISH_KOSMICKRISP_LLVM_CONFIG at llvm-config from that installation." >&2
    exit 1
fi
export PATH="$(dirname "${llvm_config}"):${PATH}"
if [[ -n "${PODISH_KOSMICKRISP_PYTHON:-}" ]]; then
    "${PODISH_KOSMICKRISP_PYTHON}" -c 'import mako, packaging, yaml'
    export PATH="$(dirname "${PODISH_KOSMICKRISP_PYTHON}"):${PATH}"
fi

toolchain="${OUT_DIR}/_kosmickrisp-tools"
if [[ -n "${PODISH_KOSMICKRISP_TOOLCHAIN_DIR:-}" ]]; then
    toolchain="${PODISH_KOSMICKRISP_TOOLCHAIN_DIR}"
else
    checkout SPIRV-Headers "${SPIRV_HEADERS_REV}" SPIRV-Headers
    checkout SPIRV-Tools "${SPIRV_TOOLS_REV}" SPIRV-Tools
    checkout SPIRV-LLVM-Translator "${SPIRV_TRANSLATOR_REV}" SPIRV-LLVM-Translator

    cmake_options=(-G Ninja -DCMAKE_BUILD_TYPE=Release
        -DCMAKE_OSX_ARCHITECTURES=arm64 "-DCMAKE_OSX_SYSROOT=${NATIVE_SDK}"
        "-DCMAKE_C_COMPILER=${CLANG}" "-DCMAKE_CXX_COMPILER=${CLANGXX}"
        "-DCMAKE_INSTALL_PREFIX=${toolchain}")
    spirv_build="${OUT_DIR}/_build-kosmickrisp-spirv"
    cmake -S "${CACHE_DIR}/SPIRV-Tools" -B "${spirv_build}" \
        "${cmake_options[@]}" "-DSPIRV-Headers_SOURCE_DIR=${CACHE_DIR}/SPIRV-Headers" \
        -DSPIRV_SKIP_TESTS=ON -DSPIRV_SKIP_EXECUTABLES=ON -DSPIRV_WERROR=OFF
    cmake --build "${spirv_build}"
    cmake --install "${spirv_build}"

    translator_build="${OUT_DIR}/_build-kosmickrisp-translator"
    cmake -S "${CACHE_DIR}/SPIRV-LLVM-Translator" -B "${translator_build}" \
        "${cmake_options[@]}" "-DLLVM_DIR=$("${llvm_config}" --cmakedir)" \
        "-DLLVM_EXTERNAL_SPIRV_HEADERS_SOURCE_DIR=${CACHE_DIR}/SPIRV-Headers" \
        -DLLVM_SPIRV_BUILD_EXTERNAL=ON -DLLVM_SPIRV_INCLUDE_TESTS=OFF -DBUILD_SHARED_LIBS=ON
    cmake --build "${translator_build}"
    cmake --install "${translator_build}"
fi

common_opts=(--buildtype=release --wrap-mode=nodownload -Dplatforms=macos
    -Dvulkan-drivers=kosmickrisp -Dgallium-drivers= -Dopengl=false
    -Dzstd=disabled)
mesa_build="${OUT_DIR}/_build-kosmickrisp/${source_id}"
native_build="${mesa_build}/native"
native_tools="${mesa_build}/tools"
native_setup=()
if [[ -f "${native_build}/meson-private/coredata.dat" ]]; then
    native_setup=(--reconfigure)
fi
PKG_CONFIG_PATH="${toolchain}/lib/pkgconfig${PKG_CONFIG_PATH:+:${PKG_CONFIG_PATH}}" \
    SDKROOT="${NATIVE_SDK}" MACOSX_DEPLOYMENT_TARGET=26.0 \
    CC="${CLANG}" CXX="${CLANGXX}" OBJC="${CLANG}" \
    meson setup "${native_build}" "${mesa_source}" "${native_setup[@]}" \
    "${common_opts[@]}" -Dllvm=enabled -Dspirv-tools=enabled \
    -Dinstall-mesa-clc=true -Dinstall-precomp-compiler=true
SDKROOT="${NATIVE_SDK}" MACOSX_DEPLOYMENT_TARGET=26.0 ninja -C "${native_build}" \
    src/compiler/clc/mesa_clc src/compiler/spirv/vtn_bindgen2 src/kosmickrisp/clc/kk_clc
mkdir -p "${native_tools}"
ln -sfn "${native_build}/src/compiler/clc/mesa_clc" "${native_tools}/mesa_clc"
ln -sfn "${native_build}/src/compiler/spirv/vtn_bindgen2" "${native_tools}/vtn_bindgen2"
ln -sfn "${native_build}/src/kosmickrisp/clc/kk_clc" "${native_tools}/kk_clc"
export PATH="${native_tools}:${PATH}"

build_framework() {
    local platform="$1" target sdk subsystem min_key plist_platform framework_binary resources install_name
    local name=PodishKosmicKrisp
    local output="${OUT_DIR}/${platform}"
    local build="${mesa_build}/${platform}"
    local cross_file="${mesa_build}/${platform}.ini"
    local sdk_major
    sdk="$(xcrun --sdk "${platform}" --show-sdk-path)"
    sdk_major="$(xcrun --sdk "${platform}" --show-sdk-version)"
    if (( ${sdk_major%%.*} < 26 )); then
        echo "error: Xcode must provide the ${platform} 26 or newer SDK for Metal 4." >&2
        exit 1
    fi
    mkdir -p "${output}"
    local temp
    temp="$(mktemp -d "${output}/.kosmickrisp.XXXXXX")"
    local framework="${temp}/${name}.framework"
    if [[ "${platform}" == macosx ]]; then
        target=arm64-apple-macos26.0
        subsystem=macos
        min_key=LSMinimumSystemVersion
        plist_platform=MacOSX
        framework_binary="${framework}/Versions/A/${name}"
        resources="${framework}/Versions/A/Resources"
        install_name="@rpath/${name}.framework/Versions/A/${name}"
    else
        target=arm64-apple-ios26.0
        subsystem=ios
        min_key=MinimumOSVersion
        plist_platform=iPhoneOS
        framework_binary="${framework}/${name}"
        resources="${framework}"
        install_name="@rpath/${name}.framework/${name}"
    fi
    cat > "${cross_file}" <<MESON
[constants]
apple_args = ['-target', '${target}', '-isysroot', '${sdk}']

[binaries]
c = '${CLANG}'
cpp = '${CLANGXX}'
objc = '${CLANG}'
ar = '$(xcrun --sdk "${platform}" --find ar)'
strip = '$(xcrun --sdk "${platform}" --find strip)'
pkg-config = '$(command -v pkg-config)'

[host_machine]
system = 'darwin'
kernel = 'xnu'
subsystem = '${subsystem}'
cpu_family = 'aarch64'
cpu = 'arm64'
endian = 'little'

[properties]
needs_exe_wrapper = true

[built-in options]
c_args = apple_args
cpp_args = apple_args
objc_args = apple_args
c_link_args = apple_args + ['-lobjc', '-Wl,-headerpad_max_install_names']
cpp_link_args = apple_args + ['-lobjc', '-Wl,-headerpad_max_install_names']
objc_link_args = apple_args + ['-lobjc', '-Wl,-headerpad_max_install_names']
MESON
    local -a setup=()
    if [[ -f "${build}/meson-private/coredata.dat" ]]; then
        setup=(--reconfigure)
    fi
    # The pinned native tools are the only LLVM consumers. The device slice
    # compiles against its own SDK and does not embed LLVM or the translator.
    PKG_CONFIG_LIBDIR=/dev/null PKG_CONFIG_PATH= \
        meson setup "${build}" "${mesa_source}" "${setup[@]}" --cross-file "${cross_file}" \
        "${common_opts[@]}" -Dllvm=disabled -Dspirv-tools=disabled \
        -Dmesa-clc=system -Dprecomp-compiler=system
    ninja -C "${build}" src/kosmickrisp/vulkan/libvulkan_kosmickrisp.dylib

    mkdir -p "${resources}/Licenses"
    cp "${build}/src/kosmickrisp/vulkan/libvulkan_kosmickrisp.dylib" "${framework_binary}"
    xcrun install_name_tool -id "${install_name}" "${framework_binary}"
    cp "${SCRIPT_DIR}/venus/Info.plist" "${resources}/Info.plist"
    plutil -replace CFBundleExecutable -string "${name}" "${resources}/Info.plist"
    plutil -replace CFBundleName -string "${name}" "${resources}/Info.plist"
    plutil -replace CFBundleIdentifier -string org.podish.KosmicKrisp "${resources}/Info.plist"
    plutil -replace CFBundleShortVersionString -string "${MESA_VERSION}" "${resources}/Info.plist"
    plutil -insert CFBundleSupportedPlatforms -json "[\"${plist_platform}\"]" "${resources}/Info.plist"
    plutil -insert "${min_key}" -string 26.0 "${resources}/Info.plist"
    cp -R "${mesa_source}/licenses" "${resources}/Licenses/Mesa"
    cp "${CACHE_DIR}/Vulkan-Headers/LICENSE.md" "${resources}/Licenses/Vulkan-Headers.md"
    cp -R "${CACHE_DIR}/Vulkan-Headers/LICENSES" "${resources}/Licenses/Vulkan-Headers"
    if [[ "${platform}" == macosx ]]; then
        ln -s A "${framework}/Versions/Current"
        ln -s "Versions/Current/${name}" "${framework}/${name}"
        ln -s Versions/Current/Resources "${framework}/Resources"
    fi
    codesign --force --sign - "${framework}"
    rm -rf "${output}/${name}.framework"
    mv "${framework}" "${output}/${name}.framework"
    rmdir "${temp}"
    echo "Prepared ${output}/${name}.framework"
}

for platform in "$@"; do
    build_framework "${platform}"
done
