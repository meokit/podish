#!/bin/bash
set -e

# Directories
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &> /dev/null && pwd)
ROOTFS="$SCRIPT_DIR/rootfs"
BUILD_DIR="$SCRIPT_DIR/build_podman"

mkdir -p "$ROOTFS"
mkdir -p "$BUILD_DIR"

# Check for Podman
if ! command -v podman &> /dev/null; then
    echo "Error: podman is not installed."
    exit 1
fi

echo "Using Podman to build Busybox (Linux x86) - Debug version..."

# Run Podman container
CONTAINER_IMAGE="docker.io/i386/alpine:latest"

echo "Running build container ($CONTAINER_IMAGE) for debug version..."
podman run --rm \
    --platform linux/386 \
    -v "$BUILD_DIR:/work/build" \
    -v "$ROOTFS:/work/output/rootfs" \
    -v "$BUILD_DIR/build_internal.sh:/build_internal.sh" \
    "$CONTAINER_IMAGE" \
    /build_internal.sh


echo "Debug build complete."
echo "Debug artifacts are in $ROOTFS"