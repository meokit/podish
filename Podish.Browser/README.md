# Podish Browser Wasm

This project hosts `Podish.Core` in a `browser-wasm` .NET app and uses a React.js + xterm.js frontend.

## Build

```bash
dotnet publish /Users/jiangyiheng/repos/x86emu/Podish.Browser/Podish.Browser.csproj -c Release
```

## Post-process Wasm

Keep `WasmRunWasmOpt` disabled in MSBuild and run `wasm-opt` only as a strip/post-pack step.
The repository includes a helper script that locates the active `dotnet.native.*.wasm`,
runs `wasm-opt` with strip-only flags, and regenerates `.br` / `.gz` assets.
When invoked from `dotnet publish`, the project passes the `wasm-opt` path resolved from
the active .NET Emscripten toolset via `EmscriptenSdkToolsPath`, so it does not depend on PATH guessing.

```bash
cd /Users/jiangyiheng/repos/x86emu/Podish.Browser/frontend
node scripts/optimize-wasm.js
```

Useful options:

```bash
node scripts/optimize-wasm.js --strip-producers
node scripts/optimize-wasm.js --wasm-opt /path/to/wasm-opt --publish-dir /path/to/publish/wwwroot
node scripts/optimize-wasm.js --no-recompress
```

## Notes

- `Fiberish.X86` now treats `browser-wasm` as a static-native target and emits a `NativeFileReference` for
  `libfibercpu.a`.
- The browser build expects an Emscripten toolchain file. Override `EmscriptenToolchainFile=/path/to/Emscripten.cmake`
  if Homebrew is not used.
- The current frontend uses CDN-hosted React and xterm assets for a zero-bundler scaffold.
- `build-rootfs.sh` now emits `image.json`, `indexes/`, and `blobs/` into `frontend/public/rootfs/` for streamed browser boot.
