# Default superopcode promotion

Date: 2026-11-17. Explicit maintainer request: replace the default table, commit,
integrate into main and push. This is not automatic benchmark-driven promotion.

## Default selection

`libfibercpu/src/generated/superopcodes.generated.cpp` now uses the frozen
[`gnu-long4x32-v1.json`](manifests/gnu-long4x32-v1.json) selection: **32 base pairs
plus 32 observed four-op patterns**, replacing the previous 256-pair table.
Ordinary builds require no manifest property, training run or guest asset download:

```sh
dotnet build Podish.Cli/Podish.Cli.csproj -c Release
```

Fusion can still be disabled with `-p:EnableSuperOpcodes=false`. Profiling remains
OFF in normal builds; instrumented training still requires fusion OFF.

## Evidence and identities

The [selection report](SEARCH_RESULTS.md) preserves all four batches, including
failed strategies and per-case regressions. Its final independent validation
passed all original gates: paired ratio **1.0168 / old pinned**, bootstrap 95%
**[1.0076, 1.0271]**, worst family **0.9956**, worst cell **0.9662**. These are
single-host results from the original experiment, not fresh performance results
for mainline changes made afterward.

Main advanced by 19 fixes before integration. The training commit was rebased
onto `546283512aee888f0e800dcbb65a496fe1faadb8`; the native Run/Step conflict was
resolved by retaining main's reentrancy guards and cache-flush behavior, together
with the new per-CPU profiling-boundary resets. Main's then-uncommitted application
profiling work was excluded from these commits; all integration edits and builds
were performed in the feature worktree. Main was clean at the final pre-merge
check. This operation did not stash, reset or discard files in the main worktree.

- Reviewed manifest SHA256:
  `a838808972316d72d6a34154a3ee5ce0c5de166651c74524b1c3899d53536e64`.
- Original measured generated C++ SHA256:
  `2bf2c2e8cfb5266de7eaf6003da9d1d8515929a0a0f66281cf79893a39ac323c`.
- Promoted/default generated C++ SHA256:
  `f5ef132187e8a4f0c69aabd36766ca51417b298b963947c5c7f6fb355c5b2f39`.
- Previous 256-pair source SHA256:
  `e440f6ac091a973e55802da1e8cd355923adc13c78becfec09c2cd15cea7fd2c`.

Regenerating on updated main adds exactly one include,
`ops/ops_carry_rmw_impl.h`, relative to the measured C++ file; every generated
handler and matcher body and the selected pattern order remain unchanged.
The checked-in default is reproduced by:

```sh
dotnet run --project Podish.PerfTools -c Release -- gen-superopcodes --input benchmark/superopcode/manifests/gnu-long4x32-v1.json --output libfibercpu/src/generated/superopcodes.generated.cpp --top 64
```

A tooling regression test checks exact text equality against this regeneration
and enforces exactly 32 width-2 and 32 width-4 patterns. Git attributes preserve
LF in the generated default source. Frozen historical artifacts are not rewritten
or relabeled as measurements of the new mainline runtime.

## Integration verification

After integrating current main and replacing the default source:

- Tooling: **26/26 passed**, including default/manifest regeneration equality.
- Instrumented pair/ngram accounting: **7/7 passed**.
- Forced actual width-2/3/4 matching, fault recovery and branch probes:
  **8/8 passed**.
- Complete quick-suite OFF versus default: **3,074 identical outcome/message
  rows**, with **3,068 passed, six known x87 failures, zero skipped** on each side.
  This includes current main's added instruction, SMC and cache-coherence tests;
  the six failures were not relaxed, rewritten or hidden.
- The regular default build uses the checked-in source without a manifest/source
  override; its native feature bits are **1** and its exported source hash is the
  promoted default hash above.

Fresh TRX files, full logs and the explicit differential/identity audit are under
`artifacts/superopcode/default-promotion-v1/`. Historical performance artifacts
remain under `artifacts/superopcode/search-v6/`.

The GNU integration check uses a new
`artifacts/superopcode/gnu-rootfs-mainline-v1/`, prepared from the same pinned
packages. The older rootfs retained a CRLF copy of `packages.lock.json`, while
Git checkout now enforces LF; strict byte-identity validation rejected it before
any sampling. The old rootfs and evidence were not modified, and no hash check
was relaxed.

The old 256-pair comparison source is retained verbatim in the promotion artifacts.
To compile a copy outside `src/generated/`, only its relative include prefixes
were relocated (`../ops/...` to `ops/...`, and equivalent header paths). All
handler/matcher bodies are byte-identical after excluding include lines. The
relocated source SHA256 is
`e8565200bf7403f7421f94af763776d6052fcd76ea4e59958723f59dbeee40ca`;
the original source hash remains recorded above. The unsuccessful pre-sampling
setup/build logs are retained separately, not counted as measured invocations.

### GNU application-path sanity check on updated main

All seven GNU workloads completed with independently validated outputs across
OFF, the new default, and the relocated old 256-pair table: **63 measured
invocations and 21 discarded warmups** (4 MiB records, two command iterations,
three timing rounds). Every native feature/source identity matched; all modules
were archived under `gnu-integration-relocated/native-evidence/`.

These are **three-round integration measurements on already-seen data**, not a
new independent validation or a claim that the old acceptance gates were passed
again. The default/old median-ratio geometric mean is **1.0270**, but tar-extract
has a substantial **0.8556** ratio (about **16.9% longer runtime**), and grep-fixed
is slightly worse. The tar-extract default MAD is also large; the observed
regression is retained rather than dismissed as noise or hidden by the mean.
Promotion is the explicit maintainer decision above, not an assertion of uniform
speedups after mainline runtime changes.

| Workload | OFF median +/- MAD | Default median +/- MAD | Old 256 median +/- MAD | Default / old speed ratio |
|---|---:|---:|---:|---:|
| tar-create | 0.5726 +/- 0.0028 | 0.6143 +/- 0.0392 | 0.6487 +/- 0.0472 | 1.0561 |
| tar-extract | 0.6971 +/- 0.0166 | 0.8006 +/- 0.0778 | 0.6850 +/- 0.0014 | **0.8556** |
| gzip-compress | 1.6190 +/- 0.0041 | 1.6149 +/- 0.0080 | 1.6502 +/- 0.0257 | 1.0219 |
| gzip-decompress | 0.5926 +/- 0.0147 | 0.5117 +/- 0.0038 | 0.6172 +/- 0.0053 | 1.2061 |
| sed-transform | 3.4151 +/- 0.0419 | 3.2689 +/- 0.1111 | 3.4862 +/- 0.0653 | 1.0665 |
| grep-fixed | 0.1881 +/- 0.0005 | 0.1855 +/- 0.0004 | 0.1851 +/- 0.0013 | 0.9976 |
| grep-regex | 0.1838 +/- 0.0012 | 0.1794 +/- 0.0041 | 0.1825 +/- 0.0011 | 1.0172 |

The raw benchmark's historical column names are `pinned` = **new default** and
`candidate` = **old 256-pair control**, so its candidate/pinned ratios must not be
mistaken for default/old speedups. `gnu-integration-audit.json` explicitly records
the direction above and checks every output, median/MAD and identity. No timing
sample or failing correctness assertion was removed.
