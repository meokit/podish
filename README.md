# x86emu

A high-performance, non-JIT, user-mode x86 IA-32 simulator written in C++23.

## Overview

x86emu is designed to execute 32-bit x86 binaries (via object files or raw code) with a focus on speed and simplicity. It uses a **Threaded Interpreter** model with **Pre-decoded Basic Block Caching** to achieve high throughput without the complexity of a JIT compiler.

## Features

-   **High Performance:** Uses `[[clang::musttail]]` and `preserve_none` calling convention for efficient instruction dispatch.
-   **No Dependencies:** The core runtime depends only on the C++ Standard Library.
-   **SoftMMU:** Implements a software Memory Management Unit with:
    -   2-level page tables.
    -   Permission tracking (Read/Write/Exec).
    -   Memory hooks for tracing and instrumentation.
-   **Instruction Set Support:**
    -   Core integer instructions.
    -   **FPU (x87):** Full 80-bit extended precision floating point support.
    -   **SSE/SSE2:** vector instruction support.
-   **Cross-Platform:** Targets macOS and Linux (requires Clang).

## Linux Loader & Busybox Support

The project includes a Go-based ELF loader (`x86loader`) capable of running static Linux binaries.
-   **Syscalls:** Implements core syscalls for file I/O, memory management (`brk`, `mmap`), and process info.
-   **Busybox:** Successfully runs simple Busybox applets (`true`, `echo`, `ls`).
-   **Modular:** Syscall layer is refactored into modular handlers (`fs`, `mem`, `proc`) with table-based dispatch.

## Requirements

-   **Compiler:** Clang++ (supporting C++23).
-   **Build System:** CMake 3.20+.
-   **Python:** 3.10+ (for running regression tests).
-   **Unicorn Engine:** (Optional, for running comparison tests).

## Build Instructions

```bash
cmake -B build
cmake --build build -j
```

## Usage

The simulator is currently designed to be embedded or run as a test harness. The `main` executable demonstrates a simple usage pattern:

```bash
./src/x86emu
```

This will initialize the VM, map some memory, and attempt to execute a hardcoded test sequence (see `src/main.cpp`).

For more advanced usage, look at `tests/framework.cpp` which sets up a comprehensive environment for comparing execution against the Unicorn Engine.

## Testing

The project includes a regression test suite that compares x86emu's execution state against Unicorn/QEMU.

To run the tests:

```bash
# Install dependencies
pip install unicorn qemu

# Run the runner
python3 tests/runner.py
```

## Directory Structure

-   `src/`: Core simulator source code.
    -   `decoder.*`: Instruction decoder.
    -   `ops/`: Instruction implementations.
    -   `mmu.h`: Memory management.
-   `spec/`: Design specifications and documentation.
-   `tests/`: Regression tests and framework.
-   `analyze/`: Tools for offline instruction analysis.

## License

MIT License
