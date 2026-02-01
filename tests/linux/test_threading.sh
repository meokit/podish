#!/bin/bash
set -e

# Build the test_pthread binary using the same alpine container (or any linux gcc)
# We use static linking to avoid library path issues
echo "Building test_pthread..."

# Check if podman exists
if command -v podman &> /dev/null; then
    CMD=podman
else
    CMD=docker
fi

$CMD run --rm -v $(pwd)/tests/linux:/src -w /src --arch 386 alpine:latest sh -c "apk add --no-cache gcc musl-dev && gcc -static -pthread -m32 test_pthread.c -o test_pthread"

echo "Running test_pthread under x86emu..."
(cd linux && CGO_LDFLAGS_ALLOW="-Wl,-rpath,.*" go build -o x86loader main.go)
./linux/x86loader tests/linux/test_pthread
