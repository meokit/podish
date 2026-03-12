# Podish CoreMark Performance Runner

This directory contains a standalone performance harness for Podish.Cli. It is
deliberately separate from `pytest`, so normal test runs do not execute any
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

Build Podish.Cli first because the runner uses `dotnet run --project ... --no-build`:

```bash
dotnet build Podish.Cli/Podish.Cli.csproj
benchmark/podish_perf/prepare_coremark_env.sh
```

On macOS, make sure `podman machine start` is already running before preparing
the rootfs.

The exported rootfs will be written to:

```text
benchmark/podish_perf/rootfs/coremark_i386_alpine
```

## Run the benchmarks

```bash
python3 benchmark/podish_perf/runner.py --repeat 3
```

Useful options:

```bash
python3 benchmark/podish_perf/runner.py --case compile --case run --repeat 5
python3 benchmark/podish_perf/runner.py --iterations 1000
python3 benchmark/podish_perf/runner.py --reuse-rootfs
```

By default the runner copies the prepared rootfs into a disposable work
directory for each sample, so `--rootfs` does not get dirtied by benchmark
writes. Logs and `summary.json` are written under `benchmark/podish_perf/results/`.
