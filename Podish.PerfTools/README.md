# Podish.PerfTools

`Podish.PerfTools` is a suite of offline performance analysis tools centered around `Podish.Cli` and `libfibercpu`.

It currently handles two main workflows:

1. `profile` pipeline
   Records and analyzes runtime hotspots, automatically selecting the backend based on platform.
2. `superopcode` pipeline
   Extracts opcode adjacency relationships from guest block dumps, filters candidates, and generates
   `libfibercpu/src/generated/superopcodes.generated.cpp`.

This README focuses on the complete, usable workflow rather than just listing commands.

## Feature Overview

Currently supported commands:

- `analyze-blocks`
- `analyze-superopcode-candidates`
- `gen-superopcodes`
- `pipeline`
- `runner`
- `profile`

Where:

- `profile` is for "finding hotspots, viewing disassembly, comparing optimizations"
- `analyze-blocks` to `gen-superopcodes` is for "exploring and generating superopcodes"
- `pipeline` is a one-stop entry point for the superopcode workflow
- `runner` is the C# CoreMark benchmark harness used to collect `guest-stats` for superopcode analysis

## Project Relationships

The relationship between this toolset and other components in the repository is:

- `Podish.Cli`
  The host program that actually runs the guest workload
- `libfibercpu`
  The native x86 emulation core; most profile hotspots fall here
- `Podish.PerfTools runner`
  The C# benchmark harness used to run CoreMark and export `guest-stats`
- `benchmark/podish_perf/prepare_coremark_env.sh`
  Responsible for preparing the CoreMark rootfs

## Dependencies

Base dependencies:

- `.NET SDK`
- `Podish.Cli` must be buildable
- A prepared CoreMark rootfs

Profile-specific additional dependencies:

- macOS:
    - `xctrace`
    - `xcrun`
- Linux:
    - `perf`
    - `llvm-objdump` or `objdump`

Superopcode-specific additional dependencies:

- A buildable `Podish.PerfTools` runner
- Ability to execute `Podish.Cli run --guest-stats-dir ...`

## Building

First verify the tool can build:

```bash
 dotnet build Podish.PerfTools/Podish.PerfTools.csproj
```

If there are issues with the local `dotnet run` apphost, temporarily add:

```bash
-p:UseAppHost=false
```

For example:

```bash
 dotnet build Podish.PerfTools/Podish.PerfTools.csproj -p:UseAppHost=false
```

## Output Directory Conventions

By default, most outputs are written to:

```text
benchmark/podish_perf/results/
```

Superopcode generation results are written to:

```text
libfibercpu/src/generated/superopcodes.generated.cpp
```

## One: Profile Pipeline

The `profile` command handles recording and analyzing runtime hotspots or native-memory retention in `Podish.Cli`.

### Backend Selection

`--backend auto` automatically selects:

- macOS: `xctrace`
- Linux: `perf`

When analyzing existing traces, it also infers the backend from the input file extension:

- `.trace` → `xctrace`
- `.data` → `perf`

### Default Workload

The default guest workload for profiling is CoreMark `run` case.

`--bench-case` accepts values:

- `compress`
- `compile`
- `run`
- `gcc_compile`

Commonly used ones are:

- `run`
- `gcc_compile`

### 1. One-step Recording and Analysis

Most common usage:

```bash
 dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- \
   profile record-and-analyze \
   --backend auto \
   --name coremark-profile
```

Common parameters:

- `--backend auto|perf|xctrace`
- `--mode cpu|native-memory`
- `--name <name>`
- `--output-dir <dir>`
- `--binary <Podish.Cli binary>`
- `--rootfs <rootfs dir>`
- `--bench-case run|compile|compress|gcc_compile`
- `--iterations <n>`
- `--time-limit <seconds>`
- `--top <n>`
- `--disasm-top <n>`
- `--jit-map-dir <dir>`

`--mode cpu` is the default and uses:

- macOS: `Time Profiler`
- Linux: `perf`

`--mode native-memory` currently requires macOS `xctrace` and uses the `Allocations` template. The analysis report
includes:

- live allocation callers aggregated by retained bytes
- retained allocation categories from the `Allocations > Statistics` view
- raw exports for `Allocations List` and `Statistics`

### 2. Record Only

```bash
 dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- \
   profile record \
   --backend auto \
   --mode cpu \
   --name coremark-profile
```

This step generates platform-specific raw traces:

- macOS: `<name>.trace`
- Linux: `<name>.data`

And produces:

- `record-command.txt`

This file is very useful as it allows reproducing the exact recording command later.

### 3. Analyze Existing Trace

```bash
 dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- \
   profile analyze \
   --backend auto \
   --mode cpu \
   --trace benchmark/podish_perf/results/coremark-profile/coremark-profile.data \
   --name coremark-profile
```

For an existing `.trace` recorded with the `Allocations` template:

```bash
 dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- \
   profile analyze \
   --backend xctrace \
   --mode native-memory \
   --trace /tmp/repro.trace \
   --name repro-native-memory
```

### 4. Compare Two Profiles

```bash
 dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- \
   profile compare \
   --report benchmark/podish_perf/results/run-a/run-a.report.json \
   --report benchmark/podish_perf/results/run-b/run-b.report.json
```

### Profile Outputs

Typical outputs include:

- `<name>.trace` or `<name>.data`
- `<name>.xml` or `<name>.perf-script.txt`
- `<name>.report.json`
- `<name>.report.md`
- `disasm-*.txt`
- `record-command.txt`

### Disassembly and Symbol Resolution

`Podish.PerfTools` automatically selects available tools:

- Disassembly:
    - macOS prioritizes `xcrun llvm-objdump`
    - Other platforms prioritize `llvm-objdump`
    - Falls back to `objdump`
- Symbols:
    - Parsed directly from ELF, PE, or Mach-O binaries via built-in object file readers

This means symbol resolution no longer depends on an external `nm` tool, while disassembly still adapts automatically to
the available objdump implementation.

### Recommended Usage

To see where current optimizations are spending time, follow these steps:

1. Run `record-and-analyze` once
2. Check `<name>.report.md`
3. Open the top few `disasm-*.txt` files
4. Use `profile compare` to contrast the two `report.json` files before and after optimization

## Two: Superopcode Pipeline

The superopcode pipeline consists of four steps:

1. Run guest workload and export `blocks.bin`
2. Parse `blocks.bin` to generate `blocks_analysis.json`
3. Aggregate candidates to produce `superopcode_candidates.json/.md`
4. Generate `libfibercpu/src/generated/superopcodes.generated.cpp` from candidates

### Core Concepts

`blocks.bin` contains handler-level execution block information.

`analyze-blocks` maps handler addresses, execution counts, 2-grams, etc., into:

- `logic_func`
- `op_id`
- `n-gram`

Then `analyze-superopcode-candidates` performs def-use adjacency filtering based on these results to select worthwhile
2-op pairs.

## Step One: Prepare RootFS

If the rootfs isn't ready yet:

```bash
 benchmark/podish_perf/prepare_coremark_env.sh
```

Default rootfs location:

```text
benchmark/podish_perf/rootfs/coremark_i386_alpine
```

## Step Two: Generate guest-stats

The easiest way is to let `Podish.PerfTools runner` handle running the case and exporting `guest-stats`.

For example:

```bash
 dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- \
   runner \
   --engine jit \
   --jit-handler-profile-block-dump \
   --disable-superopcodes \
   --block-n-gram 2 \
   --aggregate-superopcode-candidates \
   --candidate-top 256 \
   --case run \
   --repeat 1 \
   --rootfs benchmark/podish_perf/rootfs/coremark_i386_alpine
```

This creates output like:

```text
benchmark/podish_perf/results/<timestamp>/guest-stats/jit-run-01/blocks.bin
benchmark/podish_perf/results/<timestamp>/guest-stats/jit-run-01/summary.json
```

If you already have `blocks.bin`, you can skip this step.

## Step Three: Parse blocks.bin

Use `analyze-blocks` to convert `blocks.bin` into structured JSON:

```bash
 dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- \
   analyze-blocks \
   --input benchmark/podish_perf/results/<timestamp>/guest-stats/jit-run-01/blocks.bin \
   --output benchmark/podish_perf/results/<timestamp>/guest-stats/jit-run-01/blocks_analysis.json
```

Common parameters:

- `--input <blocks.bin>`
- `--lib <libfibercpu.so|dylib|dll>` (optional; only needed for legacy dumps without embedded handler metadata)
- `--output <blocks_analysis.json>`
- `--n-gram 2`
- `--top-ngrams <n>`

### Critical Requirement

New-format `blocks.bin` embeds handler ids, handler symbols, and opcode ids, so it can be analyzed without reopening the native library.

If you're analyzing a legacy dump with `--lib`, `blocks.bin` must still come from the same build as the provided `libfibercpu`. If they're mismatched, the most common issues are:

- Handler addresses can't be resolved
- `logic_func` appears empty
- Subsequent candidate sets become severely skewed

This is the most common pitfall in the superopcode pipeline.

## Step Four: Aggregate Superopcode Candidates

Once you have multiple `blocks_analysis.json` files, aggregate candidates:

```bash
 dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- \
   analyze-superopcode-candidates \
   benchmark/podish_perf/results/<timestamp>/guest-stats \
   --top 256 \
   --anchor-top 64 \
   --min-samples 2 \
   --output-json benchmark/podish_perf/results/<timestamp>/superopcode_candidates.json \
   --output-md benchmark/podish_perf/results/<timestamp>/superopcode_candidates.md
```

This command recursively scans `blocks_analysis.json` files in the input directory.

### Candidate Filtering Rules

The current C# implementation includes:

- Supports only `2-op` superopcodes
- Performs def-use semantic analysis
- Retains only truly related pairs
- Distinguishes `RAW / RAR / WAW`
- Supports `anchor` / `pair` scoring basis
- Supports `raw_weight / rar_weight / waw_weight`
- Supports `jcc_multiplier / jcc_mode`

Recommended defaults come from the C# pipeline and its existing validation data. Avoid changing them casually.

### Common Outputs

- `superopcode_candidates.json`
- `superopcode_candidates.md`

Where:

- JSON is suitable for consumption by the generator
- Markdown is suitable for manual review of top candidates

## Step Five: Generate superopcodes.generated.cpp

```bash
 dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- \
   gen-superopcodes \
   --input benchmark/podish_perf/results/<timestamp>/superopcode_candidates.json \
   --output libfibercpu/src/generated/superopcodes.generated.cpp \
   --top 256
```

Output:

```text
libfibercpu/src/generated/superopcodes.generated.cpp
```

This step only generates code—it doesn't build or validate.

## Three: One-Stop Superopcode Pipeline

If you don't want to manually split the four steps, use:

```bash
 dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- \
   pipeline \
   --candidate-top 256 \
   --superopcode-top 256 \
   --reuse-rootfs
```

It performs these actions:

1. Calls `Podish.PerfTools runner`
2. Runs benchmark with `EnableSuperOpcodes=false`
3. Exports handler profile block dump
4. Aggregates candidates
5. Generates `libfibercpu/src/generated/superopcodes.generated.cpp`
6. Optionally rebuilds `Podish.Cli` for verification

Common parameters:

- `--project-root <repo>`
- `--rootfs <dir>`
- `--results-dir <dir>`
- `--case <compress|compile|run>`
- `--repeat <n>`
- `--iterations <n>`
- `--timeout <seconds>`
- `--candidate-top <n>`
- `--superopcode-top <n>`
- `--generated-output <path>`
- `--reuse-rootfs`
- `--keep-workdirs`
- `--skip-verify-build`

### Recommended Scenarios

Best suited when "restarting from scratch to find a new set of candidates".

If you already have clean `guest-stats`, directly running:

- `analyze-blocks`
- `analyze-superopcode-candidates`
- `gen-superopcodes`

is more controllable.

## Four: Recommended Practical Workflow

### Scenario A: I want to see current hotspots

```bash
 dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- \
   profile record-and-analyze \
   --backend auto \
   --name coremark-profile
```

Then examine:

- `<name>.report.md`
- `disasm-*.txt`

### Scenario B: I want to regenerate a new set of 256 superopcodes

1. Prepare rootfs

```bash
 benchmark/podish_perf/prepare_coremark_env.sh
```

2. Run pipeline

```bash
 dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- \
   pipeline \
   --candidate-top 256 \
   --superopcode-top 256 \
   --reuse-rootfs
```

3. Confirm generated output

```text
libfibercpu/src/generated/superopcodes.generated.cpp
```

4. Rebuild `Podish.Cli`, then run CoreMark/profile validation

### Scenario C: I already have fresh blocks and want to analyze manually

1. Parse blocks

```bash
 dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- \
   analyze-blocks \
   --input <blocks.bin> \
   --output <blocks_analysis.json>
```

2. Aggregate candidates

```bash
 dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- \
   analyze-superopcode-candidates <guest-stats-dir> \
   --top 256 \
   --output-json <superopcode_candidates.json> \
   --output-md <superopcode_candidates.md>
```
