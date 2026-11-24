# Reviewed default manifest

## GNU long4x32 v1

[`gnu-long4x32-v1.json`](gnu-long4x32-v1.json) contains the frozen winning recipes in schema 3
from the fourth [selection experiment](../SEARCH_RESULTS.md): **32 base pairs and
32 observed four-op sequences**, 64 patterns total. All patterns use the existing
constituent fault/restart/branch handling; longer candidates match longest-first.
This file is sufficient for generation and does not require old `artifacts/`
profiles, reference sources, GNU packages or network access to build.

On the evaluated Windows x64 host, independent final validation across 35 GNU
workload/corpus cells and seven paired rounds gave **1.0168x / pinned**, with
conditional bootstrap 95% **[1.0076, 1.0271]**. All original gates passed, but
tar-create's family ratio was **0.9956** and the worst small-input cell **0.9662**
(about 3.5% longer runtime). Results do not establish cross-platform gains or a
uniform improvement. On 2026-11-17 the maintainer explicitly requested promoting
this selection to the default, replacing the old 256-pair table. See the
[promotion record](../PROMOTION.md) for integration checks and source identities.

### Build from the repository root (PowerShell)

```powershell
$manifest = (Resolve-Path benchmark/superopcode/manifests/gnu-long4x32-v1.json).Path
dotnet build Podish.Cli/Podish.Cli.csproj -c Release -p:FibercpuBakingPreset=custom -p:SuperOpcodeFlavor=gnu-long4x32-v1 -p:NativeBuildDir=build_native/gnu-long4x32-v1 -p:EnableSuperOpcodes=true -p:EnableSequenceProfile=false -p:EnableHandlerProfile=false -p:EnableBlockProfile=false "-p:SuperOpcodeManifest=$manifest" -p:SuperOpcodeTop=64
```

Native and managed build products are isolated from ordinary/pinned builds. The
CLI output is under
`artifacts/superopcode/builds/gnu-long4x32-v1/managed/Podish.Cli/bin/Release/net10.0/`.
An ordinary build without these options now uses the same long4x32 selection from
the checked-in generated table. The explicit options above are only needed for
an isolated rebuild from the frozen manifest.

For generation only:

```sh
dotnet run --project Podish.PerfTools -c Release -- gen-superopcodes --input benchmark/superopcode/manifests/gnu-long4x32-v1.json --output artifacts/superopcode/gnu-long4x32-v1.generated.cpp --top 64
```

### Frozen identities

- Manifest SHA256:
  `a838808972316d72d6a34154a3ee5ce0c5de166651c74524b1c3899d53536e64`.
- Original experiment generated C++ SHA256:
  `2bf2c2e8cfb5266de7eaf6003da9d1d8515929a0a0f66281cf79893a39ac323c`.
- Promoted default generated C++ SHA256:
  `f5ef132187e8a4f0c69aabd36766ca51417b298b963947c5c7f6fb355c5b2f39`.
  Regeneration against current main adds its new carry-RMW helper include;
  the 64 selected patterns and generated handler/matcher bodies are unchanged.
- Final measured DLL SHA256 (specific to the recorded toolchain/build):
  `dcc8ba78e7ee8eda26621960b29713515f17ee097a707b9a2ebe9c1fb8913bca`.
- Expected native feature bits: **1** (fusion ON; all profiling OFF).

Do not reformat this frozen JSON: its byte hash is embedded in generated-source
identity. Changes need a new version and new correctness/performance evidence.
The selection plan, complete per-cell regressions, environment, test outcomes and
local raw-evidence locations are recorded in [SEARCH_RESULTS.md](../SEARCH_RESULTS.md).
The six existing x87 failures remain failures; a differential match is not a fully
green ISA suite. The performance numbers above describe the original experiment,
not a fresh performance claim for subsequent mainline runtime changes.

The manifest-generation build entry was checked independently after final
validation: 25/25 tool tests and 15/15 native profile/long-sequence checks passed,
with identical generated-source identity and native feature bits 1. The complete
candidate OFF/ON quick-suite comparison had 2,226 identical outcome/message rows,
including the same six failing x87 tests.
