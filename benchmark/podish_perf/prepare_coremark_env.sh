#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOTFS_DIR="${SCRIPT_DIR}/rootfs/coremark_i386_alpine"
CONTAINERFILE="${SCRIPT_DIR}/Containerfile.coremark"
IMAGE_NAME="localhost/x86emu-coremark-perf:latest"
ARCH="386"
COREMARK_ITERATIONS="3000"
FORCE_REBUILD=false
PODMAN_STORAGE_OPTS=()

usage() {
    cat <<EOF
Usage: $(basename "$0") [options]

Build a preconfigured i386 Alpine CoreMark environment with podman and export
it as a rootfs directory for Podish.Cli --rootfs.

Options:
  --rootfs-dir PATH          Export destination (default: ${ROOTFS_DIR})
  --image-name NAME          Podman image tag (default: ${IMAGE_NAME})
  --arch ARCH                Podman build architecture (mapped to linux/<arch>, default: ${ARCH})
  --iterations N             CoreMark build iterations (default: ${COREMARK_ITERATIONS})
  --force                    Rebuild image and replace exported rootfs
  -h, --help                 Show this help
EOF
}

log() {
    printf '[coremark-env] %s\n' "$1"
}

arch_to_platform() {
    case "$1" in
        386|i386)
            echo "linux/386"
            ;;
        amd64|x86_64)
            echo "linux/amd64"
            ;;
        arm64|aarch64)
            echo "linux/arm64"
            ;;
        *)
            echo "linux/$1"
            ;;
    esac
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rootfs-dir)
            ROOTFS_DIR="$2"
            shift 2
            ;;
        --image-name)
            IMAGE_NAME="$2"
            shift 2
            ;;
        --arch)
            ARCH="$2"
            shift 2
            ;;
        --iterations)
            COREMARK_ITERATIONS="$2"
            shift 2
            ;;
        --force)
            FORCE_REBUILD=true
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            usage >&2
            exit 1
            ;;
    esac
done

if ! command -v podman >/dev/null 2>&1; then
    echo "podman is required" >&2
    exit 1
fi

if [[ ! -f "${CONTAINERFILE}" ]]; then
    echo "Containerfile not found: ${CONTAINERFILE}" >&2
    exit 1
fi

if [[ -d "${ROOTFS_DIR}" && "${FORCE_REBUILD}" != "true" ]]; then
    log "rootfs already exists at ${ROOTFS_DIR}; reuse it or pass --force"
    exit 0
fi

PODMAN_PLATFORM="$(arch_to_platform "${ARCH}")"
if [[ "$(id -u)" -ne 0 ]]; then
    PODMAN_STORAGE_OPTS+=(--storage-opt ignore_chown_errors=true)
fi

log "building image ${IMAGE_NAME} with arch=${ARCH} platform=${PODMAN_PLATFORM}"
podman build \
    --platform "${PODMAN_PLATFORM}" \
    "${PODMAN_STORAGE_OPTS[@]}" \
    --build-arg "COREMARK_ITERATIONS=${COREMARK_ITERATIONS}" \
    -f "${CONTAINERFILE}" \
    -t "${IMAGE_NAME}" \
    "${SCRIPT_DIR}"

if [[ -d "${ROOTFS_DIR}" ]]; then
    log "removing previous rootfs ${ROOTFS_DIR}"
    rm -rf "${ROOTFS_DIR}"
fi
mkdir -p "${ROOTFS_DIR}"

container_id="$(podman create "${IMAGE_NAME}" /bin/true)"
cleanup() {
    podman rm "${container_id}" >/dev/null 2>&1 || true
}
trap cleanup EXIT

log "exporting image filesystem to ${ROOTFS_DIR}"
podman export "${container_id}" | tar -xf - -C "${ROOTFS_DIR}"

test -f "${ROOTFS_DIR}/coremark/Makefile"
test -x "${ROOTFS_DIR}/usr/bin/gcc"
test -x "${ROOTFS_DIR}/coremark/coremark.exe"

log "ready: ${ROOTFS_DIR}"
