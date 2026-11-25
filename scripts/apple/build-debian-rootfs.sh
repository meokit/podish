#!/usr/bin/env bash
#
# Build the embedded i386 Debian Trixie + LXQt + Qt6 Touch Launcher rootfs
# and package it as a pre-indexed tarfs store (for tarfs + silkfs overlay) at:
#   PodishApp/Resources/<graphics-backend>/PodishRootfs/
#     ├── image.json
#     ├── graphics-backend
#     ├── blobs/rootfs.tar
#     └── indexes/rootfs.index.json
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
LAUNCHER_SRC="${REPO_ROOT}/guest/podish-launcher"
BACKEND="${PODISH_GRAPHICS_BACKEND:-vulkan}"
case "${BACKEND}" in
    vulkan|gles) ;;
    *) echo "error: PODISH_GRAPHICS_BACKEND must be vulkan or gles" >&2; exit 2 ;;
esac
OUT_STORE="${PODISH_ROOTFS_STORE_DIR:-${REPO_ROOT}/PodishApp/Resources/${BACKEND}/PodishRootfs}"
DEBIAN_IMAGE="${PODISH_DEBIAN_IMAGE:-docker.io/library/debian:trixie}"

if [[ -n "${PODISH_CONTAINER_RUNTIME:-}" ]]; then
    RUNTIME="${PODISH_CONTAINER_RUNTIME}"
elif command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    RUNTIME="docker"
elif command -v docker >/dev/null 2>&1 && [[ -S "${HOME}/.orbstack/run/docker.sock" ]] && \
     DOCKER_HOST="unix://${HOME}/.orbstack/run/docker.sock" docker info >/dev/null 2>&1; then
    export DOCKER_HOST="unix://${HOME}/.orbstack/run/docker.sock"
    RUNTIME="docker"
elif command -v podman >/dev/null 2>&1 && podman info >/dev/null 2>&1; then
    RUNTIME="podman"
else
    echo "error: no running docker or podman daemon found (OrbStack, Docker Desktop, or Podman machine)." >&2
    exit 1
fi
echo "Using container runtime: ${RUNTIME} (DOCKER_HOST=${DOCKER_HOST:-default})"

TMP_DIR="$(mktemp -d -t podish-debian-rootfs-XXXXXX)"
CTR_NAME="podish-debian-i386-build-$$"
trap 'rm -rf "${TMP_DIR}"; "${RUNTIME}" rm -f "${CTR_NAME}" >/dev/null 2>&1 || true' EXIT

echo "Building i386 Debian Trixie + LXQt + podish-launcher (${BACKEND}) inside ${DEBIAN_IMAGE}..."
"${RUNTIME}" run \
    --platform linux/386 \
    --name "${CTR_NAME}" \
    -v "${LAUNCHER_SRC}:/src/podish-launcher:ro" \
    -v "${REPO_ROOT}/tests/vulkan:/src/vulkan-tests:ro" \
    -e DEBIAN_FRONTEND=noninteractive \
    -e PODISH_GRAPHICS_BACKEND="${BACKEND}" \
    "${DEBIAN_IMAGE}" \
    /bin/bash -euxc '
apt-get update

if [[ "$PODISH_GRAPHICS_BACKEND" == vulkan ]]; then
    renderer_packages=(libvulkan1)
else
    renderer_packages=(libgl4es0)
fi

# Runtime packages: Debian base utils + Qt6 Wayland + LXQt common apps + Breeze icons + fonts
apt-get install -y --no-install-recommends \
    bash \
    coreutils \
    findutils \
    grep \
    sed \
    gawk \
    procps \
    passwd \
    login \
    ca-certificates \
    curl \
    wget \
    git \
    nano \
    vim \
    less \
    file \
    tar \
    gzip \
    bzip2 \
    xz-utils \
    zip \
    unzip \
    python3 \
    htop \
    iproute2 \
    iputils-ping \
    locales \
    dbus-daemon \
    dbus-bin \
    xkb-data \
    xdg-user-dirs \
    xdg-utils \
    shared-mime-info \
    desktop-file-utils \
    fontconfig \
    fonts-dejavu-core \
    fonts-dejavu-mono \
    fonts-noto-core \
    fonts-wqy-microhei \
    hicolor-icon-theme \
    kf6-breeze-icon-theme \
    libwayland-client0 \
    libwayland-cursor0 \
    libwayland-egl1 \
    libxkbcommon0 \
    libegl1 \
    libgl1 \
    libdrm2 \
    qt6-wayland \
    qt6-svg-plugins \
    qt6-qpa-plugins \
    libqt6opengl6 \
    lxqt-qtplugin \
    lxqt-themes \
    lxqt-config \
    lxqt-archiver \
    lximage-qt \
    pcmanfm-qt \
    qterminal \
    featherpad \
    qps \
    screengrab \
    "${renderer_packages[@]}" \
    supertuxkart

# Temporary build dependencies for the launcher.
build_packages=(
    g++ \
    cmake \
    ninja-build \
    pkg-config \
    qt6-base-dev
)
if [[ "$PODISH_GRAPHICS_BACKEND" == vulkan ]]; then
    build_packages+=(
        meson \
        bison \
        flex \
        patch \
        python3-mako \
        python3-packaging \
        python3-yaml \
        libdrm-dev \
        libexpat1-dev \
        libglvnd-dev \
        libvulkan-dev \
        libwayland-dev \
        libwayland-egl-backend-dev \
        libzstd-dev \
        zlib1g-dev \
        wayland-protocols
    )
fi
apt-get install -y --no-install-recommends "${build_packages[@]}"

cmake -S /src/podish-launcher -B /tmp/build-launcher -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DPODISH_GRAPHICS_BACKEND="$PODISH_GRAPHICS_BACKEND" \
    -DCMAKE_INSTALL_PREFIX=/usr/local
cmake --build /tmp/build-launcher
cmake --install /tmp/build-launcher --strip
install -d /usr/local/libexec
printf "%s\n" "$PODISH_GRAPHICS_BACKEND" > /usr/local/libexec/podish-graphics-backend
if [[ "$PODISH_GRAPHICS_BACKEND" == vulkan ]]; then
    bash /src/podish-launcher/build-mesa.sh
    bash /src/vulkan-tests/build.sh /usr/local/bin
    install -Dm644 /src/podish-launcher/podish-vulkan-env /usr/local/libexec/podish-vulkan-env
else
    install -d /run/podish-gles
fi
install -Dm755 /src/podish-launcher/podish-desktop-session /usr/local/bin/podish-desktop-session
install -Dm755 /src/podish-launcher/podish-supertuxkart /usr/local/bin/supertuxkart
install -Dm644 /src/podish-launcher/podish-launcher.desktop /usr/local/share/applications/podish-launcher.desktop
install -Dm644 /src/podish-launcher/podish-launcher.svg /usr/local/share/icons/hicolor/scalable/apps/podish-launcher.svg
rm -rf /tmp/build-launcher

# Remove build-only toolchain packages to keep embedded rootfs lean
apt-get purge -y --auto-remove "${build_packages[@]}"

# Configure default root password, desktop user account (uid 1000), and desktop directories
echo "root:podish" | chpasswd
if ! id -u user >/dev/null 2>&1; then
    useradd -m -U -u 1000 -s /bin/bash user
fi
install -d -m 1777 /tmp /tmp/.X11-unix /var/tmp
install -d -m 755 /run
install -d -m 755 -o 1000 -g 1000 \
    /home/user/Desktop \
    /home/user/Documents \
    /home/user/Downloads \
    /home/user/Pictures \
    /home/user/.config/lxqt \
    /home/user/.local/share/applications

cat > /home/user/.config/lxqt/lxqt.conf <<EOF
[General]
__userfile__=true
icon_theme=breeze
theme=ambiance
EOF
chown -R 1000:1000 /home/user

dbus-uuidgen --ensure=/etc/machine-id
# gl4es is selected by the legacy SuperTuxKart wrapper only.
rm -f /etc/ld.so.conf.d/podish-gl4es.conf
ldconfig
update-desktop-database /usr/share/applications /usr/local/share/applications || true
fc-cache -f

# Trim caches and bulky documentation to keep iOS/macOS bundle size compact
apt-get clean
rm -rf \
    /var/lib/apt/lists/* \
    /var/cache/apt/archives/* \
    /var/cache/apt/*.bin \
    /usr/share/doc/* \
    /usr/share/man/* \
    /usr/share/info/* \
    /usr/share/lintian/* \
    /tmp/* \
    /var/tmp/* \
    /root/.bash_history

# Verify launcher links cleanly and is an IA-32 ELF binary
file /usr/local/bin/podish-launcher
ldd /usr/local/bin/podish-launcher
if [[ "$PODISH_GRAPHICS_BACKEND" == vulkan ]]; then
    test -r /usr/local/lib/podish-zink/share/vulkan/icd.d/virtio_icd.json
    test -r /usr/local/lib/podish-zink/share/glvnd/egl_vendor.d/50_podish_zink.json
    test ! -e /usr/local/lib/podish-gl4es-sdl-shim.so
    test ! -e /usr/lib/i386-linux-gnu/gl4es
    test ! -e /run/podish-gles
else
    test -r /usr/local/lib/podish-gl4es-sdl-shim.so
    test -r /usr/lib/i386-linux-gnu/gl4es/libGL.so.1
    test ! -e /usr/local/lib/podish-zink
    test ! -e /usr/local/libexec/podish-vulkan-env
    test ! -e /usr/lib/i386-linux-gnu/libvulkan_virtio.so
    ! compgen -G "/usr/share/vulkan/icd.d/virtio_icd*.json"
fi
'

echo "Exporting container filesystem to uncompressed tar..."
RAW_TAR="${TMP_DIR}/rootfs.tar"
"${RUNTIME}" export "${CTR_NAME}" -o "${RAW_TAR}"

rm -rf "${OUT_STORE}"
mkdir -p "${OUT_STORE}"
"${PODISH_PYTHON:-/usr/bin/python3}" "${SCRIPT_DIR}/index-tarfs.py" \
    "${RAW_TAR}" "${OUT_STORE}" "podish/debian-lxqt-${BACKEND}:i386"
printf '%s\n' "${BACKEND}" > "${OUT_STORE}/graphics-backend"

echo "Debian i386 tarfs rootfs ready at ${OUT_STORE}"
ls -lh "${OUT_STORE}" "${OUT_STORE}/blobs" "${OUT_STORE}/indexes"
