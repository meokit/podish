# Podish CoreMark Performance Runner

This directory contains a standalone performance harness for `Podish.Cli`. It
is deliberately separate from `pytest`, so normal test runs do not execute any
performance workload.

## What it does

- Builds a preconfigured i386 Alpine environment with `git`, `gcc`, `musl-dev`,
  `make`, and `gzip`
- Clones CoreMark into `/coremark` inside that environment
- Prebuilds `/coremark/coremark.exe` with `make ... compile`
- Exports the environment as a local rootfs directory for `Podish.Cli --rootfs`
- Runs three benchmark cases with `pexpect`
  - `compress`: archive and extract `/coremark`
  - `compile`: rebuild CoreMark without running it
  - `run`: execute the prebuilt `coremark.exe`

## Prepare the rootfs

Build the release JIT binary first because the runner uses `dotnet run -c Release --no-build`:

```bash
dotnet build Podish.Cli/Podish.Cli.csproj -c Release
benchmark/podish_perf/prepare_coremark_env.sh
```

If you want to benchmark NativeAOT, also publish the AOT binary:

```bash
dotnet publish Podish.Cli/Podish.Cli.csproj -c Release -r osx-arm64 -p:PublishAot=true -o build/nativeaot/podish-cli-static
```

On macOS, make sure `podman machine start` is already running before preparing
the rootfs.

The exported rootfs will be written to:

```text
benchmark/podish_perf/rootfs/coremark_i386_alpine
```

## Run the benchmarks

Release JIT:

Note: the current copy-and-patch JIT is an experimental baseline and is now
disabled by default in CMake. If you intentionally want to benchmark that path,
configure/build `libfibercpu` with `-DFIBERCPU_ENABLE_JIT=ON` first. On current
M3 Max measurements, that baseline JIT is still slower than the interpreter
(roughly `~1950` vs `~2250` CoreMark iterations/sec).

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

By default the runner copies the prepared rootfs into a disposable work
directory for each sample, so `--rootfs` does not get dirtied by benchmark
writes. Logs and `summary.json` are written under
`benchmark/podish_perf/results/`.

## Export block dumps and aggregate SuperOpcode candidates

If you want to mine handler 2-grams for `SuperOpcode` work, use the JIT
handler-profile build and enable block analysis:

```bash
python3 benchmark/podish_perf/runner.py \
  --engine jit \
  --case run \
  --repeat 3 \
  --jit-handler-profile-block-dump \
  --block-n-gram 2 \
  --aggregate-superopcode-candidates
```

This writes per-sample files under:

```text
benchmark/podish_perf/results/<timestamp>/guest-stats/.../blocks_analysis.json
```

and also emits aggregate outputs:

```text
benchmark/podish_perf/results/<timestamp>/superopcode_candidates.json
benchmark/podish_perf/results/<timestamp>/superopcode_candidates.md
```

You can also aggregate existing samples later:

```bash
python3 benchmark/podish_perf/analyze_superopcode_candidates.py \
  benchmark/podish_perf/results/<timestamp>/guest-stats \
  --n-gram 2 \
  --top 100 \
  --output-json benchmark/podish_perf/results/<timestamp>/superopcode_candidates.json \
  --output-md benchmark/podish_perf/results/<timestamp>/superopcode_candidates.md
```

To run the whole SuperOpcode mining flow in one command, use:

```bash
python3 benchmark/podish_perf/superopcode_pipeline.py \
  --results-dir benchmark/podish_perf/results/superopcode-run \
  --case run \
  --repeat 1 \
  --iterations 3000
```

This pipeline intentionally builds the analysis binary with `EnableSuperOpcodes=false`,
runs the benchmark and candidate mining on raw opcode streams, then generates
`libfibercpu/generated/superopcodes.generated.cpp`, and finally does an optional
verification rebuild with superopcodes enabled.

## Record and analyze `xctrace`

Use [profile_xctrace.py](/Users/jiangyiheng/repos/x86emu/benchmark/podish_perf/profile_xctrace.py) to:

- copy the NativeAOT binary to a unique name to avoid clashing with a local Swift app
- record a `Time Profiler` trace
- export the raw `time-profile` XML
- aggregate steady-state hotspots after a warmup cutoff
- dump disassembly for the top symbols
- compare multiple runs with a markdown table

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

Outputs:

- `<name>.trace`
- `<name>.xml`
- `<name>.report.json`
- `<name>.report.md`
- `disasm-*.txt`
