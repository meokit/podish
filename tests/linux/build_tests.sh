# Use zig cc to cross-compile for x86 Linux (musl)
# Clear cache if needed
rm -rf $HOME/.cache/zig
mkdir -p build/zig_cache
export ZIG_GLOBAL_CACHE_DIR=$(pwd)/build/zig_cache
export ZIG_LOCAL_CACHE_DIR=$(pwd)/build/zig_cache


SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &> /dev/null && pwd)
mkdir -p "$SCRIPT_DIR/assets"

echo "Building syscall_test.c..."
zig cc -target x86-linux-musl -static -o "$SCRIPT_DIR/assets/syscall_test" "$SCRIPT_DIR/syscall_test.c"
echo "Build complete: $SCRIPT_DIR/assets/syscall_test"
