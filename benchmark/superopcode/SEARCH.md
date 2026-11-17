# Iterative selection experiments

This extends the initial [training experiment](RESULTS.md). Its old holdout has
already been inspected and is **not** treated as a fresh test set for tuning.
`search-v1.json` fixes separate training, development and final-validation inputs,
with distinct dataset IDs and seeds. No result promotes a table automatically.
Measurements and rejected strategies are retained in [SEARCH_RESULTS.md](SEARCH_RESULTS.md).

## Reproduce the staged search

The plan's `referenceGenerated` is an explicit dependency on the initial local
128-pair candidate. Preserve that file, or create the baseline first with
`TrainAndVerify` and author a new versioned plan pointing at its output. Missing
baseline files fail; there is no silent substitute. Exact copies of every source
used by the search are saved under its own `candidates/` directory.

```sh
dotnet msbuild build/SuperOpcodes.proj -t:Search -p:TrainingOutput=artifacts/superopcode/search-v3 -p:SearchStage=screen
dotnet msbuild build/SuperOpcodes.proj -t:Search -p:TrainingOutput=artifacts/superopcode/search-v3 -p:SearchStage=confirm
dotnet msbuild build/SuperOpcodes.proj -t:Search -p:TrainingOutput=artifacts/superopcode/search-v3 -p:SearchStage=validate
```

Equivalent CLI: `superopcode search --plan benchmark/superopcode/search-v1.json
--stage screen|confirm|validate --output ...`. `--stage prepare` only collects
training data and generates candidates, without benchmark timings.

Each stage is explicit. Screen evidence cannot be overwritten, plan/runner hashes
must match between stages, and program-list and source hashes are checked before
confirmation/validation. Invalid profile caches fail rather than being relabeled.
Native and managed outputs for each `tune-<strategy>` are isolated, allowing all
candidates to be built **before** interleaved measurements. The workflow lease
prevents another standard workflow from replacing a live module.

## Data changes

All seven GNU workloads remain: tar creation/extraction, gzip compression/
decompression, sed transformations, fixed-string grep and extended-regex grep.
There are four deterministic text generators:

- `records-v1`: the original structured records, preserving its byte-generation
  rules and filter expectations.
- `logs-v2`: timestamped HTTP-like logs, variable field counts, occasional long
  lines and pseudorandom request IDs. Sed also redacts request IDs with a regex;
  grep selects error/status combinations.
- `source-v2`: generated C-shaped declarations/expressions and comments; sed
  edits types and return expressions and grep exercises declaration patterns.
- `entropy-v2`: much less compressible ASCII, 512–1024-character payloads with
  sparse tags. This family is not used for training or development in the first
  plan. It is still synthetic text, **not** an external real-world corpus.

Tar file sizes vary from 16 KiB to 1 MiB, exercising metadata-heavy and bulk-I/O
shapes. The final validation also increases payload size and command iterations.
Independent .NET expectations validate each corpus's sed/grep output, tar bytes
and gzip contents. Small GNU smoke runs test fixture correctness, not candidate
ranking. Changing a filter is a new workload definition, never a looser assertion.

Training collects **8 MiB and 512 KiB** for each records/logs/source family.
Development uses independent records/logs seeds, 2 MiB and two command iterations.
Final validation reserves four independent seeds, 4 MiB and three iterations.
Each split retains every GNU workload; none can omit a regression to improve its
aggregate.

## Rules under comparison

The legacy candidate, OFF and the original 256-pair pinned table remain controls.
Eight alternatives are specified before measuring:

| ID | Rule | Pair budget |
|---|---|---:|
| light128 | Equal normalized workload weight across three 512 KiB corpora | 128 |
| heavy128 | The same rule using three heavy profiles | 128 |
| slope64 | Payload-size differential, smaller table | 64 |
| slope128 | Payload-size differential | 128 |
| slope256 | Payload-size differential, wider coverage | 256 |
| cost128 | Differential score divided by estimated pair cost^0.75 | 128 |
| coverage128 | Greedy diminishing-returns coverage across workload/corpus cells | 128 |
| union320 | Preserve all 256 pinned pairs, append differential-ranked new pairs | 320 |

For pair `p` and workload/corpus cell `c`, the differential rule uses
`max(0, countHeavy[p] - countLight[p]) / (actualHeavyBytes - actualLightBytes)`.
It then normalizes each cell independently and weights cells equally. This is a
finite-difference estimate of payload-related activity, **not an exact exclusion
of Bash/loader execution**. Clipped negative differences remain documented noise;
no missing measurements are invented. Heavy/light iterations are the same.

The cost model is deliberately only a heuristic: explicit loads/stores cost 4,
register-specialized/simple handlers 1, other handlers 3. Pair cost is their sum.
It estimates code/logic complexity, not measured CPU cycles. The experiment can
reject it. Coverage greedily maximizes summed `log(1 + nextMass/(0.02+coveredMass))`
over cells, with ordinal tie breaks. Neither rule claims to solve actual sequence
overlap; edge opportunity is not additive realized dispatch savings.

## Selection and validation contract

1. Screen every strategy for three rotating, round-major samples per cell, plus
   one discarded warmup. Each measured invocation checks payloads and native
   feature/source identities. Screening alone does not certify the ISA suite.
2. Choose the two highest development scores; run complete OFF/ON quick-suite
   differential verification for those exact named native builds. Existing x87
   failures remain failures and are compared explicitly.
3. Confirm both finalists against legacy/pinned/OFF using seven new samples per
   cell. The final candidate is selected **only** from these development results.
4. Persist `decision.json`, including the chosen source hash and development
   metric, **before any final validation timings are executed**. Validation never
   reranks candidates. A second unsealing attempt is rejected.

Each cell reports median/MAD and both the ratio of independent medians and the
median of within-round pinned/candidate log ratios. The objective is the equal-cell
mean of those paired log ratios, penalized by twice the shortfall of the worst
workload-family ratio below 0.97. This disfavors sacrificing one GNU command for
another. An entire poor case is still retained.

Fixed acceptance gates:

- Paired geometric speed ratio against pinned at least **1.015**.
- Conditional paired-bootstrap lower 95% bound strictly above **1.0**.
- Worst workload-family geometric ratio at least **0.97**.
- Worst individual workload/corpus median ratio at least **0.95**.

Bootstrap results resample within-case paired rounds on this host. They do not
cover between-machine variance or establish general application performance.
Three-sample screening is selection-biased; fresh confirmation and locked final
validation are necessary. A failed gate is reported, not lowered after observing
data. If no strategy qualifies, retain the original table and use a **new versioned
experiment** for further work; do not rename the same validation set "unseen".

All stages retain raw samples, scripts, transcripts, profiles, candidate manifests,
native identities, summaries and differential test reports under the experiment
directory. Performance stages do not run concurrently with builds or other tests.

## Second batch: overlap and paired controls

`search-v2.json` explicitly continues after the first batch's failed confirmation.
It retains the same development corpora (already development data), adds the first
batch's heavy128 as a frozen comparator, and reserves **new final-validation seeds**.
The original acceptance gates are unchanged.

- `branch32/64/128` restrict second handlers to Jcc, testing smaller tables of
  compare/test/other ordinary-op→branch fusions.
- `register64` limits the estimated pair cost to at most 4, rather than merely
  penalizing larger handlers in an otherwise unrestricted ranking.
- `overlap64/128` use a first-order edge model. Per cell, node mass is the maximum
  of measured incoming/outgoing Continue-edge mass. For the current selected set,
  12 damped iterations estimate `free[b] = 1 - sum(free[a]*P(a,b)/mass[b])`.
  Next-pair score is its measured mass times free-first probability, discounted
  for an already selected outgoing fusion from its second handler. This discourages
  spending two slots on A→B and B→C when the first fusion consumes B. It is **not**
  measured trigram data or an exact decoder simulation; unit tests only establish
  the intended model behavior.

Measurement now alternates adjacent **pinned/candidate AB and BA** blocks. Every
candidate has its own dedicated pinned invocation in every case/round, recorded
by the `comparison` field; the pinned samples are never shared across candidates.
Each case record includes that candidate's actual `pinnedMedian`. OFF ratios in
this mode are relative to each pair's pinned-normalized result, not falsely
presented as directly adjacent OFF/candidate measurements.

For development only, each guest CLI process is scoped to affinity mask 16
(logical CPU 4 on this Windows host), without changing global affinity, power
settings or process priority. The plan validates that the requested CPU is allowed.
**Final validation uses normal unrestricted affinity**, so a core-pinned-only gain
cannot silently be claimed as the ordinary configuration. Windows/Linux affinity
support is checked explicitly; unsupported requests fail.

The second plan's extra historical comparator is an explicit source-file
dependency, just like the legacy baseline. Preserve all referenced artifacts when
reproducing it, or author a new plan pointing at regenerated baseline sources.

Host preparation now creates only inputs needed by the current command: grep/sed
no longer build unused tar trees and compressed archives. The timed GNU commands,
input bytes and independent output assertions are unchanged. This removes
unnecessary host I/O and background write pressure; it is not counted as a guest
speedup. All seven GNU cases passed a smoke run after this change. New profiles
are collected rather than relabeling older collector identities.

Starting with the second batch, the exact selection/collector/fixture/generator
sources and plan are also copied into the experiment's `provenance/` directory.
Later stages reject source changes. This supplements the hashes and keeps the
uncommitted experiment reproducible as development continues.

## Third batch: density rather than equal edge mass

`search-v3.json` follows the second batch's unsuccessful confirmation. For each
workload/corpus cell it scales the positive heavy-minus-light edge slope by
`actualHeavyBytes / heavyCommandSeconds`, without unit-normalizing the cell.
Candidates are then ranked by equal-cell average opportunity rates. This is a
dispatch-density/Amdahl-inspired heuristic: the timing includes profiling cost,
and the counter covers more startup code than the command timer, so it is **not**
an exact per-pair profitability measurement.

The plan tests density64, density128, densitybranch32 and densityoverlap128 against
legacy and the previously frozen branch32 source. Development uses 8 MiB and one
iteration. Final validation reserves fresh four-corpus inputs plus a smaller
512 KiB dataset; all seven GNU workloads remain in every dataset. CPU affinity and
paired-control policies are unchanged from batch 2. Strictly compatible training
profiles can be copied into a fresh experiment's `profiles/` cache; normal
suite/runtime/native/rootfs/collector checks still run, and metadata is not edited.

## Fourth batch: exact longer sequences

After three unsuccessful two-op batches, `search-v4.json` tests variable-width
generation. It does not infer or fabricate longer counts from pair frequencies.
Native profiling now records actual width-3 and width-4 windows after consecutive
ordinary Continue edges. A tiny per-CPU history is accepted only when the expected
next decoded slot is entered. History is broken at non-Continue paths, sentinels,
new block dispatch, Run/Step boundaries and reset. Cumulative maps survive code-cache
changes; no TLS/global counter is used. The last entry may fault, just as with the
original pair counter: this measures dispatch entry, not complete retirement.

These are adjacency windows in the **actual dispatch stream**. A guest jump that
has been elided by block concatenation is not an intervening handler. Profiling
and fusion remain mutually exclusive, and performance builds contain no ngram
history/counter updates. JSON schema 2 adds `ngrams` and explicit ngram semantics;
both pairs and longer sequences use canonical operation names rather than verbose
compiler signatures. All native/managed/collector cache checks remain strict.

Candidates retain the reviewed 32 base pairs and add 16/32 observed three-op or
four-op patterns. Ranking uses positive heavy-minus-light ngram slope, normalized
by heavy Continue-edge count and multiplied by width-minus-one. The latter is an
opportunity estimate, not additive realized savings: longer matches can overlap.
Raw `weighted_exec_count` remains the actual heavy-run window count. All ordinary
prefixes use the conservative policy; a conditional branch may occur only last.

The new matcher hashes the first two handlers and checks each matching bucket
longest-first, with explicit real-slot/sentinel checks for third/fourth entries.
Every constituent uses the existing fault/restart/branch flow wrapper. The old
two-op generator path remains available; a forced extended-lookup base-only
control measures matcher overhead independently of longer candidates.

Correctness probes are **not a recommended table**:

```sh
dotnet run --project Podish.PerfTools -c Release -- gen-superopcodes --input benchmark/superopcode/sequence-probes.json --output artifacts/superopcode/sequence-probes.generated.cpp --top 32
```

Build `Fiberish.X86.Tests` with that generated source, superopcodes ON, and
`EnableExtendedSuperOpcodeProbes=true` to enforce actual matching assertions in
`LongSequenceFusionTests`. Without that flag the same tests still check CPU
semantics on normal OFF/pinned/candidate builds; they are not skipped.

This batch recollects heavy/light profiles for the new exact counter identity.
Tool, native emitter, ABI and build sources are archived under `provenance/` and
checked again before later stages. All previous negative results remain intact.

The scheduled/manual training workflow also runs the instrumented counter tests
and forces actual width-2/3/4 probe matching and fault recovery before training.
Both TRX reports and the generated probe source are included in its evidence
artifact. These are correctness checks, not shared-runner performance gates;
the workflow does not promote a candidate or claim cross-machine speedups.
