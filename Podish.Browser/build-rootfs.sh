#!/usr/bin/env bash
# build-rootfs.sh — Build an Alpine i386 rootfs with common tools using podman.
# Output: rootfs.tar.gz in the wwwroot directory (ready for static deployment).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
OUTPUT="${SCRIPT_DIR}/frontend/public/rootfs.tar.gz"
CONTAINER_NAME="podish-rootfs-builder-$$"

PACKAGES=(
  python3
  luajit
  fastfetch
  gcc
  vim
  musl-dev
  bash
  coreutils
  procps-ng
  curl
  less
  ncurses
)

echo "==> Creating Alpine i386 container with packages..."
podman create \
  --name "$CONTAINER_NAME" \
  --platform linux/386 \
  docker.io/library/alpine:latest \
  sh -c "
    apk update &&
    apk add --no-cache ${PACKAGES[*]} &&
    # Create a basic profile
    echo 'export PS1=\"\\[\\e[1;32m\\]\\u@\\h\\[\\e[0m\\]:\\[\\e[1;34m\\]\\w\\[\\e[0m\\]\\$ \"' > /etc/profile.d/prompt.sh &&
    echo 'alias ll=\"ls -lah --color=auto\"' >> /etc/profile.d/prompt.sh &&
    echo 'alias la=\"ls -A --color=auto\"' >> /etc/profile.d/prompt.sh &&
    echo 'export TERM=xterm-256color' >> /etc/profile.d/prompt.sh &&
    rm -rf /var/cache/apk/*
  "

echo "==> Starting container to install packages..."
podman start -a "$CONTAINER_NAME"

echo "==> Exporting rootfs..."
podman export "$CONTAINER_NAME" | gzip -9 > "$OUTPUT"

echo "==> Cleaning up container..."
podman rm -f "$CONTAINER_NAME" > /dev/null 2>&1 || true

SIZE=$(du -h "$OUTPUT" | cut -f1)
echo "==> Done! Rootfs saved to: $OUTPUT ($SIZE)"
