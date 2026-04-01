# Podish Browser Wasm

This project hosts `Podish.Core` in a `browser-wasm` .NET app and uses a React.js + xterm.js frontend.

## Build

```bash
dotnet publish /Users/jiangyiheng/repos/x86emu/Podish.Browser/Podish.Browser.csproj -c Release
```

## Post-process Wasm

Keep `WasmRunWasmOpt` disabled in MSBuild and run `wasm-opt` as a separate post-publish step.
The repository includes a helper script that locates the active `dotnet.native.*.wasm`,
runs `wasm-opt`, and regenerates `.br` / `.gz` assets.
When invoked from `dotnet publish`, the project passes the `wasm-opt` path resolved from
the active .NET Emscripten toolset via `EmscriptenSdkToolsPath`, so it does not depend on PATH guessing.

```bash
cd /Users/jiangyiheng/repos/x86emu/Podish.Browser/frontend
node scripts/optimize-wasm.js --profile Oz
```

Useful options:

```bash
node scripts/optimize-wasm.js --profile O2
node scripts/optimize-wasm.js --profile Oz --strip-producers
node scripts/optimize-wasm.js --wasm-opt /path/to/wasm-opt --publish-dir /path/to/publish/wwwroot
```

Current measured result on this repo's AOT browser build:

- raw `dotnet.native.*.wasm`: `61.8 MiB` -> `16.3 MiB`
- brotli `dotnet.native.*.wasm.br`: `10.78 MiB` -> `3.47 MiB`
- gzip `dotnet.native.*.wasm.gz`: `22.37 MiB` -> `5.15 MiB`

## Notes

- `Fiberish.X86` now treats `browser-wasm` as a static-native target and emits a `NativeFileReference` for
  `libfibercpu.a`.
- The browser build expects an Emscripten toolchain file. Override `EmscriptenToolchainFile=/path/to/Emscripten.cmake`
  if Homebrew is not used.
- The current frontend uses CDN-hosted React and xterm assets for a zero-bundler scaffold.
- `build-rootfs.sh` now emits `image.json`, `indexes/`, and `blobs/` into `frontend/public/` for streamed browser boot.
