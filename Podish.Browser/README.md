# Podish on CoreCLR WebAssembly

`Podish.Browser` targets `net11.0` and uses the CoreCLR `browser-wasm` runtime. The
.NET SDK links the Emscripten build of `libfibercpu` into the Wasm runtime and
produces the fingerprinted and compressed assets used by the browser frontend.
On Emscripten, the i386 engine enables the hand-written single-block WebAssembly
JIT (`FIBERCPU_ENABLE_WASM_JIT=ON`, with native Wasm FP/SIMD128 SSE/SSE2 lowering
and threaded interpreter fallback), disables i386 static baking by default
(`FIBERCPU_I386_STATIC_SPECIALIZATION=AUTO`), and omits offline handler reflection
symbol tables (`FIBERCPU_ENABLE_HANDLER_REFLECTION=OFF`). Set
`-p:FibercpuBakingPreset=1.5m` when publishing to shrink AArch64 baked tables.

## Build and serve

Install the .NET 11 SDK, Node.js 24, CMake, Ninja, and Python 3. The .NET workload
supplies the matching Emscripten compiler used to build and link `libfibercpu`.

```sh
dotnet workload install wasm-tools
dotnet publish Podish.Browser -c Release
python3 scripts/serve_browserwasm.py
```

Open `http://127.0.0.1:8081/?import=1` to upload a rootfs archive. To serve the
default streamed rootfs, use `build-rootfs.sh` to prepare `image.json`,
`indexes/`, and `blobs/` under `frontend/public/rootfs/` before publishing.

## Run in Node.js

Run a noninteractive i386 guest command from an uncompressed rootfs tar. The
runner uses the same published Wasm bundle as the browser, writes guest output
to Node's standard streams, and returns the guest exit status. Guest stdin is
closed and the native host JIT is disabled.

```sh
node Podish.Browser/node.mjs rootfs.tar /opt/bench/coremark 0 0 0x66 100000 7 1 2000
```

Set `PODISH_WASM_APP_DIR` to the published `wwwroot` directory when using a
custom publish location. It must contain `_framework/dotnet.js`.

See the [CoreMark benchmark results](../benchmark/coreclr-wasm-v8.md).
