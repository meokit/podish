# Podish

> A high-performance Linux x86 userland container for iOS and Apple Silicon.

**Podish** runs unmodified 32-bit Linux binaries on modern Apple platforms without JIT compilation. It is built around a from-scratch C++ interpreter core and a C# Linux compatibility layer, optimized specifically for environments where code generation is restricted.

If you have used iSH, the goal is similar, but the code is entirely original and significantly faster on compute-heavy workloads.

A browser demo is available at [podish.meokit.com](https://podish.meokit.com) (performance is lower than native; networking is not yet enabled).

An AltStore source is available at [meokit/podish-altstore](https://github.com/meokit/podish-altstore) for easy sideloading on iOS.

---

## What It Is

Podish is not a full-system VM like UTM. It is a **userland container** that emulates an x86 CPU and enough of the Linux kernel to run real-world software: shells, scripting languages, build toolchains, and even some JIT-capable guests.

The interpreter core uses a pre-decoding + direct-threaded dispatch design inspired by [LuaJIT Remake](https://github.com/luajit-remake/luajit-remake). It leverages modern Clang ABI features (`preserve_none`, `[[musttail]]`) to keep dispatch overhead near hand-written-assembly levels without writing a line of assembly.

The result is an interpreter that outperforms QEMU's TCI (Tiny Code Interpreter) by an order of magnitude and approaches the speed of Wasm3 on pure compute benchmarks.

---

## Why Podish

iOS prohibits JIT by blocking `PROT_EXEC | PROT_WRITE` mappings and unsigned code execution. This rules out LuaJIT's JIT mode, fast UTM, and any engine that generates machine code at runtime.

The natural question: **how fast can an interpreter get?**

Podish answers this with a stack of CPU-emulation techniques usually found in JIT research:

- **Pre-decoded IR** with tail-call threading to eliminate the classic decode-dispatch loop
- **MicroTLB + SoftTLB** to minimize address-translation overhead on every memory access
- **Block linking** to reduce control-flow exit costs between basic blocks
- **Profile-guided superopcodes** (≈256 fused instruction pairs) to eliminate redundant internal state updates
- **Parity-only lazy flags** — a pragmatic compromise that avoids the memory traffic of full lazy EFLAGS
- **SMC-aware MMU** that invalidates cached code blocks precisely when guest JITs (LuaJIT, V8) patch executable pages

The outcome, measured on CoreMark 1.0:

| Hardware | Podish | iSH | QEMU TCI | Native (arm64) |
| --- | ---: | ---: | ---: | ---: |
| iPhone 17 (A19) | **~3,447** | ~1,692 | — | — |
| MacBook Pro (M3 Max) | **~2,967** | — | ~325 | ~38,087 |

Podish is roughly **2× iSH** and roughly **9× QEMU TCI** on the same class of workload, while remaining a pure interpreter that does not generate host machine code.

---

## What Runs Today

| Category | Representative Software | Status | Notes |
| --- | --- | --- | --- |
| Shell / base userland | `busybox`, `ash` | Stable | Daily interactive use, including `vim` |
| Full shell | `bash` | Stable | Used in browser rootfs and local testing |
| Scripting runtimes | `python3` | Stable | Benchmarked; large projects untested |
| JIT-capable guests | `LuaJIT` (`-joff`) | Stable | SMC handling validated; forced LuaJIT interpreter mode |
| Build toolchain | `gcc`, `make` | Verified | CoreMark tree compiles end-to-end |
| Networking tools | `git`, `OpenSSH` | Verified | `git clone` works; validates VFS / DNS / TLS paths |
| Heavy modern runtime | `Node.js` | Starts | Usable for simple scripts; V8 JIT can trigger occasional crashes. Disabling V8 JIT stabilizes it at a steep performance cost. |

**Experimental / in progress:**

- **Wayland bridge** (`Podish.Wayland`): forwards guest Wayland clients to host SDL windows. `foot` terminal works; SDL2-based apps require EGL/GBM paths not yet implemented.
- **PulseAudio bridge** (`Podish.Pulse`): redirects guest audio to host backends. `ffplay -nodisp` plays audio; general desktop audio is rough.

---

## Architecture

```text
Guest i686 Linux Binary
  -> Predecode / Block Builder
  -> Interpreter Dispatch + Semantics
  -> MMU / MicroTLB / SoftTLB
  -> Linux Runtime / Syscall Layer (C#)
  -> VFS / Process / Signal / Network
  -> Host OS (macOS / iOS) or Browser (Wasm)
```

### Key Components

| Component | Language | Role |
| --- | --- | --- |
| `libfibercpu` | C++ | IA-32 interpreter core: decode, dispatch, MMU, block cache |
| `Fiberish.Core` | C# | Linux runtime: syscalls, VFS, process model, signals, PTY |
| `Fiberish.Netstack` | Rust | Native TCP/IP stack based on [smoltcp](https://github.com/smoltcp-rs/smoltcp) |
| `Podish.Core` | C# | Container orchestration, OCI image handling, runtime lifecycle |
| `Podish.Cli` | C# | Primary user-facing CLI (`run`, `pull`, `ps`, `logs`, …) |
| `PodishApp` | SwiftUI | Optional iOS/macOS GUI (wraps `Podish.Core.Native`) |

### C++ Core + C# Runtime: How the Boundary Works

The interpreter core is C++ for raw performance; the Linux layer is C# for rapid development and portability. The boundary is intentionally thin:

- **C-only API surface**: `libfibercpu` exposes plain C functions (`X86_Create`, `X86_Run`, `X86_RegRead`, …). C# calls them via `LibraryImport` / `DllImport` with blittable types.
- **Zero-copy guest memory**: `X86_ResolvePtrForRead/Write` returns host pointers directly; C# operates over them via `Span<byte>` without per-byte P/Invoke.
- **Pinned callbacks**: C++-to-C# callbacks (faults, interrupts, logs) use `GCHandle.Alloc(this)` as `userdata`, preventing GC movement across the boundary.
- **Syscall batching**: The interpreter runs hundreds to thousands of guest instructions in C++ before crossing into C# for a syscall. The cross-language cost is workload-dependent, not instruction-dependent.

Real-world profiling shows the boundary overhead stays **below 1%** on compute-intensive workloads; on I/O-heavy workloads the bottleneck is network and VFS, not P/Invoke.

### Concurrency Model

Podish implements Linux `clone`/`fork`/`vfork` and basic `pthread` semantics, but it is **not** an SMP emulator. A single host scheduler thread serializes all guest tasks. Each guest thread gets its own interpreter `Engine` (and private `EmuState`), so the core itself needs no locks. Shared address spaces use a reference-counted `MmuCore` with a lightweight `RuntimeTlbShootdownRing` to keep per-engine TLBs coherent.

This is sufficient for shells, Python, compilers, and `git`. It is not suitable for heavily parallel scientific computing.

---

## Quick Start

### Requirements

- .NET SDK 10+
- CMake 3.20+
- A C/C++ toolchain
- Rust toolchain with Cargo

Optional:

- Python 3.10+, `pytest`, `pexpect` (Python tests)
- `zig` (integration test assets)
- `podman` (image/rootfs prep)
- Xcode / Swift toolchain (for the SwiftUI app)

Verified versions on the current snapshot:

- `.NET SDK 10.0.103`
- `cargo 1.85.0`
- `Python 3.12.7`
- `cmake 3.29.6`

### Build

```bash
dotnet build Podish.slnx -c Debug
```

This automatically triggers:

- CMake build of `libfibercpu` (via `Fiberish.X86`)
- Cargo build of `Fiberish.Netstack` (via `Fiberish.Core`)

### Run

Show help:

```bash
dotnet run --project Podish.Cli/Podish.Cli.csproj -- --help
```

Pull an image and run a command:

```bash
dotnet run --project Podish.Cli/Podish.Cli.csproj -- pull docker.io/i386/alpine:latest
dotnet run --project Podish.Cli/Podish.Cli.csproj -- run docker.io/i386/alpine:latest /bin/uname -a
```

Run from a local rootfs:

```bash
dotnet run --project Podish.Cli/Podish.Cli.csproj -- run --rootfs /path/to/rootfs /bin/sh
```

Useful runtime flags:

- `-i`, `-t` — interactive TTY session
- `-v /host:/guest` — bind mount
- `--network private` + `-p hostPort:containerPort` — private networking with port publishing
- `-s` — syscall tracing
- `--init` — engine-managed PID 1 reaper

### Test

Managed tests:

```bash
dotnet test Fiberish.Tests/Fiberish.Tests.csproj --no-build
dotnet test Fiberish.SilkFS.Tests/Fiberish.SilkFS.Tests.csproj --no-build
```

Python emulator / regression tests:

```bash
pytest
```

Integration tests:

```bash
cmake -S tests/integration -B build/integration-assets -DFIBERISH_PROJECT_ROOT=$PWD
cmake --build build/integration-assets --target integration-tests-build
pytest -m integration tests/integration
```

---

## Performance Notes

### From 600 to 3,000 CoreMark

Early builds scored ~600 on CoreMark, roughly on par with a naive interpreter. The climb to ~3,000 was driven by a series of targeted fixes rather than any single breakthrough:

1. **Fix TLB refill bug** (~600 → ~800): confirmed address translation was the first real bottleneck.
2. **Hot-path tuning** (~800 → ~1,500): removed redundant per-instruction memory writes; specialized handler templates.
3. **Memory layout / paired loads** (~1,500 → ~2,000): shifted optimization focus from dispatch to data organization.
4. **Lazy parity flags** (~2,000 → ~2,200): static def-use pruning + lazy PF evaluation.
5. **Block linking** (~2,200 → ~2,500): appended short successor blocks to reduce boundary costs.
6. **Profile-guided superopcodes** (~2,500 → ~3,000): ~256 fused instruction pairs based on anchor instructions and RAW dependencies.

### Why A19 Can Beat M3 Max

Podish is single-threaded and latency-bound; it does not benefit from the M3 Max's massive memory bandwidth. On small working sets like CoreMark, the A19's newer core microarchitecture gives it a slight edge despite running in a phone thermal envelope.

### Copy-and-Patch: A Tried-and-Failed Experiment

A copy-and-patch JIT was prototyped using the same Clang ABI machinery (`preserve_none`, `[[musttail]]`). The hope was 200%+ speedup; the reality was a small regression.

The reason: direct-threaded dispatch was already so cheap that the real bottlenecks were memory access, address translation, and instruction-cache pressure. Replacing dispatch with larger patched stencils increased I-cache pressure without reducing the costly work. This confirmed that **a well-tuned interpreter can outperform a naive baseline JIT** when the bottleneck has shifted away from dispatch.

---

## Repository Layout

- `Podish.Cli/` — main CLI entrypoint
- `Podish.Core/` — container runtime and image handling
- `Podish.Core.Native/` — NativeAOT / static packaging for Apple platforms
- `PodishApp/` — SwiftUI frontend
- `Fiberish.Core/` — Linux emulation layer and runtime services
- `Fiberish.X86/` — .NET wrapper that builds and ships `libfibercpu`
- `Fiberish.Netstack/` — Rust networking component
- `Fiberish.SilkFS/` — SQLite-backed filesystem storage
- `libfibercpu/` — C++ emulator core (CMake)
- `Fiberish.Tests/` — primary managed test suite
- `Fiberish.SilkFS.Tests/` — SilkFS tests
- `tests/` — Python tests, regression generation, integration harnesses
- `benchmark/` — performance tools and benchmark runners
- `docs/` — technical deep-dives (including the original Chinese technique report)

---

## License

Dual-licensed under **GPLv3** (open-source / non-commercial track) and a **commercial license** (proprietary distribution).

- Non-commercial use: freely available under GNU GPLv3.
- Commercial use: contact `giantneko@icloud.com`.

See the `LICENSE` file for full terms.

---

**Questions, bugs, or software you want to see running?** Reach out at `giantneko@icloud.com`.
