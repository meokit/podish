# i386 emulator comparison

Build the guest fixture once on x86-64 Linux, then copy the bundle unchanged to
each benchmark host. The guest contains the same Debian i386 userspace, ELF
binaries, Node dependency tree, inputs and expected outputs for every backend.

| Backend | Where to run | Execution mode |
| --- | --- | --- |
| Podish interpreter | Linux Docker or native macOS arm64 | Baked instruction specialization and extended superopcodes, Host JIT off |
| Podish LLVM JIT | Native macOS arm64 | Same specialization and fusion, online exact compilation with two workers |
| QEMU Usermode | Linux Docker | Direct `qemu-i386 -cpu max` invocation, with `-L` pointing at the extracted guest |
| QEMU system | Linux Docker or native macOS | `qemu-system-i386`, TCG, one vCPU, 1 GiB, `-cpu max`, Linux kernel from the guest bundle |
| iSH CLI | Native macOS arm64 | Upstream iSH with Meson Release settings, imported into fakefs |

## Build and deploy

From a checkout of the revision to measure, build the host image on each Linux
Docker engine. The first command uses the current Docker context. The second
sends the tracked source and new benchmark files over SSH and builds on the
remote Docker engine. The SSH login needs access to that engine. Set
`BENCH_SSH_CONFIG` if the host uses a separate SSH configuration file.

```sh
bench=benchmark/emulator-comparison
"$bench/deploy.sh" build-host
"$bench/deploy.sh" build-host linux-workstation
```

For OrbStack on macOS, select its Docker context or set
`DOCKER_HOST=unix:///var/run/docker.sock` when you invoke the first command.
Check that the Docker server architecture is aarch64. Select a CPU available
on each engine when running the tests below. For an x86-64 build, set
`BENCH_UBUNTU_MIRROR` to an HTTPS Ubuntu package mirror if needed.

On the x86-64 workstation, use the same checkout to create the fixture. The host
kernel must support IA-32 executables. A Docker engine without the Buildx plugin
can use `DOCKER_BUILDKIT=0`.

```sh
benchmark/emulator-comparison/deploy.sh build-fixture "$HOME/podish-bundle"
```

Copy the entire directory to the other host and verify `SHA256SUMS`.

```sh
benchmark/emulator-comparison/deploy.sh fetch-fixture linux-workstation \
  podish-bundle "$HOME/podish-bundle"
```

`rootfs.tar.gz` and `rootfs.cpio.gz` come from the same directory in one
build. Native Podish and QEMU Usermode read a tar extraction, iSH imports the
same tar into fakefs and the QEMU kernel unpacks the same CPIO tree. The
runner decompresses the CPIO on the host before booting the VM. Keep the
bundle and the per-run metadata with any results you publish. The Debian
package revisions can change when someone rebuilds the fixture later.

The workloads are CoreMark, GNU sha256sum linked against OpenSSL, GNU sort,
GNU gzip, Node 14.21.3 with Webpack, SciMark 4 with x87 and SSE2, and the
PolyBench packed SSE2 GEMM kernel. The fixture builder records binary and
package identities, checks source hashes, and uses a lockfile for Webpack.
The Webpack input is generated from fixed seeds and uses a separate corpus
from the Podish bake step. It leaves the guest V8 JIT enabled.

With AArch64 Node 14, the default Webpack MD4 backend reserves about 10 GiB
of virtual address space for WebAssembly. Builds with the 39-bit address
space can run the default fixture. For a build limited to 4 GiB, append
`--native-md4` after `webpack/build.cjs` to select OpenSSL's MD4 and get the
same bundle hash. Use the same backend on both guests when comparing timings.

### CoreMark build

All platforms and backends run the same CoreMark 1.0 i386 ELF. The fixture
builds it with Alpine GCC 15.2.0 and `-O3 -static -fno-pie -no-pie`, using
statically linked musl. The shared guest rootfs uses Debian. The runner
records the ELF SHA-256, checks the compiler and flags reported by the
guest, and checks the benchmark CRCs.

For the README CoreMark table, we ran Podish at `b30f252e` with `3.5m` and
reused the QEMU 8.2.2 and iSH scores from the previous pass. Both passes used
the same fixture and 190,000 iterations per run. We took the median of three
measured runs per cell.

## Run the Linux matrix

Run on each Linux Docker engine, with the copied bundle. Start with
`--smoke --repeats 1` and a fresh output directory to validate all eight
workloads. Run again without `--smoke` to collect scores.

```sh
benchmark/emulator-comparison/deploy.sh run-linux \
  "$HOME/podish-bundle" "$HOME/podish-results-linux" 2 \
  --machine "ARM64 Linux Docker" \
  --backend podish-off --backend qemu-user --backend qemu-system
```

## Run the macOS matrix

Use native arm64 Clang and LLVM 21 or newer from the same release. Build both
Podish modes once, then switch JIT at runtime. To build the pinned upstream
iSH CLI, install Meson, Ninja, pkg-config and libarchive. Put the host
libarchive pkg-config directory on `PKG_CONFIG_PATH` if Meson needs it.

```sh
bench=benchmark/emulator-comparison
out="$PWD/artifacts/readme-emulator-comparison"
llvm="$(brew --prefix llvm@21)"
"$bench/build-ish.sh" "$out/ish"

dotnet publish Podish.Cli/Podish.Cli.csproj -c Release -o "$out/podish" \
  -p:LlvmBlockJit=ON -p:NativeClangDir="$llvm/bin" \
  -p:LlvmConfigDir="$llvm/lib/cmake/llvm" \
  -p:DynamicInstructionSpecialization=OFF

python3 "$bench/measure.py" --bundle "$HOME/podish-bundle" \
  --state "$out/state" --output "$out/results" \
  --commit "$(git rev-parse HEAD)" --machine "Apple M5 Max native macOS" \
  --podish "$out/podish/Podish.Cli.dll" \
  --ish "$out/ish/build/ish" --fakefsify "$out/ish/build/tools/fakefsify" \
  --ish-commit "$(cat "$out/ish/commit")" \
  --backend podish-off --backend podish-jit --backend ish
```

Add `--backend qemu-system` if you have a native QEMU installation. The runner
records the version, which can differ from the Linux Docker version.

## Read results

Run one measurement at a time per physical machine, including the native and
VM runs on the same Mac. Use the same CoreMark iteration count on every host.
The runner checks that each scored CoreMark run takes at least ten seconds.
Use `--iterations` to tune that count after smoke testing. Pass
`--case coremark` to run only CoreMark. The comparison script puts those
CoreMark results in one table across environments.

The runner performs one unscored round before seven measured rounds and
rotates the backend order. Use `--warmup 0` for a run that follows a separate
smoke pass, or `--repeats` to set the number of samples. CoreMark and SciMark report in-guest scores. GEMM
reports its kernel timer. The four application workloads use host elapsed
time. In QEMU system that timer runs between serial markers around the
workload. VM boot time is recorded separately. The other backends include
their process launch and exit overhead in these four workloads. The README
tables should keep the native macOS and Linux VM environments separate.

Each run checks its output or benchmark result, preserves logs and records
failures. A case needs all measured repeats to produce a median and
interquartile interval. Report missing or failed cases with their reason.
Render checked runs as Markdown with the following command.

```sh
python3 benchmark/emulator-comparison/compare.py /path/to/linux-results /path/to/macos-results
```

The comparison script requires matching fixture hashes, Podish revisions,
CoreMark iteration counts and repeat counts. Use `--allow-smoke` only for
development checks.

## CoreMark interpreter specialization

The [interpreter comparison](../../README.md#coremark-interpreter-specialization)
uses two i386 ELFs from the same CoreMark source revision,
`1f483d5b8316753a742cbf5590caf5bd0a4e4777`. The Debian GCC 12.2.0 build
links glibc statically. The Alpine GCC 15.2.0 build links musl statically
and is the ELF used in the emulator comparison above. Both use effective
`-O3` and disable PIE. The Debian build passes
`XCFLAGS='-O3 -static -fno-pie -no-pie'` to the Linux port Makefile,
which logs its default `-O2` before the overriding `-O3`.

| ELF | SHA-256 |
| --- | --- |
| Debian GCC 12.2.0 | `21e5fa2303eff7481917bd44ee234022405e5f889c7181260254aefd053d5793` |
| Alpine GCC 15.2.0 | `fcb65151196caa0678c992677442aa7ce7f41e9e46cbaa690d43746f0907fff6` |

We ran both ELFs at the same guest path in the Debian rootfs. We built with
Clang 21.1.8 in Release mode and used a 400 MiB block cache. We set
`FIBERCPU_GPR_PIN=WRITE_BACK` to pin EAX and ESP, and disabled Host JIT,
dynamic specialization, and linked block caching.

| Measured configuration | Build settings |
| --- | --- |
| Default `3.5m` at `b30f252e` | Use the checked-in `3.5m` tables with Host JIT off. |
| No specialization or fusion at `37cea465` | Set `FIBERCPU_STATIC_SPECIALIZATION=OFF` and `FIBERCPU_ENABLE_SUPEROPCODES=OFF`. |
| Baked for either ELF at `37cea465` | Supply that ELF's ModRM plan, exact scalar plan, fusion operand plan, and generated fusion source. |

For the ELF-specific tables, we captured an unfused Block Profile for each
ELF over 20,000 CoreMark iterations. We targeted 99.9% weighted ModRM coverage
and capped the plans at 384 exact scalar variants, 384 fusion sequences, and
768 fusion operand variants. We turned profiling off for the measurements
and ran both ELFs against each table.

Run CoreMark with `0x0 0x0 0x66 100000 7 1 2000`. Alternate the build and ELF
order across three measured rounds and check the benchmark CRCs. The entries
where the training and measured ELF match quantify an in-profile result. The
crossed entries show how each plan transfers to the other ELF.

In the earlier experiment, we estimated exact scalar coverage at 75.9% on
the GCC 12 ELF and 85.1% on the GCC 15 ELF with the old default tables. With
the matching tables, those estimates rose to 98.4% and 97.5%. We replayed the
profiles and estimated that the old fusion tables saved 112 and 242
dispatches per thousand guest instruction visits. The matching tables
saved an estimated 642 and 651.

## Early public results

The initial public README recorded these CoreMark 1.0 scores. The entries
describe early interpreter builds and the tests available for each machine.

| Hardware | Podish | iSH | QEMU TCI | Native (arm64) |
| --- | ---: | ---: | ---: | ---: |
| iPhone 17 (A19) | ~3,447 | ~1,692 | n/a | n/a |
| MacBook Pro (M3 Max) | ~2,967 | n/a | ~325 | ~38,087 |

The initial README also recorded the following development results.

| Change | Approximate CoreMark score |
| --- | ---: |
| Fix TLB refill | 600 to 800 |
| Remove redundant writes and specialize handlers | 800 to 1,500 |
| Reorder fields for paired loads | 1,500 to 2,000 |
| Defer parity calculation and prune flag writes | 2,000 to 2,200 |
| Append short successor blocks | 2,200 to 2,500 |
| Bake instruction pairs from profiles | 2,500 to 3,000 |

For a repeatable comparison of the current interpreter variants, use the
[static CoreMark harness](../coremark-static.md).
