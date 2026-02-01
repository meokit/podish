#!/bin/bash
set -e

# x86emu Full Build & Test Script
# This script builds the core, loader, and all Linux test cases, then runs them.

# 1. Build C++ Core
echo ">>> Building x86emu core..."
mkdir -p build
pushd build > /dev/null
cmake ..
make -j4
cp libx86emu.dylib ../linux/
popd > /dev/null

# 2. Codesign (needed for macOS ARM/Intel security)
echo ">>> Codesigning libx86emu.dylib..."
codesign -f -s - linux/libx86emu.dylib

# 3. Build Go Loader
echo ">>> Building x86loader..."
pushd linux > /dev/null
export CGO_LDFLAGS_ALLOW="-Wl,-rpath,@loader_path"
go build -v -o x86loader .
popd > /dev/null

echo ">>> Codesigning x86loader..."
codesign -f -s - linux/x86loader

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

# Helper to run and verify
run_test() {
    local name=$1
    local bin=$2
    local args=$3
    echo -n "Test $name: "
    # Run loader. Redirect output but keep it for error reporting.
    if ./linux/x86loader $args $bin > /tmp/emu_test.log 2>&1; then
        # Check for success indicators
        if grep -qE "SUCCESS|Hello|All Tests Passed|Exiting" /tmp/emu_test.log; then
            echo "PASSED"
        else
            echo "FAILED (Unexpected output. Check /tmp/emu_test.log)"
            cat /tmp/emu_test.log
            exit 1
        fi
    else
        echo "CRASHED (Exit Code $?. Check /tmp/emu_test.log)"
        cat /tmp/emu_test.log
        exit 1
    fi
}

run_test "Hello World"  "tests/linux/assets/hello_static" ""
run_test "Linux Syscalls" "tests/linux/assets/syscall_test" "--rootfs tests/linux/assets"
run_test "Futex Sync"   "tests/linux/assets/test_futex" ""
run_test "Mutex Lock"   "tests/linux/assets/test_mutex" ""
run_test "Pthread Basic" "tests/linux/assets/test_pthread" ""

echo ""
echo ">>> ALL TESTS PASSED! <<<"
echo ""
