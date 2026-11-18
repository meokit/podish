#!/usr/bin/env bash
#
# Build the i386 Linux rootfs that ships inside the iOS Podish.app bundle.
#
# The output is a gzip-compressed USTAR tarball placed at
#   PodishApp/Resources/Rootfs/alpine-i386.tar.gz
# which the Xcode "Prepare Seed OCI Image" build phase copies into the app
# as PodishSeedRootfs.tar.gz. The iOS app decompresses + extracts it on
# first launch with a small in-process reader.
#
# Design notes:
#   - We assemble the rootfs inside a throwaway i386 Alpine container.
#   - We install the tools we want to ship (curl, git, wget, ...).
#   - We then strip the apk package database and delete the apk binary so
#     the running app cannot fetch additional Alpine packages at runtime.
#     Apple reviewers will read this as "ships with a fixed set of tools",
#     which is the angle that keeps Guideline 2.4.5(i) comfortable for
#     App Store review.
#   - The resulting filesystem is exported as a gzip tarball so the IPA
#     stays small; extraction on the device uses Apple Compression + a
#     small tar reader.
#
# Prerequisites:
#   - podman (or docker) on the PATH
#   - binfmt + qemu-user-static registered for i386 (standard on macOS
#     and most Linux distributions)
#
# Usage:
#   ./PodishApp/scripts/build-rootfs.sh
#   PODISH_ROOTFS_OUT=/path/to/out.tar.gz ./PodishApp/scripts/build-rootfs.sh
#
set -euo pipefail

readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
readonly OUT_DIR_DEFAULT="${REPO_ROOT}/PodishApp/Resources/Rootfs"
readonly OUT_FILE_DEFAULT="${OUT_DIR_DEFAULT}/alpine-i386.tar.gz"

OUT_DIR="${PODISH_ROOTFS_OUT_DIR:-${OUT_DIR_DEFAULT}}"
OUT_FILE="${PODISH_ROOTFS_OUT:-${OUT_FILE_DEFAULT}}"

CONTAINER_RUNTIME="${PODISH_CONTAINER_RUNTIME:-}"
if [[ -z "${CONTAINER_RUNTIME}" ]]; then
    if command -v podman >/dev/null 2>&1; then
        CONTAINER_RUNTIME="podman"
    elif command -v docker >/dev/null 2>&1; then
        CONTAINER_RUNTIME="docker"
    else
        echo "error: neither podman nor docker is on PATH" >&2
        exit 1
    fi
fi
echo "Using container runtime: ${CONTAINER_RUNTIME}"

ALPINE_VERSION="${PODISH_ALPINE_VERSION:-3.19}"
ALPINE_IMAGE="docker.io/library/alpine:${ALPINE_VERSION}"

echo "Verifying i386 emulation is available..."
if ! "${CONTAINER_RUNTIME}" run --rm --platform linux/386 "${ALPINE_IMAGE}" \
        /bin/sh -c 'uname -m' >/dev/null 2>&1; then
    cat >&2 <<'EOF'
error: failed to run an i386 (linux/386) container.

On macOS / Linux hosts this usually means binfmt + qemu-user-static is
not registered. On Apple Silicon you can install it with:

    podman machine ssh sudo rpm-ostree install qemu-user-static qemu-user-binfmt
    # or, if using a plain podman install:
    sudo apt-get install -y qemu-user-static binfmt-support
    sudo update-binfmts --enable qemu-i386

After that, re-run this script.
EOF
    exit 1
fi

WORK_CTR="podish-rootfs-build-$$"
ROOTFS_TMP_DIR="$(mktemp -d -t podish-rootfs-XXXXXX)"
trap 'rm -rf "${ROOTFS_TMP_DIR}"; "${CONTAINER_RUNTIME}" rm -f "${WORK_CTR}" >/dev/null 2>&1 || true' EXIT

echo "Launching i386 Alpine build container..."
"${CONTAINER_RUNTIME}" run --rm \
    --platform linux/386 \
    --name "${WORK_CTR}" \
    --privileged \
    -e ROOTFS_BUILD=1 \
    -v "${ROOTFS_TMP_DIR}:/out:rw" \
    -w /root \
    "${ALPINE_IMAGE}" \
    /bin/sh -euc '
set -euo pipefail

apk update --no-progress

apk add --no-progress --virtual .podish-rootfs \
    alpine-baselayout \
    bash \
    busybox \
    busybox-suid \
    ca-certificates \
    coreutils \
    curl \
    file \
    git \
    libcrypto3 \
    libssl3 \
    musl \
    ncurses-terminfo \
    openssh-client-default \
    openssl \
    posix-libc-utils \
    tzdata \
    wget \
    zlib

# Replace the apk repository list with a comment so apk fails noisily if
# the user ever manages to call it. We also wipe the apk index database
# and the apk binary itself below.
install -d /etc/apk
cat >/etc/apk/repositories <<REPOS
# Podish ships with a fixed rootfs. Software repositories are intentionally
# not configured so the running app cannot fetch additional Alpine packages
# over the network. See the App Store review notes for context.
REPOS
rm -rf /var/cache/apk /var/lib/apk /lib/apk

apk del --no-progress --purge .podish-rootfs || true
find / -path /proc -prune -o -path /sys -prune -o -name apk -type f -print -delete 2>/dev/null || true

rm -rf /tmp/* /var/tmp/* /root/.ash_history

# Tar the rootfs in deterministic order so the output is reproducible
# enough for diffing two builds. We exclude pseudo-filesystems and the
# bind mount that holds the output.
tar --numeric-owner --owner=0 --group=0 --sort=name \
    -czf /out/alpine-i386.tar.gz \
    --exclude=./out \
    --exclude=./proc \
    --exclude=./sys \
    --exclude=./dev \
    --exclude=./run \
    --exclude=./var/cache/apk \
    .
'

mkdir -p "${OUT_DIR}"
mv "${ROOTFS_TMP_DIR}/alpine-i386.tar.gz" "${OUT_FILE}"

BYTES=$(wc -c <"${OUT_FILE}" | tr -d ' ')
echo
echo "Rootfs written to ${OUT_FILE} (${BYTES} bytes)."
echo "Next step: open PodishApp.xcodeproj in Xcode and the iOS scheme"
echo "will bundle this tarball as PodishSeedRootfs.tar.gz via the"
echo "Prepare Seed OCI Image build phase."
