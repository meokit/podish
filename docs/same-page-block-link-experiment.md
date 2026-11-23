# Same-page block linking experiment

We reverted this November 2026 experiment after two full-length CoreMark runs. The final variant retired 5.17% fewer host instructions and scored 0.01% above the baseline, commit `d2b92aa4`. The L1D miss count rose in one pair and fell in the other after we matched the number of scheduler calls to the tagged variant.

## Design

The interpreter's `ResolveBranchTargetInline` loads a `BasicBlock*` from the link slot. It reads the first 16 bytes of the block header, checks validity and target EIP, deducts the target instruction count, and tail-calls `entry`. The baseline used the same path. We tried to remove the header load for same-page edges. See [dispatch.h](../libfibercpu/src/dispatch.h) and [decoder.h](../libfibercpu/src/decoder.h).

The first prototype stored a signed offset from the current op, a cached guest EIP and a rounded charge in the link slot. The revised prototype stored the original block pointer and cached handler entry in a pair of 64-bit words for fixed-target edges. It generated page-local copies of eligible scalar and fused handlers and selected them when installing the link. Fallthrough exits received a second slot. Variable-target edges took the guarded path.

The prototype restored the regular handlers and cleared links when it invalidated a page. It did the same when it published a JIT entry, so subsequent links used the new entry. Generated static handlers, runtime machine-code specializations, and LLVM block templates followed this policy.

Three revisions changed the representation and charge while retaining the two slots.

| Variant | Link representation and budget |
| --- | --- |
| Low-bit tag | The bottom four bits of the block pointer encoded the target charge rounded up to a multiple of four. |
| Entry tag | The handler address carried the rounded charge in its upper four bits. The block pointer kept its original value. |
| Fixed 4 or 10.375 | The link held both original pointers. The handler used an immediate charge. For 10.375, the handler counted in sixteenths of a guest instruction and deducted 166 units. The outer run loop counted whole guest instructions. |

## CoreMark throughput

We ran the same GCC 15.2 IA-32 CoreMark guest on an Apple M5 Max with native macOS arm64, static operand specialization, extended fusion, a 400 MiB block cache, and host JIT off. The guest binary SHA-256 was `48c3ecf2c2e2bbbebc8249f3f2ab30933a1e85e8eeaa1b6c0806bed90a5085db`. We ran one build at a time and checked the guest CRC. Use the 30,000-iteration batches for comparison only. The 100,000-iteration batches met CoreMark's minimum execution time.

| Batch | Baseline | Experimental variants | Repetitions per build |
| --- | ---: | --- | ---: |
| Initial offset and cached-EIP prototype, 100k | 6614.19 | Exact charge 5743.17, rounded charge 5794.41 | 3 |
| Revised fixed-target links, 100k | 6676.46 | Earlier rounded prototype 5889.28, revised rounded prototype 6419.72 | 3 |
| Separate CoreMark PGO profiles, 100k | 6686.92 | Revised rounded prototype 6540.68 (−2.19%) | 2 |
| Pointer and budget variants, 30k | Low-bit tag 6429.49 | Entry tag 6534.52, fixed 4 6666.67 | 5 |
| Untagged variants versus `d2b92aa4`, 30k | 6650.41 | Fixed 10.375 6674.08 (+0.36%), fixed 4 6693.44 (+0.65%) | 5 |
| Final comparison, 100k | 6633.77 | Fixed 10.375 6634.41 (+0.01%) | 2 |

We alternated the run order for the final comparison. The paired scores were 6615.51 versus 6620.76 and 6652.03 versus 6648.05. Each build won once.

## Scheduler and hardware counters

We counted `X86_Run` calls on the VM thread during 30,000 guest iterations. The tagged variant made 1,435 calls and fixed 4 made 727. A charge of 10.375 matched the tagged count. The baseline made 1,307 calls, so the calibrated variant entered `X86_Run` 9.8% more often.

| VM thread L1D load miss comparison | Low-bit tag | Untagged variant | Change |
| --- | ---: | ---: | ---: |
| Fixed 4, first pair, 100k | 12,448,101 | 11,537,583 | −7.3% |
| Fixed 4, reversed pair, 100k | 17,389,881 | 10,683,710 | −38.6% |
| Fixed 10.375, first pair, 30k | 4,906,196 | 5,122,573 | +4.4% |
| Fixed 10.375, reversed pair, 30k | 5,588,820 | 4,835,888 | −13.5% |

We recorded `L1D_CACHE_MISS_LD_NONSPEC` with Instruments on the VM thread. The tagged run took 17.592 seconds in the second fixed-4 pair and 15.699 seconds in the first. The fixed-10.375 recordings matched the tagged variant's run count within 1%, including the profiling shell. L1D misses rose 4.4% in one pair and fell 13.5% in the other. We changed both pointer tagging and budget charge, so the experiment leaves the hardware prefetch hypothesis open.

| Final 100k comparison, midpoint of two runs | Baseline | Fixed 10.375 | Change |
| --- | ---: | ---: | ---: |
| Process-wide retired instructions | 412.704 billion | 391.359 billion | −5.17% |
| Process-wide CPU cycles | 65.378 billion | 65.422 billion | +0.07% |
| Instructions per cycle | 6.31 | 5.98 | −5.24% |
| Guest elapsed time | 15.0745 s | 15.0730 s | −0.01% |

The separate 30k wrapped runs measured a 4.87% reduction in retired instructions and a 0.36% increase in cycles inside `X86_Run` on the VM thread's performance cores. The final build's Mach-O `__text` grew from 1,811,248 to 1,914,020 bytes. The mean decoded-block cache allocation in the 100k batch increased from 534,890 to 574,130 bytes, with zero flushes in both builds.

## Code generation and interpretation

For a fallthrough exit, the baseline emitted `ldr` for the block pointer and `ldp` for the block header, then checked EIP and validity and unpacked the instruction count. The final fast path emitted `ldp x0, x8, [x21, #0x10]` for the handler and block pointers, then `sub x22, x22, #0xa6` to charge 166 units. Both paths still stored `last_block`, computed the next op address with `add x21, x8, #0x30`, and jumped with `br x0`.

The baseline can compute the next op address as soon as it has the block pointer. When the processor predicts the indirect jump, it can overlap the header load and checks with work in the successor. The prototype removes instructions while keeping the dependency from the block-pointer load to the next op and its operands. Each new `X86_Run` call reinitializes the register-resident MicroTLB. The cycle counts leave each effect's contribution unresolved.

Arm's Cortex-A75 guide distinguishes dependent-instruction latency from instruction throughput for that core. Instruments groups CPU bottlenecks by instruction delivery, instruction processing, and discarded work. [Arm Cortex-A75 Software Optimization Guide, section 3.1](https://documentation-service.arm.com/static/667bea7639267c50a1d5b638#page=13) · [Apple CPU bottleneck analysis](https://developer.apple.com/documentation/xcode/addressing-cpu-bottlenecks)

## Local artifacts

The original checkout stores the run logs, patches, and disassembly under these paths.

- `artifacts/same-page-links.kfVzn2/measured/` and `artifacts/fixed-target-links.46eSTp/measured/` contain the earlier prototype runs.
- `artifacts/coremark-pgo.132135/official-100k/` contains the separate PGO comparison.
- `artifacts/coremark-fixed4.gKE9x5/` contains the three-way pointer-tag comparison, VM call counts, fixed-4 L1D totals, patch and disassembly.
- `artifacts/coremark-aligned-budget.McvSTz/` contains the matched-budget L1D totals, final baseline comparisons in `vs-head-clean/` and `vs-head-100k/`, and the VM-only comparison in `vm-vs-head/`. `head-vs-aligned.asm` contains both fallthrough handlers.
- `artifacts/coremark-aligned-budget.McvSTz/page-local-links-before-revert.patch` preserves the reverted working-tree changes. The fixed-charge variants have their own experimental patches and source snapshots.
