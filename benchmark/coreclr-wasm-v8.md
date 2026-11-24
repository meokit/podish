# CoreMark on CoreCLR Wasm and Node.js

Measured on an Apple M5 Max running macOS 26.3.2, with .NET SDK 11.0.100 and
runtime 11.0.0. The Wasm build used Emscripten 6.0.2 and Node.js 24.18.1
(V8 13.6.233.17-node.50). The native macOS build used Apple Clang 17.0.0.

Both builds used the default `3.5m` baking preset in Release, with the LLVM
host JIT and dynamic instruction specialization disabled. Both executed the
same static i386 CoreMark ELF from the [comparison fixture](emulator-comparison/README.md).
It was built with GCC 15.2.0 and `-O3 -static -fno-pie -no-pie`. Its SHA-256
was `fcb65151196caa0678c992677442aa7ce7f41e9e46cbaa690d43746f0907fff6`.

Each scored run started a fresh host process and performed 100,000 guest
iterations. The backends ran serially and alternated order across three
rounds. Scores are guest-reported iterations per second and exclude host
startup and rootfs loading. All scored runs validated the CoreMark CRCs
and reported a final CRC of `0xd340`.

| Backend | Run 1 | Run 2 | Run 3 | Median (iterations/s) |
| --- | ---: | ---: | ---: | ---: |
| CoreCLR Wasm in Node/V8 | 1,031.67 | 1,309.96 | 1,306.73 | 1,306.73 |
| Native macOS arm64 | 4,557.89 | 4,624.71 | 4,884.96 | 4,624.71 |

The Node/V8 median was 28.3% of the native median on this machine.

## Reproduce

Prepare a minimal tar containing the fixture's `opt/bench/coremark` and put
the same file in a directory for the native CLI.

```sh
mkdir -p artifacts/coremark-rootfs/opt/bench
cp /path/to/extracted-fixture/opt/bench/coremark artifacts/coremark-rootfs/opt/bench/
tar -cf artifacts/coremark-rootfs.tar -C artifacts/coremark-rootfs opt

dotnet workload install wasm-tools
dotnet publish Podish.Browser -c Release -p:DynamicInstructionSpecialization=OFF
dotnet build Podish.Cli -c Release -p:DynamicInstructionSpecialization=OFF

node Podish.Browser/node.mjs artifacts/coremark-rootfs.tar \
  /opt/bench/coremark 0 0 0x66 100000 7 1 2000

dotnet Podish.Cli/bin/Release/net11.0/Podish.Cli.dll run --rm --log-driver none \
  --rootfs artifacts/coremark-rootfs -XX:JitMode=off -- \
  /opt/bench/coremark 0 0 0x66 100000 7 1 2000
```

## Inspect V8 machine code

`wasm-objdump -d` disassembles the Wasm bytecode. To see the ARM64 machine
code V8 generates, use Node's `--print-wasm-code-function-index`. Map a Wasm
function index to its C++ symbol using the Emscripten linker symbol table.
The indices can change when the Wasm bundle is rebuilt.
[V8 disassembly implementation](https://chromium.googlesource.com/v8/v8/+/f0e7875366d7e4bd0531be4cc95de0cdec1a5e26/src/wasm/wasm-code-manager.cc)

```sh
symbols=Podish.Browser/obj/Release/net11.0/wasm/for-publish/dotnet.native.js.symbols
index=$(awk -F: '$2 == "X86_Run" { print $1; exit }' "$symbols")
node --print-wasm-code-function-index="$index" \
  Podish.Browser/node.mjs artifacts/coremark-rootfs.tar \
  /opt/bench/coremark 0 0 0x66 3000 7 1 2000 > x86-run.asm

node --cpu-prof --cpu-prof-name=coremark.cpuprofile \
  Podish.Browser/node.mjs artifacts/coremark-rootfs.tar \
  /opt/bench/coremark 0 0 0x66 12000 7 1 2000
```

The machine-code dump labels each tier Liftoff or TurboFan. Load the CPU
profile in Chrome DevTools and use the same linker symbol table to identify
`wasm-function[N]` frames. Add `--trace-wasm-inlining` to see the actual
inlining decisions. Capture traces and profiles separately from scoring runs.
[V8 compilation pipeline](https://v8.dev/docs/wasm-compilation-pipeline)

In one Node 24 profile, 94.75% of sampled time was attributed to
`libfibercpu` symbols. Several conditional-jump handlers and block exits
were among the hottest symbols. In the native build, the representative
`OpJcc_NE_Rel32` wrapper loaded the handler pointer and dispatched with
`br x0` without a stack frame. The Wasm equivalent used
`return_call_indirect`. TurboFan emitted table bounds and signature checks,
a target load, and a longer register and stack sequence for the tail jump.

With `--wasm-inlining-call-indirect`, V8 inlined several hot successor
chains. The representative Jcc's two indirect call sites were megamorphic,
so its 848-byte TurboFan body still contained both indirect dispatch paths.
V8 also inlined its direct tail call to the block lookup fallback. These
observations explain why some handlers benefit from the flag while the
interpreter still pays indirect dispatch costs elsewhere.
[V8 indirect-call inlining](https://v8.dev/blog/wasm-speculative-optimizations)

## Follow-up measurements

The follow-up used the same guest ELF, Release preset, and Apple M5 Max.
The Wasm runs used 20,000 guest iterations and the native runs used 60,000.
Every scored run exceeded ten seconds and passed the CoreMark validation.
The Node version and flag-only comparisons used the original Wasm bundle.
The Node 26 comparison used 26.3.0 with V8 14.6.202.34-node.20.

| Configuration | Runs | Median (iterations/s) |
| --- | ---: | ---: |
| Node 24 default | 3 | 1,325.91 |
| Node 24 with indirect-call inlining | 3 | 1,549.55 |
| Node 26 default | 3 | 1,583.41 |
| Node 24 with scalar Wasm32 block-header loads | 2 | 1,312.70 |
| Node 24 with scalar loads and indirect-call inlining | 2 | 1,559.15 |
| Native arm64 with default GPR pinning | 2 | 4,885.93 |
| Native arm64 with GPR pinning disabled | 2 | 4,611.16 |

Node 24 gained 16.9% from indirect-call inlining and Node 26 gained 19.4%
at its default settings, both relative to the Node 24 default. The two
Wasm32 scalar-load variants stayed within 1% of their corresponding stock
settings in paired rounds. On native arm64, disabling GPR pinning reduced
throughput by 5.6%. The native comparison quantifies that part of the gap.

A second source experiment routed cold block-link misses through a function
table to keep lookup out of the hot handler's default inlining path. In two
more paired rounds, this reduced default Node 24 from 1,299.65 to 1,252.51
iterations/s. With indirect-call inlining enabled, the scores were
1,573.91 and 1,572.40, respectively. Both source experiments were reverted.

To reproduce the Node 24 improvement, keep the published bundle fixed and
add the V8 flag before the entry point.

```sh
node --wasm-inlining-call-indirect Podish.Browser/node.mjs artifacts/coremark-rootfs.tar \
  /opt/bench/coremark 0 0 0x66 20000 7 1 2000
```
