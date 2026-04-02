#!/usr/bin/env bash
# build-rootfs.sh — Build an Alpine i386 browser rootfs and export OCI store assets.
# Output: image.json, indexes/, blobs/ in frontend/public/rootfs (ready for static deployment).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
PUBLIC_DIR="${SCRIPT_DIR}/frontend/public"
OUTPUT_DIR="${PUBLIC_DIR}/rootfs"
IMAGE_REF="localhost/podish-browser-rootfs:latest"
SAFE_IMAGE_NAME="localhost_podish-browser-rootfs_latest"
CONTAINER_NAME="podish-rootfs-builder-$$"
TEMP_TAR="$(mktemp -t podish-browser-rootfs.XXXXXX.tar)"

PACKAGES=(
  python3
  luajit
  fastfetch
  gcc
  make
  vim
  musl-dev
  bash
  coreutils
  procps-ng
  curl
  git
  less
  ncurses
  bsd-games
)

cleanup() {
  podman rm -f "$CONTAINER_NAME" > /dev/null 2>&1 || true
  rm -f "$TEMP_TAR"
}

trap cleanup EXIT

echo "==> Creating Alpine i386 container with packages..."
podman create \
  --name "$CONTAINER_NAME" \
  --platform linux/386 \
  docker.io/library/alpine:latest \
  sh -c "
    apk update &&
    apk add --no-cache ${PACKAGES[*]} &&
    git clone --depth 1 https://github.com/eembc/coremark /coremark &&
    rm -rf /coremark/.git &&
    # Create a basic profile
    echo 'export PS1=\"\\[\\e[1;32m\\]\\u@\\h\\[\\e[0m\\]:\\[\\e[1;34m\\]\\w\\[\\e[0m\\]\\$ \"' > /etc/profile.d/prompt.sh &&
    echo 'alias ll=\"ls -lah --color=auto\"' >> /etc/profile.d/prompt.sh &&
    echo 'alias la=\"ls -A --color=auto\"' >> /etc/profile.d/prompt.sh &&
    echo 'export TERM=xterm-256color' >> /etc/profile.d/prompt.sh &&
    rm -rf /var/cache/apk/*
  "

echo "==> Starting container to install packages..."
podman start -a "$CONTAINER_NAME"

echo "==> Exporting temporary rootfs tar..."
podman export "$CONTAINER_NAME" > "$TEMP_TAR"

echo "==> Importing rootfs into local Podish OCI store..."
pushd "$REPO_ROOT" > /dev/null
dotnet run --project "$REPO_ROOT/Podish.Cli/Podish.Cli.csproj" -- import "$TEMP_TAR" "$IMAGE_REF"
popd > /dev/null

STORE_DIR="$REPO_ROOT/.fiberpod/oci/images/$SAFE_IMAGE_NAME"
if [[ ! -f "$STORE_DIR/image.json" ]]; then
  echo "image.json not found in $STORE_DIR" >&2
  exit 1
fi

echo "==> Copying OCI browser assets..."
mkdir -p "$OUTPUT_DIR"
rm -f "$OUTPUT_DIR/rootfs.tar.gz"
rm -f "$OUTPUT_DIR/image.json"
rm -rf "$OUTPUT_DIR/indexes" "$OUTPUT_DIR/blobs"
rm -f "$PUBLIC_DIR/rootfs.tar.gz"
rm -f "$PUBLIC_DIR/image.json"
rm -rf "$PUBLIC_DIR/indexes" "$PUBLIC_DIR/blobs"
cp "$STORE_DIR/image.json" "$OUTPUT_DIR/image.json"
cp -R "$STORE_DIR/indexes" "$OUTPUT_DIR/indexes"
cp -R "$STORE_DIR/blobs" "$OUTPUT_DIR/blobs"

IMAGE_SIZE=$(du -sh "$OUTPUT_DIR/blobs" | cut -f1)
echo "==> Done! OCI assets saved to: $OUTPUT_DIR (blob payload $IMAGE_SIZE)"
