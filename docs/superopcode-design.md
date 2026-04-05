# SuperOpcode Design Document

## Document Status

- Owner: Fiberish Core Maintainers
- Last Updated: 2026-03-16
- Status: Draft v1
- Scope: `libfibercpu` decode/dispatch path + profile/generation scripts

## 1. Background

The current hot path of interpretation execution has two obvious costs:

- The framework cost of `DispatchWrapper` for a single op
- Handler jumps between adjacent ops, `NextOp()` advancement, branch prediction, and I-cache interference

Existing profiles show that workloads like CoreMark repeatedly exhibit stable local op sequences.  
If two adjacent ops frequently appear together and there is no boundary that must be exposed to the generic dispatch between them, merging them into a wider handler typically reduces:

- One handler jump
- One `DispatchWrapper` framework
- Some repeated decode metadata reads
- Some unnecessary branch predictor / BTB noise

Here we call this "a single execution unit formed by merging multiple consecutive ops" a `SuperOpcode`.

## 2. Goals

The first-phase goals for `SuperOpcode` are modest:

- Only merge 2 consecutive ops within a block
- Only select candidates from high-frequency single-op anchors plus adjacent def-use relations
- Only rewrite the first op's handler after decode is complete
- Do not change `DecodedOp` size
- Do not introduce a new dynamic compiler

Non-goals:

- Do not support merging 3 or more ops in the first phase
- Do not support cross-block merging in the first phase
- Do not change guest-observable semantics in the first phase
- Do not perform SSA/IR-based peephole optimization in the first phase

## 3. Core Approach

### 3.1 Data Sources

The candidate miner should start from hot single-op anchors rather than directly ranking raw 2-grams.

For each hot handler `handler[i]`, analyze:

- Its predecessor `handler[i - 1]`
- Its successor `handler[i + 1]`
- Whether either adjacent op forms a plausible def-use chain with the anchor

Input can come from:

- Handler profile block dump
- Block trace dump
- Directly traversing the op sequence exported from `blocks.bin`

Output should at least include:

- `anchor_handler`
- `first_handler`
- `second_handler`
- `direction` (`predecessor` or `successor`)
- `relation_kind` such as `flags-flow`, `flags-into-control`, or `gpr-flow`
- Anchor hotness, pair frequency, and coverage
- Optional guest opcode / ModRM / prefix distribution

Typical coarse def-use patterns worth modeling first:

- `cmp/test/...` define `eflags`
- `jcc/cmov/setcc` use `eflags`
- `load/movzx/movsx/mov reg, ...` define a register
- `store/cmp/test/alu/...` use a register

### 3.2 Generated Artifacts

For selected 2-grams, scripts generate a new handler:

- Named like `SuperOpcode_<A>__<B>`
- Internally executes two original `LogicFunc` sequentially
- Jumps directly to the third op on the success path

After decoding, if:

- The current op's handler is `A`
- The next op's handler is `B`
- And the additional constraints for this superopcode are satisfied

Then rewrite the first op's handler to `SuperOpcode_<A>__<B>`.

The second op remains in the block but is skipped by the first op during normal execution.

## 4. Execution Model

Assume the original execution chain is:

`op0(A) -> op1(B) -> op2(C)`

After rewriting it becomes:

- `op0.handler = SuperOpcode_A_B`
- `op1.handler = B`, but the normal path will not directly land here
- `op2.handler = C`

The success path of `SuperOpcode_A_B` is equivalent to:

1. Call the `LogicFunc` corresponding to `A`
2. If `A` returns non-`Continue`, exit according to original semantics
3. Call the `LogicFunc` corresponding to `B`
4. If `B` returns non-`Continue`, exit according to original semantics
5. Jump directly to `op2->handler`

The key points are:

- `A` and `B` still use their original logic implementations
- Single-op semantics are not changed
- Only the "scheduling framework between two ops" is changed

## 5. Recommended Implementation Form

### 5.1 Generation Scripts

It is recommended to add a new script that takes 2-gram statistics as input and outputs:

- A `.inc` or `.generated.h`
- Optionally a `.generated.cpp`
- A manifest recording which superopcodes were generated

Recommended output content:

- Superopcode enumeration table
- `HandlerFunc` declarations
- `DispatchWrapper`-style wrapper functions
- Lookup table initialization code for `(handler_a, handler_b) -> super_handler`

### 5.2 Composition Approach

It is recommended to reuse the existing `LogicFunc` model rather than directly concatenating two `HandlerFunc`s.

Reasons:

- `HandlerFunc` already has a dispatch wrapper layer; direct nesting would duplicate the framework
- `LogicFunc` input/output is closer to "composable interpreter primitives"
- Existing `LogicFlow` already expresses continue/exit/restart/branch results

The ideal generated form looks like:

```cpp
template <LogicFunc A, LogicFunc B>
ATTR_PRESERVE_NONE int64_t SuperOpcodeDispatch(EmuState* state, DecodedOp* op, int64_t instr_limit,
                                               mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {
    auto* op1 = NextOp(op);
    auto flow0 = A(state, op, &utlb, GetImm(op), &branch, flags_cache);
    // Handle A's flow
    auto flow1 = B(state, op1, &utlb, GetImm(op1), &branch, flags_cache);
    // Handle B's flow
    auto* op2 = NextOp(op1);
    ATTR_MUSTTAIL return op2->handler(state, op2, instr_limit, utlb, branch, flags_cache);
}
```

The actual generated code may not look exactly like this, but the semantics should be as close as possible.

### 5.3 Lookup Table

Expensive lookups should not be done at runtime.  
It is recommended to build a static table during process initialization:

- key: `(handler_a, handler_b)`
- value: `super_handler`

The decode phase only performs one lookup and then writes back to `op->handler`.

Lookup structure options:

- Small static array + linear scan
- Sorted array + binary search
- `unordered_dense` static map

The first phase will not have many entries; prefer a simple implementation.

## 6. Decode Integration

### 6.1 Timing

It is recommended to run a block-local pass uniformly either before or after block decode is complete and sentinel is appended.

Input:

- `temp_ops`
- Number of ops
- Each op's already-parsed handler

Traversal method:

- For each `i`, look at `temp_ops[i]` and `temp_ops[i+1]`
- If a superopcode table match is found, attempt to apply it

### 6.2 Application Rules

When applying a superopcode, the first op must at least satisfy:

- `op[i]` is not the last real op in the block
- `op[i+1]` is not a sentinel
- `(handler_i, handler_{i+1})` matches the table
- Both ops satisfy their corresponding legality checks

After application:

- `op[i].handler = super_handler`
- `op[i]` can optionally mark a debug bit indicating it has been covered by a superopcode

It is not recommended to directly delete `op[i+1]` in the first phase, as this would change block layout, `NextOp` offsets, and many existing assumptions.  
"Keep the second op but skip it" is safer.

## 7. Correctness Constraints

Not all adjacent pairs of ops are suitable for merging. The first phase must be conservative.

### 7.1 Combinations That Must Be Rejected

- The first op may `ContinueSkipOne`
- The first op or second op may depend on a special protocol where "current op must equal dispatch entry op"
- The first op may change `op` pointer semantics after success
- The second op has special boundary requirements for block profiler / debug hooks
- Either op is a sentinel / exit handler
- Either op is a special control-flow op that requires precise single-step visible boundaries

### 7.2 Memory Restart Semantics

If the first op returns:

- `RestartMemoryOp`
- `RetryMemoryOp`
- `ExitOnCurrentEIP`
- `ExitOnNextEIP`
- `ExitToBranch`

Then the superopcode must immediately exit according to original semantics and cannot continue executing the second op.

The same applies to the second op.

### 7.3 Branch Semantics

If the first op writes `branch` and returns a branch-type flow:

- The superopcode must pass this `branch` unchanged to the original resolver path

Only when the first op returns a normal `Continue` can it proceed to the second op.

### 7.4 EIP Visibility

If exiting midway:

- Must guarantee exactly the same `eip` synchronization behavior as "executing the first or second op alone"

The easiest places to make mistakes here are:

- First op succeeds, second op faults/restarts
- First op has already modified state, second op needs to set visible `eip` to its own start/end position

Therefore, flow handling in superopcodes cannot cut corners; it must follow the original rules separately for "which op is currently being processed".

## 8. Recommended First Batch of Candidates

Candidates should prioritize satisfying:

- The anchor op itself is very hot
- The adjacent op is connected by an obvious def-use relation
- The pair is still only 2 ops wide
- Legality can be expressed conservatively at decode time

Typical first-batch shapes include:

- `cmp/test/... -> jcc`
- `cmp/test/... -> cmov`
- `load/movzx/movsx -> cmp/test`
- `load/mov -> alu`
- `mov/reg-producer -> store`

But note:

- `cmp/test + jcc` is attractive precisely because the data dependency is clear (`eflags`), but it still needs strict control-flow legality checks
- Pairs containing stores or other memory side effects should preserve restart / retry semantics exactly

## 9. Statistics Script Output Recommendations

Candidate mining should expose a three-level view:

1. Anchor hotness: which single handlers dominate execution
2. Anchor neighborhood: which predecessor/successor ops sit next to those anchors
3. Mergeable candidates: which adjacent pairs also satisfy a coarse def-use relation

Example fields:

- `anchor_handler`
- `first_handler`
- `second_handler`
- `direction`
- `relation_kind`
- `shared_resources`
- `anchor_weighted_exec_count`
- `weighted_exec_count`
- `count_pct`
- `is_candidate`
- `reject_reason`

This avoids incorrectly selecting combinations that are merely adjacent in the profile but not semantically coupled.

## 9.1 Existing Runner / Block Dump Capability Assessment

The current `Podish.PerfTools runner` command now provides a usable end-to-end foundation for `SuperOpcode` candidate collection.

### Existing Capabilities

- Supports batch running samples for fixed workloads
- Supports block dump collection
- Supports automatically calling `Podish.PerfTools analyze-blocks`
- Supports block-local adjacency mining over decoded op streams
- Supports `--aggregate-superopcode-candidates`
- Supports writing aggregate outputs to `superopcode_candidates.json` and `.md`
- Each sample saves transcript, summary, and optional guest stats directory

### Parts Already Sufficient in Existing Data

With the current block-dump pipeline, the exported data is already sufficient to recover:

- Op order within blocks
- Handler symbol corresponding to each op
- Block-level execution count

This means:

- Hot-anchor statistics do not necessarily require new VM internal instrumentation
- We can first traverse block dump offline, weighting anchor and adjacent-pair observations by `block.exec_count`

### Current Deficiencies

The old gaps around n-gram analysis and cross-sample aggregation are no longer the main blocker. The remaining deficiencies are narrower:

1. `Podish.PerfTools runner` treats block dump as a "byproduct"  
   The current main goal is still benchmark timing, not building a stable op-sequence corpus.

2. Workload coverage is still narrow by default  
   If based only on CoreMark, it is easy to overfit superopcodes to a single workload.

3. Candidate selection is still too frequency-driven  
   The aggregation pipeline should explicitly encode anchor-neighbor def-use affinity, rather than treating every adjacent pair equally.

4. CLI shape is still somewhat historical  
   Some naming still reflects older profiling-oriented workflows rather than the current block-analysis pipeline.

### Conclusion

The conclusion is:

- `Podish.PerfTools runner` is now sufficient as the main sampling entry point
- `Podish.PerfTools analyze-blocks` and `Podish.PerfTools analyze-superopcode-candidates` already provide the first usable post-processing path
- The next step is to improve candidate quality and workload breadth, not to rebuild the data pipeline from scratch

## 9.2 Implementation Plan Based on Existing Runner

It is recommended to proceed from the current implementation, rather than treating the whole pipeline as future work.

### Step 1: Keep Block Dump Readability Verified

Keep validating that the chain `blocks.bin -> Podish.PerfTools analyze-blocks -> blocks_analysis.json` stays readable as schemas evolve.

Need to check:

- Whether `blocks.bin` export format is consistent with the PerfTool reader
- Whether runtime base / handler pointer parsing is correct
- Whether there is schema drift causing analysis output to go empty

This remains a regression guard, not a missing first milestone.

### Step 2: Continue Extending `Podish.PerfTools analyze-blocks`

`Podish.PerfTools analyze-blocks` is already the first working block-order data source. Future work should build on it directly.

Recommended new capabilities:

- `--group-by handler`
- `--group-by guest-opcode`
- `--weighted-by exec-count`
- Output for each anchor / adjacent pair:
  - weighted count
  - unique block count
  - sample count
  - context examples
  - inferred def-use hints

### Step 3: Keep Improving the Aggregation Script

`Podish.PerfTools analyze-superopcode-candidates` already exists. The next step is to score hot-anchor neighborhoods rather than raw N-grams.

Current inputs:

- A `results/` directory
- Multiple `guest-stats/*/blocks_analysis.json`

Current outputs:

- `superopcode_candidates.json`
- `superopcode_candidates.md`

Next responsibilities to strengthen:

- Cross-sample anchor aggregation
- Cross-sample adjacent-pair aggregation around anchors
- Deduplicate runtime address differences
- Sort by semantic relation first, then weighted count / coverage
- Mark candidates with reject reasons

### Step 4: Extend Runner Options

The runner already exposes the essential superopcode sampling parameters, but the CLI can still be cleaned up.

Current options already include:

- `--block-n-gram 2`
- `--aggregate-superopcode-candidates`
- block dump related switches

Among these:

- `--block-n-gram 2` can remain as the block-window width, but selection should be anchor-driven rather than pure n-gram ranking
- Long-term it is recommended to converge on more general block stats / adjacency semantics

### Step 5: Expand Workload Coverage

The first batch of candidates should not come only from CoreMark.

It is recommended to cover at least:

- `coremark run`
- `compress`
- `compile`
- Future additions:
  - Small shell workloads
  - libc-heavy workloads
  - branch-heavy workloads

This makes the resulting candidates less likely to be hijacked by a single program.

## 9.3 Specific Extension Recommendations for Runner

If extending directly on the existing `Podish.PerfTools runner`, the following is recommended.

### A. Keep Existing SampleResult, but Add Fields

It is recommended to add:

- `block_dump_dir`
- `ngram_analysis_json`
- `candidate_manifest_json`

This way summary.json can directly link the entire data chain.

### B. Decouple Block Dump from Benchmark Timing

In the current `run_sample()`, exporting block dump is tied together with benchmark samples.  
For `SuperOpcode`, a better pattern is:

- Benchmark mode: pursue timing stability
- Block-dump mode: pursue corpus coverage

It is recommended to subsequently allow:

- Running "corpus collection mode" separately
- Lowering `repeat`
- Increasing workload diversity

### C. Support Post-Processing Aggregation

It is recommended that after all samples are complete, the runner can optionally automatically call the aggregation script to produce:

- Global top 2-gram
- Top 2-gram for each workload
- Candidate superopcode list

This is much more efficient than manually flipping through multiple `blocks_analysis.json`.

Now this step can already be run directly by first collecting block-dump samples with the current runner workflow, then aggregating the exported `guest-stats` directories separately:

```bash
dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- \
  analyze-superopcode-candidates \
  benchmark/podish_perf/results/<timestamp>/guest-stats \
  --n-gram 2 \
  --top 100 \
  --output-json benchmark/podish_perf/results/<timestamp>/superopcode_candidates.json \
  --output-md benchmark/podish_perf/results/<timestamp>/superopcode_candidates.md
```

The current aggregation script's responsibilities are:

- Recursively discover `blocks_analysis.json`
- Skip samples where `blocks` is empty or there are obvious schema/dump drift warnings
- Rebuild N-gram directly from `blocks`, rather than relying on `top_ngrams`
  This way it is not affected by single-sample truncation affecting the overall ranking
- Output:
  - `superopcode_candidates.json`
  - `superopcode_candidates.md`

### D. Record Build Identity

Because superopcode candidates depend on handler symbol names and layout, it is recommended to additionally record in summary:

- git commit
- build flavor
- `EnableHandlerProfile`
- `FIBERCPU_EXIT_HANDLER_REPLICA_COUNT`
- Whether superopcode is enabled

This way subsequent comparisons of different datasets will not be confused about their origins.

## 9.4 Recommended Data Maturity Standards

Before starting to generate the first batch of superopcodes, it is recommended to at least satisfy:

- `blocks_analysis.json` can stably show non-empty `blocks`
- At least 3 workloads
- At least 3 sample aggregations
- Top 2-gram rankings are basically stable between samples
- Candidate pair coverage is sufficiently high

If these conditions cannot be met, it is more suitable to fix the data pipeline first rather than rushing to generate superopcodes.

## 10. Code Generation Recommendations

It is recommended to generate code rather than hand-write large tables; the reasons are:

- Candidate combinations will be continuously adjusted
- Naming rules can be unified
- Easy to synchronously generate legality metadata
- Can automatically produce registration tables and test lists

Generator input recommendations:

- `superopcode_candidates.json`

Generator output recommendations:

- `libfibercpu/generated/superopcodes.generated.h`
- `libfibercpu/generated/superopcodes.generated.cpp`

Each superopcode metadata should include:

- Name
- `LogicFunc A`
- `LogicFunc B`
- Required legality predicate
- Whether enabled

## 11. Debugging and Observability

It is recommended to retain the following debugging capabilities:

- Environment variable master switch: `FIBERCPU_ENABLE_SUPEROPCODE`
- Log/counter: how many times superopcodes are hit
- Block dump shows "which op was covered by superopcode"
- Can print `(A,B) -> SuperOpcode_A_B` mapping

This way, when errors occur, one can quickly:

- Globally disable superopcode to verify if the problem disappears
- Locate which specific pair has issues

## 12. Testing Strategy

### 12.1 Unit Tests

For each generated superopcode, cover at least:

- Normal `Continue -> Continue`
- First op early exit
- Second op early exit
- Branch writeback
- Memory restart / retry
- `eip` synchronization on fault

### 12.2 Differential Testing

For the same guest input, compare:

- Superopcode disabled
- Superopcode enabled

Compare:

- GPR
- flags
- memory
- `eip`
- Exception/signal behavior

### 12.3 Profile Regression

Each time the superopcode set is expanded, at least look at:

- Overall wall time
- top-25 self time
- Number of superopcode hits
- Whether popular original handlers really decreased

Avoid misjudging optimization effectiveness just because of hotspot rearrangement.

## 13. Risks

There are four main risk categories:

1. Semantic risks  
   Incomplete flow handling leading to restart/exit/eip synchronization errors.

2. Code size risks  
   Generating too many superopcodes, causing I-cache and instruction layout deterioration.

3. Maintenance risks  
   After too many hand-written combinations become difficult to manage, so the first phase must use scripted generation.

4. Profile overfitting  
   Only effective for a single workload; benefits disappear or even regress when switching programs.

## 14. Phased Rollout Recommendations

### Phase 1: Infrastructure

- Add N-gram statistics scripts
- Establish `blocks.bin -> Podish.PerfTools analyze-blocks` readable chain
- Enable runner to stably export block-op sequences
- Define superopcode manifest format
- Add code generation scripts
- Introduce global switches and debug counters

### Phase 2: Minimum Viable Version

- Select a small number of stable 2-grams based on aggregated data
- Only enable a small number of pure `Continue` pairs
- Rewrite the first op by handler pair after decode
- Keep the second op, do not change block layout

### Phase 3: Expand the Set

- Expand candidates based on profile
- Add more complex but higher-benefit combinations
- Introduce legality predicate refinement

### Phase 4: More Aggressive Optimizations

- Research 3-op superopcodes
- Research physical op compression within blocks
- Research coordinated generation with specialized handler / modreg fast path

## 15. Current Recommendations

The current most reasonable implementation path is:

- First produce N-gram statistics
- Only support 2-ops initially
- Only do "handler rewriting after decode" initially
- Only merge the most stable ordinary `Continue` combinations initially

After this version is run through and benefits are verified, then decide whether to incorporate control-flow combinations like `cmp/test + jcc` into the first batch.

The advantages of this route are:

- Small intrusion into existing interpreter structure
- Easy rollback
- Easy gradual expansion
- Easier to confine errors to individual superopcodes
