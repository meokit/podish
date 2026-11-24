# Podish

Podish runs unmodified 32-bit x86 Linux programs through a C++ CPU interpreter
and a C# Linux compatibility layer. Use the CLI on macOS, Linux, or Windows,
the SwiftUI app on iOS and macOS, or the [browser demo](https://podish.meokit.com).
For iOS sideloading, use the [AltStore feed](https://github.com/meokit/podish-altstore).

Podish emulates the CPU and implements Linux syscalls, processes, signals,
filesystems, and networking in userspace. Give it an IA-32 OCI image or a local
root filesystem to run a shell, compiler, or application.

## Quick Start

### Requirements

- .NET 10 SDK. [global.json](global.json) sets the SDK floor and roll-forward policy.
- CMake 3.20 or newer, Python 3.10 or newer, and a Clang toolchain with C++23 support.
- Rust and Cargo for the native network stack.

On macOS, use the Xcode command-line tools. The Linux CI build uses Clang 20
with libc++ and libc++abi. On Windows x64, install the Visual Studio C++
toolchain with LLVM (`clang-cl`) and CMake tools (Ninja). See the package lists
and commands in the [CI workflow](.github/workflows/ci.yml).

### Build

On macOS and Linux, build the CLI from the repository root.

```bash
dotnet build Podish.Cli/Podish.Cli.csproj -c Debug
```

On Windows x64, specify the runtime identifier and build one project at a time.

```powershell
dotnet build Podish.Cli/Podish.Cli.csproj -c Debug -r win-x64 -m:1
```

The CLI build calls CMake through `Fiberish.X86` and Cargo through
`Fiberish.Core`. The default baking preset is `3.5m`. Set
`-p:FibercpuBakingPreset=1.75m` or `-p:FibercpuBakingPreset=10m` to select
a different set of instruction tables. The names are size hints calibrated
on macOS ARM64. See [native build presets](docs/build-presets.md).

For the full solution, follow the CI workflow's macOS job.
It builds the browser project alongside the native projects and installs the
`wasm-tools` workload. The [browser build notes](Podish.Browser/README.md)
cover Emscripten setup. The [Swift app roadmap](docs/podish-swift-app-roadmap.md)
lists the Apple build commands.

### Run

Run an IA-32 image. The CLI pulls the image if it is missing from the local store.

```bash
dotnet run --project Podish.Cli/Podish.Cli.csproj -- --help
dotnet run --project Podish.Cli/Podish.Cli.csproj -- run --rm docker.io/i386/alpine:latest /bin/uname -m
```

Use `--rootfs` to open a shell in a local IA-32 root filesystem.

```bash
dotnet run --project Podish.Cli/Podish.Cli.csproj -- run --rm -i -t --rootfs /path/to/rootfs /bin/sh
```

| Option | Behavior |
| --- | --- |
| `-i -t` | Keep standard input open and allocate a guest TTY. |
| `-v /host:/guest` | Bind-mount a host path in the guest. |
| `--network private -p hostPort:containerPort` | Use private networking and publish a port to the host. |
| `-s` | Log guest syscalls. |
| `--init` | Start an engine-managed PID 1 reaper. |
| `--fbdev --fbdev-size 800x600` | Open a host window for the guest framebuffer and forward input. [Framebuffer and X11 examples](tests/fbdev-games/README.md) |

### Test

Run the managed suites and the isolated CPU tests.

```bash
dotnet test Fiberish.Tests/Fiberish.Tests.csproj
dotnet test Fiberish.SilkFS.Tests/Fiberish.SilkFS.Tests.csproj
dotnet test Fiberish.X86.Tests/Fiberish.X86.Tests.csproj -c Release --filter "Category!=Exhaustive"
```

The CPU suite calls `libfibercpu` and covers arithmetic boundaries, SIMD,
addressing, and pinned FEX regressions. See the commands for exhaustive tests
and the superopcode build variants in the
[CPU test guide](docs/cpu-instruction-test-strategy.md).

The Linux syscall integration suite uses a pinned [LTP](https://github.com/linux-test-project/ltp)
submodule and selected static i386 binaries. Build the assets on Linux or WSL.
Run the native reference on a Linux host that supports i386 executables.

```bash
git submodule update --init tests/ltp/upstream
bash tests/ltp/build.sh
python3 tests/ltp/run.py --backend native
python3 tests/ltp/run.py --backend podish
```

The [LTP guide](tests/ltp/README.md) covers prerequisites, Windows and WSL
commands, and compatibility reports.

## Tested software

| Workload | Test or reported use |
| --- | --- |
| `busybox`, `ash`, `bash`, `vim` | Shell sessions and command-line editing. The browser root filesystem includes Bash. |
| Python 3 | Benchmark scripts. |
| LuaJIT | Interpreter mode with `-joff`. |
| `gcc`, `make` | Build and run CoreMark in the guest. |
| Git, OpenSSH | Network clients and Git clones. |
| Node.js | Node 14 runs the allocator regression and Webpack workloads. [Node regression harness](tests/node-crash/README.md) |
| PrBoom, SuperTux | SDL 1.2 framebuffer output and PulseAudio playback. [Framebuffer game tests](tests/fbdev-games/README.md) |
| Chocolate Doom, SuperTux | ANGLE-backed GLES on macOS with Metal. [Graphics setup](graphics/README.md) |
| SuperTuxKart 1.4 (GLES2) | ANGLE with a virtual KMS/GBM device on Windows and Linux/WSL2. [Media smoke tests](tests/media/README.md#supertuxkart) |
| X.Org, Openbox, xeyes, xclock, xterm | Guest X11 with a framebuffer displayed in a host SDL2 window. [X11 setup](tests/fbdev-games/README.md) |

The Wayland bridge handles shared-memory and ANGLE-backed EGL clients.
The PulseAudio bridge forwards guest audio to host backends.
[Graphics setup](graphics/README.md) · [Media smoke tests](tests/media/README.md)

## Architecture

```text
IA-32 Linux executable
  -> libfibercpu                 decode, dispatch, and address translation
  -> Fiberish.Core               syscalls, VFS, scheduler, and signals
  -> Host OS or WebAssembly      platform I/O
```

### Interpreter

`libfibercpu` predecodes guest instructions into basic blocks and dispatches
their handlers with tail calls, a design inspired by [LuaJIT Remake](https://github.com/luajit-remake/luajit-remake).
Clang's `preserve_none` and `musttail`
attributes support the threaded handler ABI on compatible native builds.
The micro-TLB and software TLB cache translations. Block linking reuses a
resolved successor, and profile-baked superopcodes execute instruction
sequences in one handler. The MMU invalidates cached blocks when a guest
write changes their code pages.
[Superopcodes](docs/superopcode-design.md) · [MMU lifecycle](docs/mmu-lifecycle-model.md)

The default build runs the interpreter without a host JIT. The optional LLVM
block JIT requires native macOS arm64 with upstream Clang. Guest code
generation, such as V8 writing IA-32 machine code, operates on guest memory
and uses the MMU's code invalidation path.
[Instruction specialization and LLVM JIT](docs/instruction-specialization.txt)

Production builds on ARM64 enable guest GPR pinning when Clang passes the
handler ABI probe. The default leaves pinning off on x86-64 and WebAssembly.
To try pinning on a compatible x86-64 build, pass `-p:GprPin=ON` to
`dotnet build` or `-DFIBERCPU_GPR_PIN=ON` to CMake.

### Linux runtime

C# calls the [native API](libfibercpu/src/bindings.h) through P/Invoke.
`X86_Run` executes a guest slice, and fault, interrupt, and log callbacks
carry a `GCHandle` token back to the managed engine. For guest memory
copies, C# resolves a host pointer for each page-sized span, then copies
the bytes without crossing the boundary for each byte.

Each container runtime owns a scheduler. One host thread executes its
guest tasks in slices. Each guest task has its own `Engine` and native
`EmuState`. Tasks with a shared address space share an `MmuCore`, which
records translation invalidations in a shootdown ring for their local TLBs.
[Scheduling and signals](docs/scheduling-and-signals.md) · [Memory management](docs/memory-management.md)

## Performance Notes

Use the [emulator comparison harness](benchmark/emulator-comparison/README.md)
to run Podish, QEMU Usermode, QEMU system emulation, and iSH with one shared
i386 root filesystem.

### CoreMark 1.0

We used one Debian i386 root filesystem and one CoreMark ELF across all
backends. We built the ELF with Alpine GCC 15.2.0, musl, and
`-O3 -static -fno-pie -no-pie`. Each score is the median of three validated
190,000-iteration runs, in iterations per second. We pinned each Linux Docker
run to one logical CPU and ran the iSH CLI on native arm64 macOS.

We retested Podish at `b30f252e` with the default `3.5m` preset. On macOS, we
used one build for both Podish modes and ran LLVM JIT in `exact` mode with two
workers. The QEMU and iSH scores come from the previous pass with the same
guest fixture and run protocol.

| Environment | Podish interpreter | Podish LLVM JIT | QEMU Usermode TCG | QEMU system TCG | iSH |
| --- | ---: | ---: | ---: | ---: | ---: |
| M5 Max / native macOS | 4,508.9 | 8,815.9 | — | — | 2,036.4 |
| M5 Max / ARM64 Linux Docker | 4,542.4 | — | 12,939.3 | 4,262.3 | — |
| Ryzen AI Max+ 395 / x86-64 Linux Docker | 3,542.5 | — | 16,712.1 | 9,655.5 | — |

The [early public results](benchmark/emulator-comparison/README.md#early-public-results)
document earlier builds and test setups.

### CoreMark interpreter specialization

We profile GNU utilities and a Node.js minification task. We use block
execution counts to rank operand forms and short instruction sequences, then
time candidate tables within code-size budgets. We ship `3.5m` for general
terminal workloads and check performance on a separate set of applications.
[Baking strategy](docs/baked-instruction-variants.md)

Two compilers can choose different registers, addressing forms, and
instruction sequences for the same C source. We measured both CoreMark ELFs
against the general-purpose preset and against tables tuned for each ELF.

We used an M5 Max, one Debian rootfs, and two i386 ELFs from the same
CoreMark source revision. Both ELFs use `-O3` and static linkage. We kept
the same register pinning and compiled out Host JIT. Each score is the
median of three validated 100,000-iteration runs, in iterations per second.
We retested `3.5m` at `b30f252e` and kept the no-bake and ELF-specific scores
from `37cea465`.

| Interpreter configuration | GCC 12.2 / glibc | GCC 15.2 / musl |
| --- | ---: | ---: |
| No specialization or fusion | 1,855.4 | 1,969.7 |
| Default `3.5m` | 4,208.6 | 4,767.1 |
| Trained on the GCC 12.2 ELF | **7,542.0** | 3,557.2 |
| Trained on the GCC 15.2 ELF | 3,496.3 | **8,397.7** |

Each ELF-specific table scored highest on the ELF we used to train it. On the
other ELF, the same table scored below `3.5m`.
[Build settings, ELF hashes, and profile coverage](benchmark/emulator-comparison/README.md#coremark-interpreter-specialization)

## Repository Layout

| Path | Contents |
| --- | --- |
| `Podish.Cli/` | CLI commands and options |
| `Podish.Core/` | Containers and OCI image storage |
| `Podish.Core.Native/` | NativeAOT C ABI for the Apple app |
| `PodishApp/` / `Podish.Browser/` | SwiftUI app and WebAssembly browser demo |
| `Fiberish.Core/` | Linux syscalls, virtual filesystem, scheduler, signals, and TTY |
| `Fiberish.X86/` / `libfibercpu/` | Managed bindings and C++ CPU core |
| `Fiberish.Netstack/` | Rust TCP/IP stack based on [smoltcp](https://github.com/smoltcp-rs/smoltcp) |
| `Fiberish.SilkFS/` | SQLite-backed filesystem |
| `Fiberish.Tests/`, `Fiberish.SilkFS.Tests/`, `Fiberish.X86.Tests/` | Managed and CPU test suites |
| `tests/` / `benchmark/` | Integration fixtures, probes, and performance tools |
| `docs/` | Design and implementation notes |

## License

[LICENSE.md](LICENSE.md) describes the GPLv3 and commercial license terms.
For commercial licensing, contact [giantneko@icloud.com](mailto:giantneko@icloud.com).
