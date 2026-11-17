# Iterative GNU selection results

Date: 2026-11-16. Same Windows x64 host/toolchain as [RESULTS.md](RESULTS.md).
Protocol and predeclared gates: [SEARCH.md](SEARCH.md). The original pinned table
remains unchanged. Earlier performance runs are development evidence now, not
fresh holdouts.

**Outcome:** the fourth batch's frozen **long4x32** table passed development
confirmation and independent final validation. Final paired speed ratio / pinned:
**1.0168**, conditional bootstrap 95% **[1.0076, 1.0271]**. A byte-identical,
opt-in [manifest](manifests/gnu-long4x32-v1.json) and [build instructions](manifests/README.md)
are available; the default pinned table is unchanged. This is evidence for this
GNU suite on one Windows x64 host, not a universal performance claim.

## Batch 1: multi-corpus and payload-differential rules

Evidence: `artifacts/superopcode/search-v3/`.
Plan: `search-v1.json`, SHA256
`c37f1e55bc85b3d69c69697e2a43af3b53b12933b3785082159b15976d36eb55`.

All three added corpus families passed GNU smoke execution of all seven cases,
with independent output validation. Tool unit tests: 21 passed, none failed/skipped.
Training collected 8 MiB and 512 KiB for records, variable-length logs and C-shaped
source text, giving 42 workload invocations. Collection wall times in seconds:

| Corpus | Heavy collection | Light collection |
|---|---:|---:|
| records | 16.932 | 6.491 |
| logs | 24.149 | 6.758 |
| source | 21.424 | 6.577 |

### Three-round development screen

There were 462 measured invocations and 154 discarded warmup invocations across
14 workload/corpus cells and 11 variants. Every invocation checked its output and
native source/feature identity. No timing samples were removed as outliers.

| Variant | Pairs | Median-ratio GM / pinned | Paired GM / pinned | Conditional bootstrap 95% | Worst family | Worst cell | Accepted |
|---|---:|---:|---:|---|---:|---:|---|
| legacy | 128 | 1.0260 | 1.0148 | [0.9727, 1.0967] | 0.9743 | 0.9613 | no |
| light128 | 128 | 1.0175 | 0.9999 | [0.9722, 1.0878] | 0.9510 | 0.9369 | no |
| heavy128 | 128 | 1.0046 | 1.0063 | [0.9701, 1.0929] | 0.9469 | 0.8708 | no |
| slope64 | 64 | 1.0210 | 1.0119 | [0.9701, 1.0975] | 0.8960 | 0.7507 | no |
| slope128 | 128 | 0.9993 | 0.9892 | [0.9551, 1.0794] | 0.8638 | 0.7044 | no |
| slope256 | 256 | 0.9930 | 0.9900 | [0.9481, 1.0758] | 0.8846 | 0.7514 | no |
| cost128 | 128 | 0.9957 | 0.9927 | [0.9532, 1.0841] | 0.8973 | 0.8074 | no |
| coverage128 | 128 | 0.9736 | 0.9575 | [0.9378, 1.0693] | 0.7615 | 0.6340 | no |
| union320 | 320 | 0.9754 | 0.9642 | [0.9396, 1.0742] | 0.8493 | 0.7489 | no |

The ranking objective also penalizes worst-family regressions, so the two new
finalists selected for confirmation are **light128** and **heavy128**, not simply
the two largest unpenalized mean ratios. The legacy candidate remains a control.

The screen is visibly noisy. For example, the *same pinned build* on log sed
measured 4.0197, 2.1849 and 1.8107 seconds across rounds. Thus a short screen cannot
support a performance claim merely because its aggregate exceeds 1.0. Conversely,
the regressions are not erased or attributed entirely to noise: the broader tables
and payload-slope heuristics have not demonstrated superiority on these data.
The next stage uses seven fresh rounds and full differential ISA verification.

Final-validation timings have not been used to select or adjust these rules.

### Seven-round confirmation

The exact `tune-light128` and `tune-heavy128` builds each passed the full OFF/ON
quick-suite differential comparison: 2,216 identical outcome/message rows,
2,210 passed, the same six pre-existing x87 failures, none skipped. Evidence is
under `search-v3/verification/`. Confirmation measured 490 invocations plus 70
discarded warmups. No strategy passed the original gates:

| Variant | Median-ratio GM / pinned | Paired GM / pinned | Conditional bootstrap 95% | Worst family | Worst cell |
|---|---:|---:|---|---:|---:|
| heavy128 | 1.0028 | 1.0054 | [0.9831, 1.0165] | 0.9880 | 0.9517 |
| light128 | 1.0021 | 1.0038 | [0.9900, 1.0193] | 0.9756 | 0.9716 |
| legacy | 1.0070 | 1.0030 | [0.9919, 1.0174] | 0.9873 | 0.9771 |
| OFF | 0.9958 | 0.9962 | [0.9772, 1.0074] | 0.9745 | 0.9598 |

Thus batch 1 is a documented negative result, not an optimization success. Its
reserved validation was **not unsealed**. The next batch changes both the rule
family (overlap/branch/register specialization) and measurement design, under a
new versioned plan; it does not silently rerun the same screen until it looks good.

## Batch 2: overlap, branches and smaller tables

Evidence: `artifacts/superopcode/search-v4/`; plan `search-v2.json`. Development
uses adjacent dedicated pinned/candidate AB/BA pairs and child-process affinity
mask 16. Final validation, if run, uses unrestricted affinity. The experiment also
archives exact tool sources under `provenance/`. Tool tests: 22 passed, none failed
or skipped. The selective-input preparation change passed all seven GNU smoke
cases; timed commands and payload assertions were not weakened.

### Three-round paired screen

There were 756 measured invocations and 140 discarded warmups, across 14 cells,
eight generated/reference programs, OFF and pinned. All outputs and native build
identities passed validation. These are screening results, not a final claim:

| Variant | Pairs | Median-ratio GM / pinned | Paired GM / pinned | Conditional bootstrap 95% | Worst family | Worst cell | Provisional gates |
|---|---:|---:|---:|---|---:|---:|---|
| branch32 | 32 | 1.0215 | 1.0293 | [1.0093, 1.0759] | 0.9855 | 0.9673 | pass |
| legacy | 128 | 1.0273 | 1.0199 | [0.9910, 1.0395] | 1.0035 | 0.9956 | fail |
| overlap128 | 128 | 1.0214 | 1.0192 | [0.9873, 1.0586] | 0.9827 | 0.9593 | fail |
| register64 | 64 | 1.0258 | 1.0171 | [0.9968, 1.0291] | 0.9911 | 0.9900 | fail |
| heavy128r | 128 | 1.0215 | 1.0042 | [0.9815, 1.0351] | 0.9772 | 0.9551 | fail |
| overlap64 | 64 | 0.9990 | 1.0040 | [0.9871, 1.0376] | 0.9793 | 0.9735 | fail |
| branch128 | 128 | 1.0041 | 1.0018 | [0.9658, 1.0475] | 0.9744 | 0.9263 | fail |
| branch64 | 64 | 0.9943 | 0.9917 | [0.9670, 1.0421] | 0.9585 | 0.9511 | fail |
| OFF | — | 0.9959 | 1.0015 | [0.9757, 1.0340] | 0.9383 | 0.9283 | fail |

The fixed ranking selects **branch32** and **overlap128** for full quick-suite
verification and seven fresh confirmation rounds. Branch32's generated source
SHA256 is `bfc91cbefedd69442b7af378354f263bca1f365affe4bcad7f56f3d68614afa6`;
overlap128 is `c019dd3d54df941f8785daa2c289879f3eb0b15a478154407521cc939ef5e7c6`.

Branch32 is promising but not uniformly faster: record/log sed paired ratios are
0.9881/0.9867. Its mean also benefits from noisy tar cases. Overlap128 instead
shows stronger sed results (1.0707/1.0556), with some extraction/decompression
regressions. Confirmation must distinguish these tradeoffs from screening noise.

### Seven-round paired confirmation

Both named builds passed the full OFF/ON quick-suite comparison (2,216 identical
rows, with the six existing x87 failures). The fresh confirmation contained 784
measured invocations and 70 discarded warmups. It did not reproduce the screen's
apparent 2.9% gain:

| Variant | Median-ratio GM / pinned | Paired GM / pinned | Conditional bootstrap 95% | Worst family | Worst cell | Accepted |
|---|---:|---:|---|---:|---:|---|
| branch32 | 1.0108 | 1.0096 | [0.9929, 1.0296] | 0.9901 | 0.9600 | no |
| overlap128 | 0.9932 | 1.0103 | [0.9839, 1.0277] | 0.9520 | 0.9346 | no |
| legacy | 0.9860 | 0.9960 | [0.9776, 1.0164] | 0.9568 | 0.9451 | no |
| OFF | 0.9819 | 0.9802 | [0.9655, 0.9977] | 0.9059 | 0.8726 | no |

Branch32 is a smaller, plausible alternative, but this is still insufficient for
promotion. Overlap128's point estimate depends strongly on the statistic and has
regression-gate failures. Its higher paired mean does not erase those failures.
Batch 2 validation was **not unsealed**.

## Batch 3 protocol: wall-time opportunity density

Plan: `search-v3.json`; evidence directory `artifacts/superopcode/search-v5/`.
This batch tests four new combinations: density-ranked 64/128 pairs, density-ranked
32 branch pairs, and density plus overlap at 128 pairs. It retains legacy and the
frozen branch32 candidate as controls. Unit tests: 23 passed, none failed/skipped.

The new score scales positive payload slope by heavy input bytes / instrumented
command seconds. Unlike equal-cell unit normalization, it does not allocate as
much budget to an I/O-dominated tar case with few dispatches per second. This is
an opportunity heuristic using instrumented time, not a measured marginal saving
or a CPU-cycle model. Existing profiles are reused only because collector,
runtime, native, suite and rootfs identities pass the unchanged strict checks.

Development changes to larger 8 MiB single-command invocations while retaining all
seven workloads and both corpora. This is a new, explicitly versioned test set,
not a rerun renamed "independent". Adjacent controls and development-only affinity
remain. Reserved validation has fresh seeds across all four corpora **plus a
512 KiB case family**, so a bulk-only improvement cannot silently stand in for
all input sizes. All original acceptance gates remain fixed.

### Three-round bulk-development screen

There were 588 measured invocations plus 112 warmups. Strict profile identities
allowed reuse of the previous batch's six training collections; there was no
metadata relabeling. Every GNU output and native source/feature identity passed.
Native binaries used in this screen are additionally preserved under
`search-v5/screen/native-evidence/`, with their hashes in `native-evidence.json`.

| Variant | Pairs | Median-ratio GM / pinned | Paired GM / pinned | Conditional bootstrap 95% | Worst family | Worst cell | Accepted |
|---|---:|---:|---:|---|---:|---:|---|
| density128 | 128 | 1.0269 | 1.0154 | [0.9795, 1.0349] | 0.9857 | 0.9570 | no |
| densityoverlap128 | 128 | 1.0132 | 1.0134 | [0.9943, 1.0288] | 1.0000 | 0.9760 | no |
| densitybranch32 | 32 | 1.0129 | 1.0111 | [0.9999, 1.0282] | 0.9827 | 0.9642 | no |
| density64 | 64 | 1.0083 | 1.0111 | [0.9833, 1.0299] | 0.9826 | 0.9734 | no |
| branch32r | 32 | 1.0178 | 1.0144 | [0.9972, 1.0486] | 0.9623 | 0.8962 | no |
| legacy | 128 | 0.9917 | 1.0098 | [0.9724, 1.0301] | 0.9542 | 0.9007 | no |
| OFF | — | 0.9850 | 0.9832 | [0.9691, 1.0045] | 0.9541 | 0.9281 | no |

The fixed score selects density128 and densityoverlap128 for full ISA verification
and seven new confirmation rounds. The former's 2.7% independent-median headline
must not replace its smaller paired estimate or conceal its inconclusive interval.

### Seven-round bulk-development confirmation

Both finalists passed full OFF/ON differential verification. In 784 measured
invocations plus 70 warmups, no rule passed the original gates:

| Variant | Median-ratio GM / pinned | Paired GM / pinned | Conditional bootstrap 95% | Worst family | Worst cell | Accepted |
|---|---:|---:|---|---:|---:|---|
| densityoverlap128 | 1.0111 | 1.0087 | [0.9984, 1.0260] | 0.9805 | 0.9740 | no |
| density128 | 0.9944 | 1.0001 | [0.9882, 1.0113] | 0.9779 | 0.9600 | no |
| legacy | 1.0088 | 1.0035 | [0.9903, 1.0206] | 0.9806 | 0.9520 | no |
| OFF | 1.0027 | 1.0059 | [0.9869, 1.0121] | 0.9780 | 0.9691 | no |

The reserved batch 3 validation was **not unsealed**. This completes three batches
of two-op selection experiments without a reliable promotion candidate. The next
batch changes fusion width rather than presenting another small two-op point
estimate as a success.

## Batch 4 preparation: observed 3/4-op sequences

The original plan was to estimate longer candidates from pair transitions. Instead,
the collector has been extended to record **actual** width-3 and width-4 dispatch
entry windows, so no estimated frequency is presented as a measured count.
New native/profile identities intentionally invalidate older caches.

- Instrumented counter tests: **7 passed**. They check cumulative 3/4-gram counts,
  CPU-instance isolation, reset/cache lifetimes, fault entry versus unexecuted
  tails, and real taken-branch boundaries. A `JMP +0` fixture was replaced with a
  genuinely taken Jcc because native block concatenation can legitimately elide
  the former; the zero-cross-boundary assertion was retained.
- Forced matching/semantics probes: **8 passed** with an opt-in probe table. They
  assert that the actual root handler is width 2/3/4, verify longest-first and
  shorter-prefix fallback, test faults in the second/third/fourth op, ensure
  resumption does not replay committed prefixes, and exercise a conditional
  fourth op. These are additional checks, not relaxed output assertions.
- Tool tests: **25 passed**. All seven GNU smoke workloads also exported nonempty
  exact width-3 and width-4 profiles with canonical handler names.

`search-v4.json` keeps the reviewed branch32 base, adding 16 or 32 observed width-3
or width-4 patterns. A base-only extended-matcher control separates matcher changes
from longer-pattern opportunity. The same fresh confirmation and reserved
validation discipline applies; the pinned table is still unchanged.

The mixed-width probe table also completed full OFF/ON quick-suite verification:
**2,226 identical outcome/message rows**, retaining only the same six x87 failures.

### Three-round long-sequence screen

Evidence: `artifacts/superopcode/search-v6/`. This uses fresh schema-2 training
profiles; earlier pair-only caches were not reused. There were 672 measured
invocations and 126 warmups, all with validated outputs and native identities.
Exact screen binaries are retained under `screen/native-evidence/`.

| Variant | Total patterns | Median-ratio GM / pinned | Paired GM / pinned | Conditional bootstrap 95% | Worst family | Worst cell | Accepted |
|---|---:|---:|---:|---|---:|---:|---|
| long4x16 | 48 | 1.0350 | 1.0230 | [0.9717, 1.0613] | 0.9781 | 0.8867 | no |
| long4x32 | 64 | 1.0256 | 1.0220 | [0.9804, 1.0605] | 0.9695 | 0.9079 | no |
| long3x32 | 64 | 1.0302 | 1.0202 | [0.9929, 1.0768] | 0.9446 | 0.8966 | no |
| branch32r | 32 | 1.0135 | 1.0364 | [0.9911, 1.0763] | 0.9246 | 0.8648 | no |
| long3x16 | 48 | 1.0097 | 1.0187 | [0.9719, 1.0947] | 0.9194 | 0.8884 | no |
| longcontrol | 32 | 0.9923 | 1.0006 | [0.9463, 1.0461] | 0.9011 | 0.7959 | no |
| legacy | 128 | 0.9913 | 1.0048 | [0.9639, 1.0483] | 0.8548 | 0.7503 | no |
| OFF | — | 1.0221 | 1.0152 | [0.9546, 1.0548] | 0.9494 | 0.8634 | no |

The fixed regression-penalized score selects long4x16 and long4x32 for full
verification and seven fresh confirmation rounds. Their shared hottest observed
pattern is NOT→AND→mask→Jcc (14,612,156 entries in the heavy training runs), followed
by related load/address/mask scanning sequences. No frequency was inferred from
pair products. Broad intervals and double-digit cell regressions still prevent a
performance claim from the short screen alone.

### Seven-round long-sequence confirmation

Both finalists passed the complete OFF/ON quick-suite differential comparison:
**2,226 identical outcome/message rows**, 2,220 passed, the same six pre-existing
x87 failures, none skipped. The fresh confirmation measured 784 invocations plus
70 discarded warmups. Exact binaries and hashes are archived under
`search-v6/confirm/native-evidence/` and `native-evidence.json`.

| Variant | Median-ratio GM / pinned | Paired GM / pinned | Conditional bootstrap 95% | Worst family | Worst cell | Accepted |
|---|---:|---:|---|---:|---:|---|
| long4x32 | 1.0266 | 1.0247 | [1.0113, 1.0423] | 1.0104 | 0.9850 | **yes** |
| long4x16 | 1.0103 | 1.0053 | [0.9948, 1.0257] | 0.9629 | 0.9252 | no |
| legacy | 1.0012 | 1.0083 | [0.9835, 1.0213] | 0.9787 | 0.9705 | no |
| OFF | 1.0086 | 1.0062 | [0.9973, 1.0291] | 0.9798 | 0.9721 | no |

Long4x32 is the **first candidate to pass every fixed development gate**, not yet
a claim about held-out performance. Its workload-family median ratios (geometric
mean over the two development corpora) are:

| Workload | Ratio / pinned |
|---|---:|
| tar-create | 1.02379 |
| tar-extract | 1.01037 |
| gzip-compress | 1.03945 |
| gzip-decompress | 1.05810 |
| sed-transform | 1.01174 |
| grep-fixed | 1.01525 |
| grep-regex | 1.02811 |

The frozen `decision.json` selects only **long4x32**, comprising 32 reviewed base
pairs plus 32 observed four-op patterns (64 patterns, not 64 pairs). Generated
source SHA256:
`2bf2c2e8cfb5266de7eaf6003da9d1d8515929a0a0f66281cf79893a39ac323c`.
Plan SHA256:
`cd6d09137a1740879251088066ef6a08fef2f35f868bd1bf14750b4c89fa4fdd`.

Final validation was unsealed for the first time only after this decision
was written. It used normal unrestricted scheduling, seven rounds, all seven GNU
workloads across four independent 4 MiB/three-iteration corpora and one
512 KiB/single-iteration corpus. Winner, legacy, pinned and OFF give 35 cells,
1,470 measured invocations and 140 discarded warmups. No validation-driven
reranking or threshold changes were made.

### Independent final validation: all original gates passed

| Variant | Median-ratio GM / pinned | Paired GM / pinned | Conditional bootstrap 95% | Worst family | Worst cell | Accepted |
|---|---:|---:|---|---:|---:|---|
| long4x32 | 1.0207 | **1.0168** | **[1.0076, 1.0271]** | 0.9956 | 0.9662 | **yes** |
| legacy | 1.0011 | 1.0024 | [0.9927, 1.0134] | 0.9839 | 0.9186 | no |
| OFF | 0.9945 | 0.9977 | [0.9886, 1.0062] | 0.9796 | 0.8674 | no |

The candidate's pinned-normalized median-ratio GM / OFF is **1.0263**; this is
not a direct adjacent candidate/OFF timing comparison. The smaller paired
**1.0168 / pinned** estimate remains the primary result, not the 2.07%
independent-median headline. It exceeds the predeclared 1.015 gate, but only
modestly; more machines and independent runs are needed before a default change.

Final workload-family median ratios (including the small dataset in every family):

| Workload | Ratio / pinned |
|---|---:|
| tar-create | **0.99561** |
| tar-extract | 1.02576 |
| gzip-compress | 1.02813 |
| gzip-decompress | 1.02487 |
| sed-transform | 1.02989 |
| grep-fixed | 1.02113 |
| grep-regex | 1.01980 |

Dataset median-ratio GMs are records **1.02215**, logs **1.02541**, source
**1.02192**, entropy **1.02253**, and small **1.01145**. There are real tradeoffs:
tar-create is slightly worse in aggregate; the worst median-ratio cell is small
tar-create at **0.96616** (about **3.5% longer runtime**). Independent-median and
paired estimates sometimes disagree, so both appear below. No regressing cell or
round was removed.

#### Every final-validation cell

Times are seconds, seven rounds per variant. Each pinned median is the dedicated
control for this candidate, not the pinned samples paired with OFF or legacy.
Other variants' medians/MAD and every raw invocation remain in
`search-v6/validate/benchmark.json` and `samples.json`.

| Corpus | Workload | Winner median +/- MAD | Dedicated pinned median | Median ratio / pinned | Paired ratio / pinned |
|---|---|---:|---:|---:|---:|
| records | tar-create | 0.837232 +/- 0.054216 | 0.821130 | 0.9808 | 0.9808 |
| records | tar-extract | 0.999922 +/- 0.096800 | 1.003958 | 1.0040 | 1.0071 |
| records | gzip-compress | 2.345819 +/- 0.053617 | 2.385338 | 1.0168 | 1.0205 |
| records | gzip-decompress | 0.727334 +/- 0.031921 | 0.757510 | 1.0415 | 1.0343 |
| records | sed-transform | 5.083239 +/- 0.384832 | 5.498241 | 1.0816 | 1.0291 |
| records | grep-fixed | 0.238308 +/- 0.005969 | 0.244717 | 1.0269 | 1.0091 |
| records | grep-regex | 0.235750 +/- 0.011071 | 0.237261 | 1.0064 | 1.0140 |
| logs | tar-create | 1.874328 +/- 0.074056 | 1.969904 | 1.0510 | 1.0510 |
| logs | tar-extract | 2.351620 +/- 0.109671 | 2.423747 | 1.0307 | 1.0052 |
| logs | gzip-compress | 4.909823 +/- 0.017485 | 5.169573 | 1.0529 | 1.0339 |
| logs | gzip-decompress | 1.112402 +/- 0.027569 | 1.108624 | 0.9966 | 0.9966 |
| logs | sed-transform | 4.695833 +/- 0.135987 | 4.882053 | 1.0397 | 1.0053 |
| logs | grep-fixed | 0.231975 +/- 0.003132 | 0.235231 | 1.0140 | 0.9974 |
| logs | grep-regex | 0.203752 +/- 0.006387 | 0.202682 | 0.9947 | 1.0196 |
| source | tar-create | 0.696210 +/- 0.069640 | 0.681920 | 0.9795 | 1.0345 |
| source | tar-extract | 0.671341 +/- 0.027495 | 0.707152 | 1.0533 | 1.0002 |
| source | gzip-compress | 2.260903 +/- 0.040307 | 2.326810 | 1.0292 | 1.0351 |
| source | gzip-decompress | 0.706840 +/- 0.018004 | 0.752367 | 1.0644 | 1.0477 |
| source | sed-transform | 8.996112 +/- 0.244593 | 9.149664 | 1.0171 | 1.0116 |
| source | grep-fixed | 0.249020 +/- 0.003277 | 0.251653 | 1.0106 | 1.0171 |
| source | grep-regex | 0.829037 +/- 0.009957 | 0.830688 | 1.0020 | 1.0155 |
| entropy | tar-create | 0.613204 +/- 0.012726 | 0.614947 | 1.0028 | 1.0525 |
| entropy | tar-extract | 0.587882 +/- 0.026711 | 0.614537 | 1.0453 | 0.9947 |
| entropy | gzip-compress | 7.284979 +/- 0.070937 | 7.588427 | 1.0417 | 1.0261 |
| entropy | gzip-decompress | 1.658509 +/- 0.077087 | 1.660861 | 1.0014 | 1.0219 |
| entropy | sed-transform | 3.279539 +/- 0.188711 | 3.316943 | 1.0114 | 1.0283 |
| entropy | grep-fixed | 0.223985 +/- 0.004595 | 0.226508 | 1.0113 | 1.0012 |
| entropy | grep-regex | 0.196519 +/- 0.007020 | 0.205355 | 1.0450 | 1.0192 |
| small | tar-create | 0.104524 +/- 0.006448 | 0.100987 | 0.9662 | 1.0148 |
| small | tar-extract | 0.108799 +/- 0.003208 | 0.108430 | 0.9966 | 0.9709 |
| small | gzip-compress | 0.153879 +/- 0.006379 | 0.154016 | 1.0009 | 1.0276 |
| small | gzip-decompress | 0.085917 +/- 0.001601 | 0.087804 | 1.0220 | 0.9913 |
| small | sed-transform | 0.274433 +/- 0.019683 | 0.274873 | 1.0016 | 1.0121 |
| small | grep-fixed | 0.074337 +/- 0.003010 | 0.077551 | 1.0432 | 1.0331 |
| small | grep-regex | 0.076057 +/- 0.002490 | 0.080032 | 1.0523 | 1.0354 |

#### Evidence audit and reusable snapshot

`search-v6/validate-audit.json` independently checks all **1,470 measured samples
and 140 warmups**, 35 output-hash groups, complete round coverage, feature bits,
source identities, empty profiling counters, timing markers, and recomputed
median/MAD/paired ratios. All four final native binaries were archived and their
SHA256 verified **before any subsequent build** under `validate/native-evidence/`.
The measured candidate DLL SHA256 is
`dcc8ba78e7ee8eda26621960b29713515f17ee097a707b9a2ebe9c1fb8913bca`.

The exact selected manifest is saved without reformatting as
[`manifests/gnu-long4x32-v1.json`](manifests/gnu-long4x32-v1.json), SHA256
`a838808972316d72d6a34154a3ee5ce0c5de166651c74524b1c3899d53536e64`.
Regeneration with `--top 64` reproduces the measured generated-source SHA256 above.
It requires no historical ignored artifacts to build; see the
[opt-in instructions](manifests/README.md).

The repository manifest was also built through the normal `SuperOpcodeManifest`
MSBuild entry in an isolated `reusable-long4x32-v1` flavor: **25/25 tool tests and
15/15 native profile/long-sequence checks passed**, none skipped. Its generated
source and exported native set hash both match the measured source identity;
feature bits are exactly 1, and the original pinned file hash is unchanged.
`reusable-manifest-audit.json` records this rebuild separately from the archived
performance DLL (a different build path can produce a different DLL byte hash).

**Recommendation:** retain this as an opt-in, evaluated GNU-workload strategy.
Do not replace the original default 256-pair table yet. Measurements are confined
to this AMD Ryzen AI MAX PRO 390 Windows x64 host, .NET 11 and clang-cl 22.1.3,
Release/LTO ON/PGO OFF. They include command-loop exec/decode, I/O and in-interval
JIT, not merely dispatch throughput; other platforms and external real-world
corpora remain untested. Existing six x87 failures are unchanged, not fixed or
skipped. This validation set is now consumed and must not be reused as an unseen
holdout in later tuning. No commit, merge or push is performed by this experiment.
