# Podish on CoreCLR WebAssembly

`Podish.Browser` targets `net11.0` and uses the CoreCLR `browser-wasm` runtime. The
.NET SDK links the Emscripten build of `libfibercpu` into the Wasm runtime and
produces the fingerprinted and compressed assets used by the browser frontend.

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

## Wasm hot dispatch loop

The Browser build defaults to the `1.75m` baking preset and its multi-workload
hot dispatch plan. The build generates a C++ switch that inlines scalar and
fused instructions. A successful case advances the cursor and returns to the
loop head. Cold instructions, memory replay, and block-link misses use the
threaded handlers. Wasm builds use `GprPin=OFF`.

Set `WasmHotDispatchHandlers` to select a prefix of the plan. A ranked Wasm body
can contribute several cases through its C++ bindings. Set the budget to `0`
to use threaded dispatch throughout. Set `FibercpuBakingPreset=3.5m` to compare
the larger preset with its own plan.

```sh
dotnet publish Podish.Browser -c Release -p:WasmHotDispatchHandlers=0 \
  -o artifacts/wasm-threaded

dotnet publish Podish.Browser -c Release -p:FibercpuBakingPreset=3.5m \
  -o artifacts/wasm-hot-dispatch/3.5m
```

Train each baking preset from its own threaded-dispatch build. Collect one V8
`.cpuprofile` per workload and pass the profiles, native Wasm, and linker
symbols to `scripts/profile_wasm_hot_dispatch.py`. The script normalizes each
profile by its eligible handler time before combining the rankings. The
checked-in plans use CoreMark, GEMM, gzip, and SciMark SSE2. Check the budget
on independent inputs such as `grep`, `sed`, SciMark x87, and a separate gzip
corpus. Pass a custom plan's absolute path as `-p:WasmHotDispatchPlan=...`.
