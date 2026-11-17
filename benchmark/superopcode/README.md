# Standard superopcode training

The workflow builds an **unfused, instrumented** native CPU, runs verified GNU
i386 command-line workloads, measures actual within-block Continue edges, derives
a lightweight suite from measured heavy/light profile similarity, emits selected
pairs and C++, and builds/verifies an **uninstrumented** candidate. Ordinary
builds still use the checked-in pinned table; no training silently overwrites it.

Measured results and limitations: [RESULTS.md](RESULTS.md).
Iterative multi-corpus selection, confirmation and reserved validation: [SEARCH.md](SEARCH.md).
Evaluated opt-in 32-pair + 32-four-op table and build instructions: [manifests/README.md](manifests/README.md).

## Requirements and explicit asset preparation

Use .NET 10+ and the normal native `Fiberish.X86` toolchain. Python 3.10+ is only
needed for the separate online asset-preparation step:

```sh
python benchmark/superopcode/prepare_gnu.py --output artifacts/superopcode/gnu-rootfs
```

This downloads only the exact SHA256-pinned Alpine x86 APKs in
`packages.lock.json`, extracts their regular files without running install hooks,
and materializes aliases as regular files (no Windows symlink privilege needed).
It refuses a nonempty destination. `--update-lock` is a separate maintainer action
which changes the selected versions/hashes; ordinary builds/training never do so.
The workload programs are **GNU tar 1.35, gzip 1.13, GNU sed 4.9 and GNU grep 3.11**,
not BusyBox applets. GNU Bash launches them by explicit `/bin/...` paths. Version
banners and all locked rootfs file hashes are verified before sampling. No guest
compiler, Docker, WSL, privileged mount or global package installation is needed.

Third-party binaries live only in ignored preparation output. Their package
licenses and exact download locations are recorded in the lock; they are not
relicensed or redistributed in the repository.

## Build entry points

From the repository root:

```sh
dotnet msbuild build/SuperOpcodes.proj -t:CollectHeavy
dotnet msbuild build/SuperOpcodes.proj -t:Distill
dotnet msbuild build/SuperOpcodes.proj -t:TrainAndVerify
dotnet msbuild build/SuperOpcodes.proj -t:Benchmark
```

`TrainAndVerify` includes heavy collection if absent, the light-size sweep,
selection, generation, candidate build and complete quick-suite differential
validation. `Benchmark` uses `holdout-v1.json` with an independent seed and compares
OFF, the current pinned table and the new candidate. It performs one discarded
warmup and seven interleaved/rotating samples per variant/workload by default.

Override `TrainingSuite`, `TrainingRootfs`, `TrainingOutput`, `GeneratedSource`,
`BenchmarkSuite` and `BenchmarkRepeat` as MSBuild properties. Defaults use
`artifacts/superopcode/standard-training`. No automatic promotion is performed.

The equivalent C# CLI is:

```sh
dotnet run --project Podish.PerfTools -c Release -- superopcode train --suite benchmark/superopcode/heavy-v1.json --rootfs artifacts/superopcode/gnu-rootfs --output artifacts/superopcode/standard-training
```

Commands: `build --flavor training|off|pinned|candidate`, `collect`, `distill`,
`train`, `verify --generated ...`, `benchmark --generated ... --repeat 7`, and
`search --plan ... --stage prepare|screen|confirm|validate`.

### Deterministic generation as a native-build dependency

Use an **absolute** manifest path to opt into generation before `BuildNativeLib`:

```sh
dotnet build Fiberish.X86/Fiberish.X86.csproj -c Release -p:SuperOpcodeManifest=/absolute/path/selected.json -p:SuperOpcodeTop=128
```

MSBuild builds the host generator, tracks the manifest/tool/options/project
inputs, emits C++ into the isolated intermediate directory and passes its path to CMake. The
generator performs no guest execution and accesses no network assets. CMake
excludes the default table from its source glob and compiles exactly the selected
file. A missing requested table is an error, not a silent fallback. Standalone
CMake can use `-DFIBERCPU_GENERATED_SUPEROPCODES_FILE=/absolute/table.cpp`.

For separate generation without compilation:

```sh
dotnet msbuild build/SuperOpcodes.proj -t:GenerateFromPinnedProfile -p:CandidateManifest=/absolute/path/selected.json
```

## Data and distillation contract

`heavy-v1.json` fixes the seed, 8 MiB input, seven GNU workload cases, iteration
count, timeout and acceptance thresholds. It is sampled **before** lighter
inputs. The lighter trials use identical input-generation rules at 128 KiB,
512 KiB and 1 MiB; this is a measured distillation, not a guessed synthetic ISA
loop. All tar/gzip/sed/grep families remain represented.

- Normalize each workload's pair distribution independently, then give each
  workload equal weight. Raw counts are preserved separately.
- Select the smallest measured size whose heavy top-128 hot-pair presence mass
  is at least 95%, top-128 set overlap at least 80%, and Jensen-Shannon divergence
  at most 0.12 bits. Presence mass alone is insufficient; startup edges can appear
  in every size with very different frequencies.
- Report all trials, including rejected ones; never relax thresholds after seeing
  a failure. A policy change is a new training experiment.
- Rank legal families by normalized score with an ordinal pair-name tie-break.
  Currently select up to 128 two-op candidates using a conservative family policy.
  Scores estimate opportunity, **not additive realized dispatch savings**: adjacent
  pairs can overlap and handwritten fusion has priority.
- The locked manifest, not timing results or host addresses, drives code generation.
  UTF-8/LF and content hashes keep generation independent of the input filename.

The exact counter is defined as an executed ordinary `Continue` edge from a real
decoded op to the next real op in the same dispatch block. It includes entering
a second instruction which later faults; it does not claim both instructions
retired. It excludes branch-target transitions, sentinels and skipped-op edges.
Counters are per-engine, cumulative across `Run`, and survive code-cache eviction.
The profile covers the **whole guest invocation, including Bash/loader startup**;
the benchmark marker interval covers the command loop. These are distinct scopes.

Training builds reject superopcodes ON at CMake configure time. Profiling is
compiled out in OFF/pinned/candidate performance builds. The native module exports
its feature bits and selected source SHA256; every measured invocation checks
them, so an old DLL cannot masquerade as a new table. Native and all managed
`bin/obj` directories are isolated by `SuperOpcodeFlavor`; the runner explicitly
copies the selected DLL rather than trusting `PreserveNewest`. Standard workflows
hold an exclusive lease to avoid overwriting live native modules.

## Integrity, caching and evidence

Output is under the chosen artifact directory: profiles, sample JSON, GNU version
banners, scripts/transcripts, `distillation.json`, `distilled-suite.json`,
`selected.json`, generated C++, correctness TRX and performance JSON/Markdown.
Native/managed runtime hashes, collector source hash, rootfs lock and suite hash
prevent reuse of incompatible cached profiles. An incompatible existing profile
is an error: choose a new experiment directory instead of silently mixing data.

Tar members and extracted bytes are validated by .NET's independent tar reader;
gzip output is independently decompressed; sed/grep outputs are compared to
managed expectations. Nonzero exits, timeouts, wrong flavor, unresolved handlers,
empty profiles and incorrect outputs abort the pipeline. Generated candidates
must also pass OFF/ON instruction-test comparison. The existing six reviewed x87
failures remain ordinary failing tests; the pipeline explicitly verifies their
identity and compares every row/message. Its report does **not** claim a green ISA
suite.

Performance results retain all samples and report medians, all three variants' MAD and
geometric speedups. The guest command interval includes guest exec/decode and I/O,
but excludes host fixture preparation and pre-marker CLI startup; any JIT during
the command is included. GNU tool checks and output
validation are outside the measured interval; profiling is OFF. Review per-case
regressions as well as aggregate speedup. No noisy shared-runner timing threshold
automatically updates the pinned table.
