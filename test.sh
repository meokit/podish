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
dotnet build Fiberish.Cli/Fiberish.Cli.csproj

# Copy dylib to output
cp build/bin/libfibercpu.dylib Fiberish.Cli/bin/Debug/net8.0/

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

echo "  - test_full_features"
$BUILD_CMD -o tests/linux/assets/test_full_features tests/linux/test_full_features.c

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
    
    # Define variables for clarity in the new logic
    local EMULATOR="dotnet run --project Fiberish.Cli/Fiberish.Cli.csproj --no-build --"
    local TEST_BIN="$bin"
    local TEST_ARGS="$args"
    local LOG_FILE="/tmp/emu_test_cs.log" # Using the original log file name
    
    # Run with timeout to prevent hangs
    # We use 'timeout' command if available, otherwise just run
    
    echo "Running $name ($TEST_BIN)..." # Changed $TEST_NAME to $name for consistency
    
    if command -v timeout >/dev/null 2>&1; then
        timeout 10s $EMULATOR $TEST_ARGS $TEST_BIN > "$LOG_FILE" 2>&1
        RET=$?
    else
        $EMULATOR $TEST_ARGS $TEST_BIN > "$LOG_FILE" 2>&1
        RET=$?
    fi
    
    if [ $RET -ne 0 ]; then
        echo "FAILED (Exit Code $RET. Check $LOG_FILE)"
        cat "$LOG_FILE"
        exit 1
    fi

    # Check for PASSED (optional, but good for confirmation)
    # The original script checked the dotnet run exit code directly.
    # This new logic adds a grep for "PASS" in the log, which might be redundant
    # if the C# app already returns 0 on success.
    # Assuming the C# app prints "PASS" on success.
    if grep -E -q "PASS|SUCCESS" "$LOG_FILE"; then
        echo "PASSED"
    else
        echo "FAILED (Output missing PASS/SUCCESS. Check $LOG_FILE)"
        cat "$LOG_FILE"
        exit 1
    fi
}


run_test "Hello World"  "tests/linux/assets/hello_static" ""
run_test "Linux Syscalls" "tests/linux/assets/syscall_test" "--rootfs tests/linux/assets"
run_test "Futex Sync"   "tests/linux/assets/test_futex" ""
run_test "Mutex Lock"   "tests/linux/assets/test_mutex" ""
run_test "Pthread Basic" "tests/linux/assets/test_pthread" ""
run_test "Full Features" "tests/linux/assets/test_full_features" "--rootfs tests/linux/assets"

# Build and run fork/wait test
echo "Building test_fork_wait..."
zig cc -target x86-linux-musl -static -O2 tests/test_fork_wait.c -o tests/linux/assets/test_fork_wait || exit 1
run_test "Fork/Wait" "tests/linux/assets/test_fork_wait" ""

echo "Building test_smc_linux..."
zig cc -target x86-linux-musl -static -O0 tests/linux/test_smc_linux.c -o tests/linux/assets/test_smc_linux || exit 1
run_test "SMC Logic" "tests/linux/assets/test_smc_linux" ""

echo "Building test_rdtsc..."
zig cc -target x86-linux-musl -static -O2 tests/linux/test_rdtsc.c -o tests/linux/assets/test_rdtsc || exit 1
run_test "RDTSC Insn" "tests/linux/assets/test_rdtsc" ""

echo ""
# echo ">>> Running Unit Tests..."
# dotnet test Fiberish.Tests/Fiberish.Tests.csproj --logger "console;verbosity=detailed" || exit 1

echo ""
echo ">>> ALL BIFROST TESTS PASSED! <<<"
echo ""
