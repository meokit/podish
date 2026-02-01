# x86emu & Bifrost

**x86emu** is a high-performance, non-JIT, user-mode x86 IA-32 emulation library written in C++23.
**Bifrost** is a .NET 8 runtime that wraps `x86emu` to provide a functional Linux kernel simulation layer, capable of running static x86 Linux binaries on macOS and Linux.

## Architecture

The project is divided into two layers:

1.  **Core (C++):** A standalone shared library (`libx86emu`) implementing the raw CPU execution logic.
    *   **Threaded Interpreter:** Uses `[[clang::musttail]]` for rapid instruction dispatch.
    *   **SoftMMU:** Software Memory Management Unit with permission tracking.
    *   **FPU/SSE:** Full 80-bit x87 FPU and SSE/SSE2 support.
    
2.  **Bifrost (C#):** A managed runtime that acts as the "kernel" and loader.
    *   **ELF Loader:** Parses and loads static x86 binaries.
    *   **Syscall Translation:** Implements Linux syscalls (Filesystem, Memory, threading) in managed code.
    *   **Process Management:** Maps Guest threads to Host threads/Tasks.

## Features

-   **High Performance:** Optimized interpreter loop approaching JIT speeds for interpretation.
-   **Cross-Platform:** Runs Linux x86 binaries on macOS (Apple Silicon/Intel) and Linux.
-   **Linux Emulation:**
    -   Support for static binaries (musl-libc compatible).
    -   File I/O with host path mapping.
    -   Multithreading support (`pthread_create`, `futex`).
    -   Signal handling (Partial).

## Requirements

-   **C++ Core:** Clang++ (supporting C++23), CMake 3.20+.
-   **Bifrost:** .NET 8 SDK.
-   **Testing:** Python 3.10+ (for core regression tests).

## Build Instructions

You can use the provided helper script to build everything and run tests:

```bash
./test.sh
```

Or build manually:

### 1. Build the Core Library

```bash
cmake -B build
cmake --build build -j
# On macOS, you may need to codesign the library locally
codesign -f -s - build/libx86emu.dylib
```

### 2. Build Bifrost

```bash
# Copy the native library to the output directory or ensure it's in LD_LIBRARY_PATH
dotnet build linux/Bifrost.csproj
cp build/libx86emu.dylib linux/bin/Debug/net8.0/  # macOS
# cp build/libx86emu.so linux/bin/Debug/net8.0/     # Linux
```

## Usage

To run a static Linux x86 binary using Bifrost:

```bash
dotnet run --project linux/Bifrost.csproj -- <binary_path> [arguments]
```

### Example

```bash
# Run a static hello world
dotnet run --project linux/Bifrost.csproj -- tests/linux/assets/hello_static

# Run with RootFS mapping
dotnet run --project linux/Bifrost.csproj --rootfs ./my_rootfs -- /bin/ls
```

## Directory Structure

-   `src/`: **Core C++ Library**. Instruction decoder, OPS, and execution engine.
-   `linux/`: **Bifrost (C#)**. Syscall implementations, ELF loader, and CLI entry point.
-   `tests/`:
    -   `regression/`: Python-based instruction verification against Unicorn/QEMU.
    -   `linux/`: C source files for integration tests.
-   `spec/`: Design specifications.

## License

MIT License