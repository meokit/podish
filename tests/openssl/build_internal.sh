#!/bin/sh
set -e

# Update & Install Deps
echo "Installing dependencies..."
apk add --no-cache gcc make musl-dev linux-headers openssl openssl-dev

WORKDIR="/work/build"
cd "$WORKDIR"

echo "Installing to rootfs..."
# Copy OpenSSL binary and libraries from Alpine to rootfs
mkdir -p /work/output/rootfs/bin
mkdir -p /work/output/rootfs/lib
mkdir -p /work/output/rootfs/usr/lib
mkdir -p /work/output/rootfs/usr/local/ssl
mkdir -p /work/output/rootfs/etc/ssl

cp /usr/bin/openssl /work/output/rootfs/bin/
cp -d /lib/libcrypto.so* /work/output/rootfs/lib/ || true
cp -d /lib/libssl.so* /work/output/rootfs/lib/ || true
cp -d /usr/lib/libcrypto.so* /work/output/rootfs/usr/lib/ || true
cp -d /usr/lib/libssl.so* /work/output/rootfs/usr/lib/ || true
cp -r /usr/lib/ossl-modules /work/output/rootfs/usr/lib/ || true
cp -r /usr/lib/engines-3 /work/output/rootfs/usr/lib/ || true
cp -r /etc/ssl/* /work/output/rootfs/etc/ssl/ || true

# Also copy ld-musl so dynamic binaries can run
cp -d /lib/ld-musl-*.so* /work/output/rootfs/lib/ || true


echo "Building test_crypto dynamically..."
gcc /test_crypto.c -lssl -lcrypto -o /work/output/rootfs/bin/test_crypto || { echo "Compilation failed"; exit 1; }

# Verify
echo "Verifying build..."
ls -l /work/output/rootfs/bin/openssl
ls -l /work/output/rootfs/bin/test_crypto
file /work/output/rootfs/bin/test_crypto || true
