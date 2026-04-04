# Podish Browser

A .NET-based x86 emulation and container orchestration platform for the web, powered by WebAssembly.

## Overview

`Podish Browser` provides a managed Linux runtime compatibility layer and x86 emulation environment that runs entirely within the browser. It combines a high-performance C++ emulation core with a .NET-managed orchestration layer and a React-based terminal interface.

## Projects

* **Podish.Browser**: The WebAssembly host application featuring a React and xterm.js frontend.
* **Podish.Core**: Managed runtime and container orchestration engine.
* **Fiberish.Core**: Managed Linux runtime compatibility layer.
* **Fiberish.X86**: .NET wrapper and build integration for `libfibercpu`.
* **Fiberish.SilkFS**: SQLite-backed storage provider for the managed runtime.
* **libfibercpu**: The core x86 emulator engine (C++/CMake).

## Prerequisites

* **.NET SDK 10+**
* **CMake 3.20+**
* **Ninja**
* **Node.js / npm**
* **Emscripten Toolchain** (must include a valid `Emscripten.cmake`)

## Build Instructions

### Build Solution
To build the managed solution in Release mode:
```bash
dotnet build Podish.slnx -c Release
```

### Publish Browser Application
To publish the WebAssembly host:
```bash
dotnet publish Podish.Browser/Podish.Browser.csproj -c Release
```

If your Emscripten toolchain is in a non-standard location, specify the path explicitly:
```bash
dotnet publish Podish.Browser/Podish.Browser.csproj -c Release -p:EmscriptenToolchainFile=/path/to/Emscripten.cmake
```

## Technical Notes

* **Frontend**: Located in `Podish.Browser/frontend`. It uses Vite for development and leverages CDN-hosted assets for React and xterm.js to keep the payload light.
* **Automation**: The MSBuild process automatically handles `npm install` and `npm run build` during the publish phase.
* **Optimization**: A post-link step invokes `optimize-wasm.js` using `wasm-opt` from the .NET Emscripten workload to ensure peak performance.

## Repository Layout

```text
├── Podish.Browser/   # Wasm Host & Frontend
├── Podish.Core/      # Orchestration Layer
├── Fiberish.Core/    # Linux Compatibility
├── Fiberish.X86/     # CPU Wrapper
└── Fiberish.SilkFS/  # Storage Layer
```

## License

This project is licensed under the **LGPL-2.0 License**.
