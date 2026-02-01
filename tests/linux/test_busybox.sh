#!/bin/bash
set -e

# Directories
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &> /dev/null && pwd)
ROOTFS="$SCRIPT_DIR/rootfs"
BUILD_DIR="$SCRIPT_DIR/build_podman"
EMULATOR="$SCRIPT_DIR/../../build/x86emu-linux"

mkdir -p "$ROOTFS"
mkdir -p "$BUILD_DIR"

# Build x86emu-linux (Go Loader)
echo "Building x86emu-linux loader..."
(cd "$SCRIPT_DIR/../../linux" && CGO_LDFLAGS_ALLOW="-Wl,-rpath,.*" go build -o "$EMULATOR" main.go) || {
    echo "Failed to build x86emu-linux!"
    exit 1
}

# Check if we need to build Busybox
if [ -f "$ROOTFS/bin/busybox" ]; then
    echo "Busybox binary found at $ROOTFS/bin/busybox. Skipping build."
else
    # Check for Podman
    if ! command -v podman &> /dev/null; then
        echo "Error: podman is not installed."
        exit 1
    fi

    echo "Using Podman to build Busybox (Linux x86)..."

# Define the build script that will run INSIDE the container
cat <<EOF > "$BUILD_DIR/build_internal.sh"
#!/bin/sh
set -e

# Install dependencies
echo "Installing build dependencies..."
apk add --no-cache build-base linux-headers curl tar bzip2

# Variables
BUSYBOX_VER="1.37.0"
BUSYBOX_TAR="busybox-\${BUSYBOX_VER}.tar.bz2"
BUSYBOX_URL="https://busybox.net/downloads/\$BUSYBOX_TAR"
WORK_DIR="/work/build"
OUTPUT_DIR="/work/output"

mkdir -p "\$WORK_DIR"
mkdir -p "\$OUTPUT_DIR"

cd "\$WORK_DIR"

# Download
if [ ! -f "\$BUSYBOX_TAR" ]; then
    echo "Downloading Busybox..."
    curl -L -o "\$BUSYBOX_TAR" "\$BUSYBOX_URL"
fi

# Extract
if [ ! -d "busybox-\${BUSYBOX_VER}" ]; then
    echo "Extracting..."
    tar -xjf "\$BUSYBOX_TAR"
fi

cd "busybox-\${BUSYBOX_VER}"

# Configure
if [ ! -f .config ]; then
    echo "Configuring..."
    make defconfig
    # Enable static linking - only needed once but harmless to repeat
    sed -i 's/# CONFIG_STATIC is not set/CONFIG_STATIC=y/' .config
fi

# Force disable TC (Traffic Control) as it requires kernel headers potentially missing or incompatible
# This must be done every time to ensure persistence doesn't break build
sed -i 's/CONFIG_TC=y/# CONFIG_TC is not set/' .config
sed -i 's/CONFIG_FEATURE_TC_INGRESS=y/# CONFIG_FEATURE_TC_INGRESS is not set/' .config
# Disable HW acceleration that causes linker errors with text relocations
sed -i 's/CONFIG_SHA1_HWACCEL=y/# CONFIG_SHA1_HWACCEL is not set/' .config
sed -i 's/CONFIG_SHA256_HWACCEL=y/# CONFIG_SHA256_HWACCEL is not set/' .config

# Build
echo "Building..."
make -j\$(nproc)

# Install
echo "Installing to \$OUTPUT_DIR..."
# Create a temporary install dir
make CONFIG_PREFIX="\$OUTPUT_DIR/rootfs" install

# Copy testsuite
echo "Copying testsuite..."
cp -r testsuite "\$OUTPUT_DIR/rootfs/testsuite"

# Verify
echo "Verifying build..."
file "\$OUTPUT_DIR/rootfs/bin/busybox"
"\$OUTPUT_DIR/rootfs/bin/busybox" --help | head -n 5

EOF

chmod +x "$BUILD_DIR/build_internal.sh"

# Run Podman container
# We use --platform linux/amd64 just in case we are on ARM mac, 
# but we want to build natively for x86 if possible, OR rely on container emulation if needed.
# Since x86emu is 32-bit x86 simulator, we ideally want 32-bit binary.
# Alpine linux/386 is valid.
CONTAINER_IMAGE="docker.io/i386/alpine:latest"

echo "Running build container ($CONTAINER_IMAGE)..."
podman run --rm \
    --platform linux/386 \
    -v "$BUILD_DIR:/work/build" \
    -v "$ROOTFS:/work/output/rootfs" \
    -v "$BUILD_DIR/build_internal.sh:/build_internal.sh" \
    "$CONTAINER_IMAGE" \
    /build_internal.sh

# Cleanup internal script
# rm "$BUILD_DIR/build_internal.sh"

    echo "Build complete."
    echo "Artifacts are in $ROOTFS"
fi

# Create runner script for x86emu
cat <<EOF > "$ROOTFS/run_tests_internal.sh"
#!/bin/busybox sh
echo "Starting Busybox Testsuite inside x86emu..."
export PATH=/bin
cd /testsuite
# Run a smoke test first
./runtest echo
# Note: running ALL tests might take a while and fail on missing features.
EOF
chmod +x "$ROOTFS/run_tests_internal.sh"

# Run Test
echo "Checking if busybox runs under x86emu..."
if [ -x "$EMULATOR" ]; then
    # Helper to run emulator with rootfs
    run_emu() {
        "$EMULATOR" --rootfs "$ROOTFS" "$@"
    }

    
    run_emu "$ROOTFS/bin/busybox" echo "Hello from x86emu Busybox!"
    
    echo "Running Busybox 'echo' test..."
    run_emu "$ROOTFS/bin/busybox" sh /run_tests_internal.sh
else
    echo "Emulator binary not found at $EMULATOR. Please build x86emu first."
fi
