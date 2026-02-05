#!/bin/bash
set -e

# Bifrost (x86emu C#) Full Build & Test Script

# 1. Build C++ Core
echo ">>> Building x86emu core..."
mkdir -p build
pushd build > /dev/null
cmake ..
cmake --build .
popd > /dev/null

# 2. Codesign (macOS)
if [[ "$OSTYPE" == "darwin"* ]]; then
    echo ">>> Codesigning libfibercpu.dylib..."
    codesign -f -s - build/bin/libfibercpu.dylib
fi

# 3. Build C# Project (Fiberish)
echo ">>> Building Fiberish..."
dotnet build Fiberish.App/Fiberish.App.csproj

# Copy dylib to output
cp build/bin/libfibercpu.dylib Fiberish.App/bin/Debug/net8.0/

# 4. Build Linux Test Cases (Static x86 binaries)
echo ">>> Building Linux test cases..."
mkdir -p tests/linux/assets
BUILD_CMD="zig cc -target x86-linux-musl -static"

echo "  - hello_static"
$BUILD_CMD -o tests/linux/assets/hello_static tests/linux/hello.c

echo "  - syscall_test"
$BUILD_CMD -o tests/linux/assets/syscall_test tests/linux/syscall_test.c

echo "  - test_futex"
$BUILD_CMD -o tests/linux/assets/test_futex tests/linux/test_futex.c

echo "  - test_mutex"
$BUILD_CMD -o tests/linux/assets/test_mutex tests/linux/test_mutex.c

echo "  - test_pthread"
$BUILD_CMD -o tests/linux/assets/test_pthread tests/linux/test_pthread.c

# 5. Run Tests
echo ""
echo ">>> Running Tests..."

run_test() {
    local name=$1
    local bin=$2
    local args=$3
    echo -n "Test $name: "
    
    # Run Bifrost and capture output. 
    # Dotnet run --no-build will propagate the exit code of our Main method.
    if dotnet run --project Fiberish.App/Fiberish.App.csproj --no-build -- $args $bin > /tmp/emu_test_cs.log 2>&1; then
        echo "PASSED"
    else
        local exit_code=$?
        echo "FAILED (Exit Code $exit_code. Check /tmp/emu_test_cs.log)"
        cat /tmp/emu_test_cs.log
        exit 1
    fi
}


run_test "Hello World"  "tests/linux/assets/hello_static" ""
run_test "Linux Syscalls" "tests/linux/assets/syscall_test" "--rootfs tests/linux/assets"
run_test "Futex Sync"   "tests/linux/assets/test_futex" ""
run_test "Mutex Lock"   "tests/linux/assets/test_mutex" ""
run_test "Pthread Basic" "tests/linux/assets/test_pthread" ""

# Build and run fork/wait test
echo "Building test_fork_wait..."
zig cc -target x86-linux-musl -static -O2 tests/test_fork_wait.c -o tests/linux/assets/test_fork_wait || exit 1
run_test "Fork/Wait" "tests/linux/assets/test_fork_wait" ""

echo "Building test_smc_linux..."
zig cc -target x86-linux-musl -static -O0 tests/linux/test_smc_linux.c -o tests/linux/assets/test_smc_linux || exit 1
run_test "SMC Logic" "tests/linux/assets/test_smc_linux" ""

echo ""
echo "=== Running C# Tests ==="
dotnet test linux-tests/Bifrost.Tests.csproj || exit 1

echo ""
echo ">>> ALL BIFROST TESTS PASSED! <<<"
echo ""
