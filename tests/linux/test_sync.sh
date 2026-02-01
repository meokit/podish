#!/bin/bash
set -e

echo "Compiling tests using Podman (Alpine)..."

# Use podman to compile
if command -v podman &> /dev/null; then
    CMD=podman
else
    CMD=docker
fi

$CMD run --rm -v $(pwd)/tests/linux:/src -w /src --arch 386 alpine:latest sh -c "apk add --no-cache gcc musl-dev && gcc -static -m32 test_futex.c -o test_futex && gcc -static -m32 test_mutex.c -o test_mutex"

echo "Building emulator..."
# Force rebuild of loader
(cd linux && CGO_LDFLAGS_ALLOW="-Wl,-rpath,.*" go build -a -o x86loader main.go)

# Copy library to where loader is (rpath @loader_path)
cp build/libx86emu.dylib linux/

echo "--- Running test_futex ---"
./linux/x86loader --trace tests/linux/test_futex

echo "--- Running test_mutex ---"
./linux/x86loader --trace tests/linux/test_mutex
