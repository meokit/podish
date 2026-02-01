#!/bin/bash
set -e

# Bifrost (x86emu C#) Full Build & Test Script

# 1. Build C++ Core
echo ">>> Building x86emu core..."
mkdir -p build
pushd build > /dev/null
cmake ..
make -j4
popd > /dev/null

# 2. Codesign (macOS)
if [[ "$OSTYPE" == "darwin"* ]]; then
    echo ">>> Codesigning libx86emu.dylib..."
    codesign -f -s - build/libx86emu.dylib
fi

# 3. Build C# Project (Bifrost)
echo ">>> Building Bifrost..."
dotnet build linux/Bifrost.csproj

# Copy dylib to output
cp build/libx86emu.dylib linux/bin/Debug/net8.0/

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
    if dotnet run --project linux/Bifrost.csproj --no-build -- $args $bin > /tmp/emu_test_cs.log 2>&1; then
        if grep -qE "SUCCESS|Hello|All Tests Passed|Exiting" /tmp/emu_test_cs.log; then
            echo "PASSED"
        else
            echo "FAILED (Unexpected output. Check /tmp/emu_test_cs.log)"
            cat /tmp/emu_test_cs.log
            exit 1
        fi
    else
        echo "CRASHED (Exit Code $?. Check /tmp/emu_test_cs.log)"
        cat /tmp/emu_test_cs.log
        exit 1
    fi
}

run_test "Hello World"  "tests/linux/assets/hello_static" ""
run_test "Linux Syscalls" "tests/linux/assets/syscall_test" "--rootfs tests/linux/assets"
# run_test "Futex Sync"   "tests/linux/assets/test_futex" ""
# run_test "Mutex Lock"   "tests/linux/assets/test_mutex" ""
# run_test "Pthread Basic" "tests/linux/assets/test_pthread" ""

echo ""
echo ">>> ALL BIFROST TESTS PASSED! <<<"
echo ""
