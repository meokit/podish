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
