# GNU superopcode training and performance report

Date: 2026-11-16. Branch: `perf/superopcode-training`, based on `de6eef2`.
The pinned source table is unchanged; generation does not promote a candidate.

**Outcome:** the workflow is implemented and exercised end to end. The final
candidate's observed geometric speedup is **1.0100× versus pinned**, but the first
run was only 1.0027×, several differences reverse direction, and regex grep
regresses. This is not sufficient evidence to replace the pinned table.

## Machine and measurement contract

- Host: Windows 11 (`10.0.22631`), x64, AMD Ryzen AI MAX PRO 390,
  12 cores / 24 logical processors.
- SDK/runtime: .NET SDK 11.0.100, .NET 11.0.0 running `net10.0` binaries.
- Native: clang-cl 22.1.3, Release, LTO ON, PGO OFF.
- Guests: Alpine i386/musl; GNU tar 1.35, gzip 1.13, GNU sed 4.9,
  GNU grep 3.11, Bash 5.2.26. The 22 pinned packages contain **no BusyBox**.
  Package SHA256 and all 250 prepared rootfs files are locked and validated.
- OFF disables both handwritten and generated fusion. Pinned uses the existing
  256 generated pairs; candidate uses 128 distilled pairs. The two ON variants
  retain the same handwritten patterns. All performance profiling switches are OFF.
- Holdout: `holdout-v1.json`, independent seed 987654, approximately 4 MiB text,
  two command iterations per sample; seven samples plus one discarded warmup per
  workload/variant, with rotating variant order. All samples are retained.
- Times are host-observed guest stdout-marker intervals, including guest
  exec/decode, I/O, the Bash loop and any in-interval JIT. They exclude host fixture
  preparation and pre-marker CLI startup, not all possible managed JIT work.
  Training counters have a different scope: the whole invocation, including
  Bash/loader startup. No instruction-counter result is reported as wall time.
- Tar payloads/extracted bytes, gzip decompression, sed transformations and grep
  counts are independently validated in C#. A timing sample with a wrong output,
  wrong native feature bits or mismatched generated-source SHA256 aborts the run.

## What the heavy profile taught us

The input generator emits deterministic records containing numeric item IDs,
ERROR/OK status, sparse `needle` tags and pseudorandom hexadecimal values. Tar
operates on 64 KiB files; gzip compresses/decompresses the records; sed performs two
substitutions; grep exercises fixed-string and extended-regex counting.

The 8 MiB heavy profile showed substantially different interpreter-edge budgets:
approximately 624 million for gzip compression, 300 million for sed, but only
3.5–3.7 million for tar. Pooling raw counts would let gzip dictate the table.
Instead, each of the seven workloads receives equal normalized weight.

Recurring hot shapes include compare/test→conditional branch, gzip's integer
shifts/XOR and byte loads, sed's adjacent loads/byte comparisons, and grep's
NOT→AND→mask scanning sequences. These observations drive measured smaller
versions of the same GNU cases, **not hand-selected synthetic instruction loops**.

Distillation fixes its thresholds before measuring: heavy top-128 presence mass
≥95%, top-128 set overlap ≥80%, Jensen–Shannon divergence ≤0.12 bits. It retains
all seven workloads and chooses the smallest passing size. Presence alone is not
sufficient: even 128 KiB contains all hot shapes but overweights startup paths.

## Exploratory run (older collector; all evidence retained)

Evidence: `artifacts/superopcode/experiment-v1/`.

- Heavy command intervals summed to 10.663 s. The exploratory collector did not
  yet record collection wall time; this is **not** the full collection duration.
- 128 KiB failed (64.84% overlap, JS 0.12543); 512 KiB passed (80.47%,
  JS 0.05021); 1 MiB passed (85.94%, JS 0.02818). Presence mass was 100%.
- Selected 512 KiB; summed command intervals 1.102 s versus 10.663 s heavy.
- The first holdout run measured candidate/pinned geometric speedup of **1.0027×**,
  essentially flat, with both wins and regressions:

| Workload | OFF median s | Pinned median s | Candidate median s | Pinned / candidate |
|---|---:|---:|---:|---:|
| tar create | 0.6098 | 0.6219 | 0.6254 | 0.9944× |
| tar extract | 0.7114 | 0.7184 | 0.7436 | 0.9661× |
| gzip compress | 1.5948 | 1.5919 | 1.5418 | 1.0325× |
| gzip decompress | 0.5163 | 0.5156 | 0.5180 | 0.9953× |
| sed transform | 3.4127 | 3.3899 | 3.2852 | 1.0319× |
| grep fixed | 0.1864 | 0.1797 | 0.1826 | 0.9842× |
| grep regex | 0.1770 | 0.1802 | 0.1772 | 1.0165× |

The final collector adds managed-package/source cache identities, collection wall
time, separate raw counts and scores, and all three variants' MAD. It intentionally
rejects the older cached profiles. The exploratory records are preserved rather
than edited into the new schema.

## Frozen-collector training run

Evidence: `artifacts/superopcode/experiment-v2/`. The actual standard entry point
was `dotnet msbuild build/SuperOpcodes.proj -t:TrainAndVerify
-p:TrainingOutput=artifacts/superopcode/experiment-v2`.

| Input per case | Top-128 overlap | JS bits | Presence mass | Accepted | Command intervals, sum s | Collection wall s |
|---|---:|---:|---:|---|---:|---:|
| Heavy 8 MiB | reference | reference | reference | reference | 10.2624 | 16.6427 |
| 128 KiB | 64.8438% | 0.12542 | 100% | no | 0.6416 | 5.7633 |
| **512 KiB** | **80.4688%** | **0.05020** | **100%** | **selected** | **1.1095** | **6.2986** |
| 1 MiB | 85.9375% | 0.02816 | 100% | yes | 1.7188 | 7.0195 |

The selected input is 16× smaller. Its summed command intervals are 9.25× shorter,
but its full collection wall time is only 2.64× shorter because startup, fixture
creation, version checks and export/validation remain. Collection wall time
excludes the preceding native build. The complete train/distill/build/verify
invocation took **108.815 s**, reusing existing toolchain/build directories; this
is not a clean-machine provisioning time. No GNU asset download was included.

The resulting manifest contains 128 pairs, with actual raw edge counts and
separate equal-workload normalized scores. OFF/candidate verification compared
**2,216 rows** with identical outcomes/messages: **2,210 passed, six failed,
zero skipped** in each variant. The six failures remain the reviewed x87 cases
`D9 F2/F3/FB/FE/FF/F9`, not new fusion regressions.

## Final independent-seed holdout

Evidence: `artifacts/superopcode/experiment-v2/benchmark/`. The actual command was
`dotnet msbuild build/SuperOpcodes.proj -t:Benchmark
-p:TrainingOutput=artifacts/superopcode/experiment-v2`.
No other builds/tests were run concurrently with the measurement. All **147
measured invocations** and **21 discarded warmup invocations** passed independent
output validation. Native feature bits were 0 for OFF and 1 for both ON variants;
all three profiling switches were OFF.

Values below are **median ± median absolute deviation**, in seconds for two
iterations. MAD describes spread, not a confidence interval. A ratio above 1
means the candidate was faster.

| Workload | OFF s | Pinned s | Candidate s | Pinned / candidate | OFF / candidate |
|---|---:|---:|---:|---:|---:|
| tar create | 0.5918 ± 0.0091 | 0.5980 ± 0.0105 | 0.5918 ± 0.0171 | 1.0104× | 1.0000× |
| tar extract | 0.6879 ± 0.0154 | 0.7607 ± 0.0899 | 0.7112 ± 0.0166 | 1.0696× | 0.9673× |
| gzip compress | 1.6239 ± 0.0233 | 1.5912 ± 0.0063 | 1.5420 ± 0.0148 | 1.0319× | 1.0531× |
| gzip decompress | 0.5213 ± 0.0079 | 0.5248 ± 0.0107 | 0.5287 ± 0.0230 | 0.9927× | 0.9861× |
| sed transform | 3.5496 ± 0.0280 | 3.2864 ± 0.0387 | 3.2369 ± 0.0348 | 1.0153× | 1.0966× |
| grep fixed | 0.1837 ± 0.0051 | 0.1828 ± 0.0035 | 0.1830 ± 0.0009 | 0.9988× | 1.0037× |
| grep regex | 0.1788 ± 0.0043 | 0.1777 ± 0.0044 | 0.1861 ± 0.0036 | 0.9549× | 0.9610× |

Equal-case geometric speedup: **1.009998× versus pinned**, **1.008688× versus OFF**.
These are descriptive ratios, not a statistical-significance claim.

- Gzip compression is the clearest repeatable improvement versus pinned: about
  3.1–3.2% lower elapsed time in both experiments.
- Sed improved in both experiments, but the final reduction versus pinned is
  only 1.5%. Its 9.7% speed ratio improvement versus OFF must not be confused with
  the incremental gain over the existing pinned implementation.
- Final tar extraction looks 6.5% lower-time than pinned, yet is 3.4% higher-time
  than OFF. Pinned MAD is 11.8% of its median, and the earlier run favored pinned
  over candidate. It is not evidence of a stable 7% optimization.
- Regex grep is **4.7% higher-time than pinned** in the final run; the earlier run
  had the opposite sign. Fixed grep is essentially flat. Gzip decompression is
  slightly slower. These results are retained, not hidden by the aggregate.

Hot-edge similarity successfully reduces collection cost; it does **not** prove
that a pair set is profitable. Pair overlap, existing handwritten patterns,
lookup/code-size effects and host noise can offset dispatch savings. This
experiment does not isolate their individual contributions. The candidate remains
an artifact for further testing; the repository's original table is retained.

### Artifact identities (SHA256)

| Item | SHA256 |
|---|---|
| Selected manifest | `d08ec71cadc4a13764bf90fac25005b1b794074a127ac70e6d399606c26ea36f` |
| Generated candidate source | `d70d87aae61dbf81e32c427abe6df655e015b9b41b98ea8eb4a0328fbda507e0` |
| Pinned source | `e440f6ac091a973e55802da1e8cd355923adc13c78becfec09c2cd15cea7fd2c` |
| Training native DLL | `00b90313bff9906f261d4175b9ed42f50df2f72498c4068b11760902e7c71570` |
| OFF native DLL | `bbd93acf4fff4c842a499e2b2a4a8ce4e49883ffb57aefab4a9445ed98972f26` |
| Pinned native DLL | `546af73f9417d50a4c51afe5934193cb46da61e9ae04af55e78e712c3261047b` |
| Candidate native DLL | `9a0c078ca20d2fa02e7d1dbaded69aad55350c96514a4bdb3f9f983d3f082a45` |
| Collector source | `0e97de03d6a18b061b56d166e1e932d8b6ed098947364b478d72e60523501e13` |

Suite/rootfs hashes, managed runtime-package identity and raw counts are retained
in the profiles and benchmark JSON. These hashes identify this local experiment;
they do not promise cross-machine reproducible native DLL bytes.

## Correctness and build integration

- Tooling unit tests: 15 passed, none failed/skipped.
- Instrumented sequence tests: 5 passed, none failed/skipped; the actual loaded
  test DLL was checked to export feature bits `2`. Cases cover cumulative counts,
  cache invalidation, faulting second ops, branch/step boundaries, reset and
  concurrent CPU-instance isolation.
- Direct `SuperOpcodeManifest` build tested with absolute paths. Relative paths
  fail explicitly. An unchanged second build preserves generated/native timestamps.
  Changing `SuperOpcodeTop` 128→127→128 changes/restores source hashes and the DLL
  always exports the matching hash.
- This test exposed and fixed stale handler reflection on a changed candidate set:
  the CMake custom command now depends on the actual core object files, not just
  the object-library ordering target. Evidence: `artifacts/superopcode/build-integration.json`.
- Re-running distillation with the current profiles reused all four collections
  without guest recollection and emitted identical candidate bytes. The older
  profiles were explicitly rejected as incompatible rather than silently reused.
- GNU payload validation is in addition to, not a replacement for, the full quick
  instruction-suite OFF/ON comparison. The six pre-existing x87 failures are not
  skipped or rebaselined; the native test command still exits red. The workflow
  checks their identities and compares every result/message, reporting separately
  that the full suite is not green.

## Scope limits

This is one Windows x64 host and one structured-text fixture family, not a broad
application benchmark. Tar is particularly noisy and metadata/I/O-sensitive;
small percentage differences must not be treated as significant without further
runs. Independent-seed holdout prevents exact training-input reuse but does not
test unrelated corpora. Linux, macOS, ARM64, wasm, AOT and remote CI have not been
validated in this experiment. No automatic performance threshold promotes a set.

Reproduction commands and artifact contracts: [README.md](README.md).
