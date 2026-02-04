#!/usr/bin/env bash

# Build script moved from benchmark/coremark/build.sh
# Only builds i686 Linux CoreMark and will clone/pull CoreMark into benchmark/coremark

set -euo pipefail

COREMARK_REPO="https://github.com/eembc/coremark.git"
COREMARK_DIR="benchmark/coremark"
ITERATIONS=3000

OUTFILE="coremark_i686_linux"

echo "CoreMark build (i686 Linux) - output: ./${OUTFILE}"

# Ensure git is available for cloning/pulling
if ! command -v git &> /dev/null; then
    echo "Error: git not found. Please install git."
    exit 1
fi

# Fetch or update CoreMark sources
if [ -d "$COREMARK_DIR/.git" ]; then
    echo "CoreMark repo exists, updating..."
else
    echo "Cloning CoreMark into $COREMARK_DIR..."
    git clone --depth 1 "$COREMARK_REPO" "$COREMARK_DIR"
fi

# Check zig
if ! command -v zig &> /dev/null; then
    echo "Error: zig not found. Install zig (brew install zig or from https://ziglang.org)."
    exit 1
fi

echo "Building CoreMark (i686 Linux)..."

# Clean then build for i686 linux (musl) no-fpu/no-sse conservative flags
make -C "$COREMARK_DIR" clean
make -C "$COREMARK_DIR" \
    CC="zig cc -target x86-linux-musl" \
    PORT_DIR=linux \
    ITERATIONS=$ITERATIONS \
    XCFLAGS="-O3 -march=i686 -DPERFORMANCE_RUN=1" \
    REBUILD=1

# coremark produces coremark.exe by default in the repo root
if [ -f "$COREMARK_DIR/coremark.exe" ]; then
    mv "$COREMARK_DIR/coremark.exe" "./${OUTFILE}"
    chmod +x "./${OUTFILE}" || true
    echo "Built: ./${OUTFILE}"
else
    echo "Build finished but coremark.exe not found in $COREMARK_DIR"
    exit 1
fi

echo "Done."
