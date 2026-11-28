# Baked instruction variants

CMake generates native scalar handlers for the interpreter's 16-byte slots from the
[ModRM table](../libfibercpu/presets/3m/modrm.json) and
[scalar plan](../libfibercpu/presets/3m/scalar.json) in the default `3m`
preset. The [native build presets](build-presets.md) describe the other table sets.
The scalar plan binds prefixes, flag liveness, effective addresses and immediates
for selected shapes. The C++ and exact LLVM paths use the catalog kernel
bindings. You can build and run the C++ handlers without LLVM.

The decoder tries exact scalar, partial scalar, and ModRM bindings in that
order. A ModRM entry can also bind width and flags. For some memory operands,
the decoder masks the ModRM bits that the kernel gets from the decoded
effective address. If no baked entry matches, the decoder selects a scalar
binding for the slot count. The
architectural catalog selects mandatory-prefix variants such as MMX or SSE.

In a version 4 scalar entry, `slots` fixes the decoded slot count to 1 or 2.
Set `disp` or `imm` to `null` in its operand record to read that value from the
runtime op. The lookup still matches the other fields and the slot count. It
tries single-field variants before variants with both fields dynamic. Exact
entries retain the version 3 format. Fusion plans still use exact shapes.

Fusions also come from recorded profiles. The checked-in sequence manifest
contains canonical instruction IDs. The decoder selects a fusion when all of
its operand shapes match an entry in the baked plan. Other sequences use their
scalar handlers. The LLVM JIT re-decodes guest bytes with architectural handlers
to build exact block recipes. See [the JIT options](instruction-specialization.txt).

## Train the baking presets

Run the [preset trainer](../scripts/train_nested_presets.py) with Python 3.11
or later on a macOS ARM64 calibration host. It captures the configured workloads with static
specialization, SuperOpcodes, online JIT and linked block caching disabled.
It ranks observed ModRM, width/flags, exact and partial scalar, and fusion
shapes. Partial candidates group observed operands after removing the
displacement, the immediate, or both from the match key.

### Ranking and linking

Both guest architectures use generalization-aware marginal ranking. One offline
search per guest produces the 1.5, 3 and 10 MiB tiers. The shipped default is
the 3 MiB prefix. Costs apply to native text growth relative to an unbaked
build with the same guest and toolchain.

The trainer replays affected blocks in native match order when a candidate is
added. It credits dispatches saved by fusions and estimated savings from
scalar bindings. It divides the incremental gain by the estimated bytes of the
candidate and any unpaid dependencies. The default `--risk 0.5` gives equal
weight to the mean gain across training families and the mean after dropping
the strongest family. The same setting discounts strict matches and long
fusions. More distinct neighboring instruction shapes or more observed operand
encodings for a partial scalar reduce the discount. Both evidence counts
saturate at 32. Each entry records its per-family gain at insertion.

To train both guests with the shared workload config and reserve grep alongside
the default Node holdout, run

```sh
python3 scripts/train_nested_presets.py --config training.json --output out/training \
  --holdout-family grep
```

To use the CMake training target:

```sh
cmake -S libfibercpu -B out/train -DCMAKE_BUILD_TYPE=Release \
  -DFIBERCPU_PRESET_TRAINING_CONFIG=/absolute/path/to/training.json
cmake --build out/train --config Release --target fibercpu_train_presets
```

The default `--search-depth 1` refreshes marginal estimates at the standard
budget checkpoints. Increasing depth refreshes estimates more often within
the same offline ranking. Use `--capture-source` to reuse verified execution profiles. The trainer
rebuilds the candidate pool from the training split. The i386 pool includes up
to 1,024 exact fusion candidates. AArch64 includes recurring three- and
four-word windows from eligible maximal runs.

The static cost cache maps native function spans to decoder bindings, resolves
plan-local i386 fusion clone numbers, and estimates whole-plan residuals by
holding out independent training runs (or distinct tables from a single run). Its current reader supports thin
little-endian 64-bit Mach-O with symbols and function starts. Cache reuse checks
the unbaked text, compiler, build options and source fingerprints. Without
`--rank-cost-cache` the trainer uses the guest's class priors with zero reserve.
The calibrated path subtracts a one-sided historical 90th-percentile reserve
from each byte budget. The linker supplies the final size. To calibrate from linked training archives,
run `python3 scripts/baking_rank_cost.py RUNS GUEST COST.json` for each guest.
Pass a cache file or a directory containing `i386.json` and `aarch64.json`
to `--rank-cost-cache`.

Use `--rank-only` to inspect `ranking.json` before linking presets. Resume with
the same search options and omit that flag to build. Each entry contains its
binding, admission rank, estimated cost, cumulative bytes, estimated first
tier, marginal gain and evidence. `rank-builds.json` records all candidate build
attempts. If a prefix exceeds its measured tier, the trainer moves the cutoff
back along the same order and links again. The previous tier is a lower bound.
Identical cutoffs share a binary. Training defaults to six candidate
build attempts per guest. Increasing the limit resumes the same ranking.
`search-summary.json` records the estimated and verified cuts. The frozen
archive includes the ranking and manifest for the verified prefixes.

### Held-out evaluation

A manifest entry with `"holdout": true` reserves its whole family. The shared
suite reserves `node` this way. Repeat `--holdout-family` to reserve additional
families. The suite groups gzip and gunzip as `gzip`, both grep cases as
`grep`, and sort and sha256 as `coreutils`. Sed has its own family. Cases
without an explicit `family` default to their individual IDs. Use a common
family name for related cases in a custom manifest. Held-out cases are
excluded before capture, pool construction, cost fitting and finalist
measurement.

After freezing, evaluate an archive directly. The command verifies the manifest,
profile identities and frozen artifact hashes, and writes case ratios, family
ratios and the slowest family to `report.json`. For a production comparison,
supply the matching unbaked or baked reference library explicitly with
`--reference-library`.

```sh
python3 scripts/evaluate_baking_holdout.py --config training.json \
  --frozen out/training/aarch64/frozen --output out/held-out-aarch64 --repeats 3
```

For leave-one-family-out comparison, train a fresh chain for each training
split. Unseen compiler builds, libc versions or genuinely new applications
require corresponding binaries and verified cases in the original evaluation
configuration. Use the same splits for both guests.
Keep a separate untouched suite for any release claim after algorithm selection.

The config supplies writable guest root filesystems and a framework-dependent
Podish build in `managed_cli` that supports the selected guests. Set `perf_tools`
to `Podish.PerfTools.dll` for i386, and provide the `cmake` executable,
`cmake_args`, and a `benchmark_lock` shared with other local benchmarks.

For shared training, set `rootfs` to an object with `i386` and `aarch64`
paths. Populate both root filesystems with the architecture's binaries and
the same input files. The shared suite expects GNU tools and Bash on `PATH`,
the existing `input.txt` and `input.gz` fixtures under `/opt/nested-training`,
Node at `/opt/bench/node` and the JavaScript dependencies under
`/opt/bench/webpack`. The Node case resolves `fast-deep-equal` with
`enhanced-resolve` and minifies it repeatedly in one Node process.
Set `workloads` to the path of the
[shared training suite](../benchmark/optimization/manifests/baking-workloads.json)
or supply the workload array inline. Relative manifest paths resolve against
the config directory. Each entry supplies `id`, descriptive `group`, software
`family`, guest `cwd`, `shell`, `repetitions` and an expected output
`sha256`. Optional `verify` selects the output representation to hash.
Both guests use the same entries.

Every shared case launches its benchmarked program once. It increases work
inside that process through extra input operands, a heavier sed script, or
repeated minification in Node. Gzip streams compressed bytes to `cksum`,
and gunzip prints a status line after gzip verifies every input. Sed prints
the reformatted needle records, sort deduplicates its input, and grep,
sha256sum and Node emit counts or digests. The runner hashes these stdout
files as bytes. Older and custom manifests can still select their original
canonical output formats. When recalibrating the shared suite, time both
guests with `JitMode=off` under the configured benchmark lock and target at
least two seconds per case.

```json
{
  "rootfs": {"i386": "/tmp/rootfs-i386", "aarch64": "/tmp/rootfs-arm64"},
  "workloads": "/path/to/podish/benchmark/optimization/manifests/baking-workloads.json"
}
```

The trainer runs both guests by default and writes their results under
`i386/` and `aarch64/` in the output directory. Use `--guest i386` or
`--guest aarch64` to run one guest with the shared config. A single `rootfs`
path with an optional `guest` still works and retains the original output
layout. Use a fresh output directory for each new suite.

The CMake target writes captures, plans, and timings under
`out/train/preset-training/Release/`. Each guest's `frozen/` directory contains
its unbaked library, measured text sizes and growth, and the selected
`1.5m`, `3m`, and `10m` plans and native libraries. Each tier is capped
at the unbaked native text size plus its per-guest MiB allowance. The trainer
uses a single-guest build so the measured increase belongs to that guest.
Compare the frozen libraries and
unbaked baseline on a separate holdout suite with the same
guest inputs and runtime settings. For i386, copy the four JSON files
and, for tiers with fusion, the generated C++ source into the matching
`libfibercpu/presets/<tier>/` directories. For AArch64, copy each tier's
`scalar.json` to `libfibercpu/presets/aarch64/<tier>/`. A normal CMake build
selects the corresponding plan for every enabled guest.

Use `FIBERCPU_BAKING_PRESET=custom` and the individual plan options to try
candidate tables. Set `FIBERCPU_MODRM_PLAN_FILE` for the ModRM plan,
`FIBERCPU_STATIC_OPERAND_PLAN_FILE` for the scalar plan, and
`FIBERCPU_GENERATED_SUPEROPCODES_FILE` and `FIBERCPU_OPERAND_PLAN_FILE` for
the paired fusion source and operand shapes. An empty scalar plan path
leaves the ModRM bindings enabled.

Relative control transfers share an operand shape across encoded offsets.
The decoder resolves the target into the runtime slot and retains the
encoding for profile export. Data immediates and the stack adjustment of
`RET imm16` still distinguish shapes. Recapture profiles after changing
canonical instruction identities or the decoded operand representation.

## Code generation review

Inspect the emitted machine code as part of a table update.

| Path | Expected generated code |
| --- | --- |
| Baked scalar | An exact scalar shape passes fixed prefixes, effective address and immediate values to the shared kernel. The fixed ModRM operand reaches the kernel as a literal. Register selection and the ModRM register-versus-memory decision disappear when the kernel uses them. The instruction advances one or two slots with a fixed displacement. A ModRM-only key leaves other operands dynamic. A matching scalar policy additionally fixes the width and omits updates to dead flags where specified. |
| Baked fusion | The exact operand records supply the ModRM, immediates and slot count of every constituent. The final handler performs one tail dispatch. Each memory operation retains its restart/retry exit. |
| LLVM exact | The block compiler inlines the operation templates with their decoded operands before running the LLVM optimization pipeline. On the ordinary path, a baked memory immediate should appear as an instruction constant and the PC for replay should be a fixed offset from the original slot pointer. Branch targets and links still come from block runtime storage. |

On ARM64, use `llvm-nm` and `llvm-objdump -d` on libfibercpu to inspect
`DispatchWrapper` instantiations with `WithModRM` and `SlotBound` in the
signature. Include a register form and a two-slot memory-immediate form.
Use `JitMode=profile` and `WeightedProfilePath` to capture the LLVM probe recipe.
`fibercpu_llvm_cache --linked PROFILE BUNDLE --llvm-opt-level O2` emits objects
with the online compiler's exact-operand preparation. Run
[extract_linked_block_cache.py](../scripts/extract_linked_block_cache.py) with
the bundle and the producer ABI fingerprint, then disassemble the matching
object. Run the probe with `JitMode=exact` to check execution and replay.

Use `-XX:LlvmFoldOperandLoads=on` for the extra post-inline folding pass in
the online exact compiler. To compare linked-cache objects from the same
profile, run `fibercpu_llvm_cache` with `FIBERCPU_LLVM_FOLD_OPERAND_LOADS=1`,
then with `FIBERCPU_LLVM_FOLD_OPERAND_LOADS=0` and a different output path.

The initial ARM64 review, recorded on 2026-11-20, counted 1,258 baked scalar symbols, including 17
two-slot bindings. The inspected Group 1 register add advanced 16 bytes. The
two-slot memory compare loaded its immediate from offset 24 and advanced 32
bytes. Both selected the ModRM operation at compile time. The LLVM exact
probe materialized `0x12345678` with `mov` and `movk`, inlined the instruction
bodies and retained the MMU replay and block-link exits.

## AArch64 plans

The AArch64 interpreter records executed integer, branch and memory words using `-XX:AArch64ProfilePath=/absolute/path/to/words.json` on a Podish run. Version 2 also records a histogram of ordered, retired blocks up to 24 instructions, including words with no baking candidate. Page edges, control transfers and runtime exits split the stream. Word, pair and sequence counts are retained. Item-level training requires version 2 captures. Recording disables cached execution for that run so instruction counts also include normally fused constituents.

`scripts/train_nested_presets.py` uses the shared workload configuration. It
normalizes each workload's capture before ranking candidates and measures
their throughput against the unbaked interpreter, with software families weighted
equally. It ranks exact words, partial shapes, pairs and long sequences together,
then checks the linked text growth of each prefix. The CMake
generator can also consume a single capture directly.

```sh
python3 scripts/train_nested_presets.py --config training.json --guest aarch64 --output /tmp/preset-training
```

The stock plans use the same GNU training workloads on both guests. Node and grep were held out.
Scalar partials keep the registers, operation, width, and addressing mode
fixed. They read the decoded ADD/SUB immediate, unsigned or pre/post-indexed
load/store offset, paired load/store offset, move-wide value, or logical
bitmask from the instruction slot. The existing presets
contain ADD/SUB and unsigned-offset load/store shapes.

A pair row can specify a `dynamic_mask`. Bit 0 clears the first word's
immediate field and bit 1 clears the second word's. Pair keys can also clear
PC-relative offsets in ADR/ADRP, literal loads, and direct branches. Those
handlers read the target already computed for the instruction slot. The
decoder tries longer exact sequences, exact pairs, then partial pairs. Among
partial pairs, it tries the first-only mask, the second-only mask, then both.
Exact scalars precede partial scalars.

The trainer requires at least two observed encodings per partial shape. It
uses ordered captures to find transfer-start partial pairs and ranks them
with the other bindings. Set `aarch64_partial_pair_budget` to cap the
candidate pool. It defaults to the scalar partial candidate budget. Use
`--no-partial-operands` to disable both pools. Ranked tiers retain the
bindings of the smaller tier.

The CMake limits are `FIBERCPU_AARCH64_OPERAND_BUDGET`,
`FIBERCPU_AARCH64_PARTIAL_BUDGET`, `FIBERCPU_AARCH64_PAIR_BUDGET`, and
`FIBERCPU_AARCH64_SEQUENCE_BUDGET`. `FIBERCPU_AARCH64_OPERAND_PLAN_FILE`
selects another plan. Unsupported keys fall back to the lowered handler.
The optimization fingerprint includes the plan hash, all four budgets, the
guest pin selection and the NZCV elimination setting.
