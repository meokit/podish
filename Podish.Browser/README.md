# Podish Browser Wasm

This project hosts `Podish.Core` in a `browser-wasm` .NET app and uses a React.js + xterm.js frontend.

## Build

```bash
dotnet publish /Users/jiangyiheng/repos/x86emu/PodishApp/browserwasm/PodishApp.BrowserWasm.csproj -c Release
```

## Notes

- `Fiberish.X86` now treats `browser-wasm` as a static-native target and emits a `NativeFileReference` for
  `libfibercpu.a`.
- The browser build expects an Emscripten toolchain file. Override `EmscriptenToolchainFile=/path/to/Emscripten.cmake`
  if Homebrew is not used.
- The current frontend uses CDN-hosted React and xterm assets for a zero-bundler scaffold.
