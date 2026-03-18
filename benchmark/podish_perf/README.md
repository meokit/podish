# Podish CoreMark Performance Runner

This directory contains a standalone performance harness for `Podish.Cli`.

It is intentionally separate from the normal test suites, so routine `dotnet test` and `pytest` runs do not execute benchmark workloads.

## What it does

- builds a prepared i386 Alpine environment with `git`, `gcc`, `musl-dev`, `make`, and `gzip`
- clones CoreMark into `/coremark`
- prebuilds `/coremark/coremark.exe`
- exports that environment as a local rootfs for `Podish.Cli --rootfs`
- runs benchmark cases through the actual runtime

Current benchmark cases:

- `compress`: archive and extract `/coremark`
- `compile`: rebuild CoreMark without running it
- `run`: execute the prebuilt `coremark.exe`

## Prerequisites

- a Release build of `Podish.Cli`
- Python 3
- `podman`
- on macOS, a running `podman machine`

Build the Release runtime and prepare the rootfs:

```bash
dotnet build Podish.Cli/Podish.Cli.csproj -c Release
benchmark/podish_perf/prepare_coremark_env.sh
```

The prepared rootfs is written to:

```text
benchmark/podish_perf/rootfs/coremark_i386_alpine
```

If you also want a NativeAOT binary:

```bash
dotnet publish Podish.Cli/Podish.Cli.csproj -c Release -r osx-arm64 -p:PublishAot=true -o build/nativeaot/podish-cli-static
```

## Run benchmarks

Interpreter / normal managed runtime path:

```bash
python3 benchmark/podish_perf/runner.py --engine jit --repeat 3
```

NativeAOT:

```bash
python3 benchmark/podish_perf/runner.py --engine aot --repeat 3
```

Useful options:

```bash
python3 benchmark/podish_perf/runner.py --engine jit --case compile --case run --repeat 5
python3 benchmark/podish_perf/runner.py --engine aot --iterations 1000
python3 benchmark/podish_perf/runner.py --engine jit --reuse-rootfs
python3 benchmark/podish_perf/runner.py --engine aot --aot-binary /path/to/Podish.Cli
```

The runner copies the prepared rootfs into a disposable work directory for each sample, so the source rootfs is not dirtied by benchmark writes.

Outputs are written under:

```text
benchmark/podish_perf/results/
```

## Optional JIT baseline

The copy-and-patch JIT is currently an experimental baseline and is disabled by default in CMake.

If you intentionally want to benchmark that path, rebuild `libfibercpu` with:

```bash
-DFIBERCPU_ENABLE_JIT=ON
```

On current local measurements noted in this repo, that baseline JIT is still slower than the interpreter on the CoreMark run workload.

## Export block dumps and mine SuperOpcode candidates

To collect handler n-grams and aggregate `SuperOpcode` candidates, the analyzer now scores each 2-gram globally as:

```text
score = frequency * dep_weight
```

The current default uses pair frequency. `RAW` pairs are weighted as `2`, and `RAR` / `WAW` pairs are weighted as `0`, which effectively keeps the default search focused on RAW-only candidates.

Run the profiling pass like this:

```bash
python3 benchmark/podish_perf/runner.py \
  --engine jit \
  --case run \
  --repeat 3 \
  --jit-handler-profile-block-dump \
  --block-n-gram 2 \
  --aggregate-superopcode-candidates
```

This writes per-sample analysis under:

```text
benchmark/podish_perf/results/<timestamp>/guest-stats/.../blocks_analysis.json
```

and aggregate outputs under:

```text
benchmark/podish_perf/results/<timestamp>/superopcode_candidates.json
benchmark/podish_perf/results/<timestamp>/superopcode_candidates.md
```

You can aggregate existing samples later:

```bash
python3 benchmark/podish_perf/analyze_superopcode_candidates.py \
  benchmark/podish_perf/results/<timestamp>/guest-stats \
  --n-gram 2 \
  --top 100 \
  --output-json benchmark/podish_perf/results/<timestamp>/superopcode_candidates.json \
  --output-md benchmark/podish_perf/results/<timestamp>/superopcode_candidates.md
```

To run the whole SuperOpcode mining flow in one command:

```bash
python3 benchmark/podish_perf/superopcode_pipeline.py \
  --results-dir benchmark/podish_perf/results/superopcode-run \
  --case run \
  --repeat 1 \
  --iterations 3000
```

That pipeline builds an analysis binary with `EnableSuperOpcodes=false`, mines raw opcode streams, ranks global 2-gram candidates by anchor frequency and dependency weight, generates `libfibercpu/generated/superopcodes.generated.cpp`, and can optionally rebuild with superopcodes enabled for verification.

## Record and analyze xctrace

Use `benchmark/podish_perf/profile_xctrace.py` to:

- copy the NativeAOT binary to a unique name to avoid colliding with the Swift app
- record a Time Profiler trace
- export raw `time-profile` XML
- aggregate steady-state hotspots after warmup cutoff
- dump disassembly for hot symbols
- compare multiple runs

Record and analyze in one step:

```bash
python3 benchmark/podish_perf/profile_xctrace.py record-and-analyze \
  --binary build/nativeaot/podish-cli-static/Podish.Cli \
  --name coremark-current
```

Profile the gcc compile workload instead of the CoreMark run workload:

```bash
python3 benchmark/podish_perf/profile_xctrace.py record-and-analyze \
  --binary build/nativeaot/podish-cli-static/Podish.Cli \
  --bench-case gcc_compile \
  --name coremark-gcc-compile
```

Analyze an existing trace:

```bash
python3 benchmark/podish_perf/profile_xctrace.py analyze \
  --trace benchmark/podish_perf/results/coremark-flags-cache-retuned-v2.trace \
  --binary build/nativeaot/podish-cli-static/PodishCliFlagsRetuned \
  --name coremark-flags-cache-retuned-v2
```

Compare saved reports:

```bash
python3 benchmark/podish_perf/profile_xctrace.py compare \
  --report benchmark/podish_perf/results/run-a/run-a.report.json \
  --report benchmark/podish_perf/results/run-b/run-b.report.json
```

Typical outputs:

- `<name>.trace`
- `<name>.xml`
- `<name>.report.json`
- `<name>.report.md`
- `disasm-*.txt`
