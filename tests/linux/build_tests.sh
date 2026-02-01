# Use zig cc to cross-compile for x86 Linux (musl)
# Clear cache if needed
rm -rf $HOME/.cache/zig
mkdir -p build/zig_cache
export ZIG_GLOBAL_CACHE_DIR=$(pwd)/build/zig_cache
export ZIG_LOCAL_CACHE_DIR=$(pwd)/build/zig_cache

echo "Building syscall_test.c..."
zig cc -target x86-linux-musl -o syscall_test syscall_test.c
echo "Build complete: syscall_test"
