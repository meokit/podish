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

echo "Using Podman to build OpenSSL (Linux x86 - Static)..."

# Run Podman container
CONTAINER_IMAGE="docker.io/i386/alpine:latest"

echo "Running build container ($CONTAINER_IMAGE)..."
podman run --rm \
    --platform linux/386 \
    -v "$BUILD_DIR:/work/build" \
    -v "$ROOTFS:/work/output/rootfs" \
    -v "$SCRIPT_DIR/build_internal.sh:/build_internal.sh" \
    -v "$SCRIPT_DIR/test_crypto.c:/test_crypto.c" \
    "$CONTAINER_IMAGE" \
    sh /build_internal.sh

echo "Build complete."
echo "Artifacts are in $ROOTFS"
