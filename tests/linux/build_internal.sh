#!/bin/sh
set -e

# Update & Install Deps
echo "Installing dependencies..."
apk add --no-cache gcc make musl-dev linux-headers wget ncurses-dev perl patch bash

WORKDIR="/work/build"
cd "$WORKDIR"

# Version
BUSYBOX_VER="1.37.0"

# Download
if [ ! -d "busybox-$BUSYBOX_VER" ]; then
    echo "Downloading Busybox $BUSYBOX_VER..."
    wget "https://busybox.net/downloads/busybox-$BUSYBOX_VER.tar.bz2"
    tar xjf "busybox-$BUSYBOX_VER.tar.bz2"
fi

cd "busybox-$BUSYBOX_VER"

# Configure
echo "Configuring Busybox..."
make defconfig

# Enable Static Linking
sed -i 's/# CONFIG_STATIC is not set/CONFIG_STATIC=y/' .config

# Enable Debugging Symbols
sed -i 's/# CONFIG_DEBUG is not set/CONFIG_DEBUG=y/' .config

# Disable Stripping
sed -i 's/CONFIG_STRIP=y/# CONFIG_STRIP is not set/' .config

# Disable TC (Traffic Control) - requires kernel headers not available
sed -i 's/CONFIG_TC=y/# CONFIG_TC is not set/' .config
sed -i 's/CONFIG_FEATURE_TC_INGRESS=y/# CONFIG_FEATURE_TC_INGRESS is not set/' .config

# Disable HW acceleration that causes linker errors
sed -i 's/CONFIG_SHA1_HWACCEL=y/# CONFIG_SHA1_HWACCEL is not set/' .config
sed -i 's/CONFIG_SHA256_HWACCEL=y/# CONFIG_SHA256_HWACCEL is not set/' .config

# Apply config changes non-interactively
yes "" | make oldconfig

echo "Building Busybox..."
make V=1 -j4 || make V=1


echo "Installing to rootfs..."
make install
cp -a _install/* /work/output/rootfs/

# Verify
ls -l /work/output/rootfs/bin/busybox
file /work/output/rootfs/bin/busybox || true
