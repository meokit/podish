# x86emu

`x86emu` is a mixed-language workspace for running Linux x86 userland on modern hosts.

At a high level:

- `Fiberish` is the Linux runtime / kernel-simulation layer.
- `Podish` is the container-oriented layer built on top of `Fiberish`.

Today the repository is centered around these pieces:

- `libfibercpu`: a C++ x86 IA-32 emulator built with CMake.
- `Fiberish.Core`: the managed Linux runtime and kernel compatibility layer.
- `Fiberish.Netstack`: a Rust native networking component built with Cargo.
- `Podish.Core`: the container/runtime orchestration layer on top of `Fiberish`.
- `Podish.Cli`: the main container-facing CLI, with `run`, `pull`, `ps`, `logs`, `events`, image import/export, and container lifecycle commands.
- `Podish`: an optional SwiftUI frontend for the `Podish` layer.

The old `Fiberish.App`-style entrypoint no longer exists. For container-style day-to-day use, the current entrypoint is `Podish.Cli`.

## What Works Today

Based on the current codebase and tests, the repository already covers both the runtime core and the container layer:

- Linux-like process/runtime behavior including `fork`, `vfork`, `clone`, `execve`, and `wait*`.
- Filesystem features including `tmpfs`, `procfs`, host mounts, and overlay-based roots.
- PTY / TTY support.
- Sockets and a native netstack integration.
- Container-style execution from either OCI images or `--rootfs`.
- OCI image pull/save/load/import/export flows.
- Large managed test coverage for syscalls, VFS, memory, networking, and container runtime behavior.

## Requirements

Core development:

- .NET SDK 10+
- CMake 3.20+
- A C/C++ toolchain capable of building `libfibercpu`
- Rust toolchain with Cargo

Optional, depending on what you want to do:

- Python 3.10+ for the Python test and benchmark tooling
- `pytest` and `pexpect` for integration/regression tests
- `zig` for building some integration test assets
- `podman` for image/rootfs preparation and some end-to-end scenarios
- Xcode / Swift toolchain for the `Podish` app and XCFramework build

On this repo snapshot, the workspace builds successfully with:

- `.NET SDK 10.0.103`
- `cargo 1.85.0`
- `Python 3.12.7`
- `cmake 3.29.6`

## Quick Start

Build the full solution:

```bash
dotnet build Fiberish.sln -c Debug
```

This build already triggers the native sub-builds:

- `Fiberish.X86` builds `libfibercpu` with CMake
- `Fiberish.Core` builds `Fiberish.Netstack` with Cargo

Show CLI help:

```bash
dotnet run --project Podish.Cli/Podish.Cli.csproj -- --help
```

Pull an image:

```bash
dotnet run --project Podish.Cli/Podish.Cli.csproj -- pull docker.io/i386/alpine:latest
```

Run a command from an image:

```bash
dotnet run --project Podish.Cli/Podish.Cli.csproj -- run docker.io/i386/alpine:latest /bin/uname -a
```

Run against a local rootfs instead of pulling an image:

```bash
dotnet run --project Podish.Cli/Podish.Cli.csproj -- run --rootfs /path/to/rootfs /bin/sh
```

Useful runtime options:

- `-i`, `-t` for interactive TTY sessions
- `-v /host:/guest` for bind mounts
- `--network private` and `-p hostPort:containerPort` for private networking and port publishing
- `-s` for syscall tracing
- `--init` for an engine-managed PID 1 reaper

## Main Commands

The current CLI exposes these top-level commands:

- `run`
- `start`
- `ps`
- `rm`
- `rename`
- `images`
- `image rm`
- `pull`
- `save`
- `load`
- `import`
- `export`
- `logs`
- `events`

See the live help for details:

```bash
dotnet run --project Podish.Cli/Podish.Cli.csproj -- run --help
```

## Testing

Managed tests:

```bash
dotnet test Fiberish.Tests/Fiberish.Tests.csproj --no-build
dotnet test Fiberish.SilkFS.Tests/Fiberish.SilkFS.Tests.csproj --no-build
```

Python emulator/regression tests:

```bash
pytest
```

Integration tests:

```bash
cmake -S tests/integration -B build/integration-assets -DFIBERISH_PROJECT_ROOT=$PWD
cmake --build build/integration-assets --target integration-tests-build
pytest -m integration tests/integration
```

Helpful references:

- `tests/README.md`
- `tests/integration/README.md`
- `benchmark/podish_perf/README.md`

## SwiftUI Frontend

The `Podish/` directory contains a SwiftUI app that consumes a native XCFramework built from `Podish.Core.Native`.

Build the Apple XCFramework:

```bash
bash Podish.Core.Native/scripts/publish-static.sh
```

Then open or build the Swift package in `Podish/`.

## Repository Layout

- `Podish.Cli/`: main CLI entrypoint
- `Podish.Core/`: container runtime orchestration and image handling
- `Podish.Core.Native/`: NativeAOT/static packaging for Apple integration
- `Podish/`: SwiftUI frontend
- `Fiberish.Core/`: Linux emulation layer and runtime services
- `Fiberish.X86/`: .NET wrapper that builds and ships `libfibercpu`
- `Fiberish.Netstack/`: Rust networking component
- `Fiberish.SilkFS/`: SQLite-backed filesystem storage pieces
- `libfibercpu/`: C++ emulator core
- `Fiberish.Tests/`: main managed test suite
- `Fiberish.SilkFS.Tests/`: SilkFS tests
- `tests/`: Python tests, regression generation, and integration harnesses
- `benchmark/`: performance tools and benchmark runners

## License

MIT License
