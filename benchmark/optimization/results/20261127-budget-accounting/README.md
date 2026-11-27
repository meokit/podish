# Experiment 9 — instruction budget accounting

**Recommendation.** Use direction A for the shared scheduling policy. It reduced AArch64 interpreter-only time in five of six rounds and lowered the paired median total by 3.33%. The AArch64 region result was 2.70% slower, with a 1.50% slowdown in GM-9. Direction B increased i386 interpreter-only time in every round, added more native text, and raised peak RSS in both i386 modes. Its region total fell by 3.46%, while GM-9 rose by 11.50%. The i386 region control under A also showed large per-workload swings, which limits attribution in that mode. The strongest repeated timing signal favored A in interpreter-only mode.

## Implementations

- **AArch64 → i386 (A).** The scheduler uses signed, block-granular budgets. It charges the whole next block while budget remains and stops at the next boundary. AArch64 blocks contain at most 24 instructions, so the final block can retire up to 23 instructions beyond the quantum. Public AArch64 `Run` calls, explicit stop addresses, and one-instruction runs keep exact stops. Exact dispatch reserves 23 instructions and switches to an exact check on the cold link path and to `DispatchSpan` when a block only partly fits.
- **i386 → AArch64 (B).** Interpreter links, slow links, and LLVM region edges check the target count before entry. The top-level dispatcher uses the existing stack decoder when the full block exceeds the caller's remaining budget. The test variant also caps terminal REP by the budget left after its block prefix. The reference i386 count treats absorbed NOPs and eliminated concatenation jumps as zero-cost. The preflight keeps that count and the existing block layout. Local experiment revision `ae8dd4ea`.

In AArch64's native fallthrough hit, A replaces `cbz` with a sign-bit `tbnz` and drops the target-count conditional comparison. In `OpExitBlock<false>`, the ordinary cached path from the budget test through the tail jump shrinks from 14 to 13 host instructions. The LLVM IR uses a sign check for ordinary region edges. The exact reserve check branches off that path. In i386's native fallthrough hit, B extracts the target's instruction count and adds a `ccmp` before taking the link. The LLVM region compares against a constant count for a known target. A dynamic target switch places the count check on the selected member edge.

## Test contracts

- AArch64 native conformance checks exact retired counts, PC and NZCV at each stopping position, including compact NOPs, fused operations, refetches and separate pages. The LLVM tests check region and external-link budgets, memory replay and invalidation. The added budget-mode test exercises precise, single-instruction and overdraw runs before and after compilation. [Native instruction tests](../../../decode/aarch64_test.cpp) · [Native LLVM tests](../../../decode/aarch64_jit_test.cpp)
- Managed AArch64 tests check exact same-page and cross-page cached successors, code-cache flushes, fault addresses and one-instruction calls. The task scheduler supplies quanta of 250,000, 1,000,000 or 10,000,000 instructions. Under A, the running task can execute up to 23 more guest instructions before rescheduling. [MMU tests](../../../../Fiberish.Tests/Core/EngineMmuTransferTests.cs) · [Scheduler](../../../../Fiberish.Core/Core/KernelScheduler.cs)
- The i386 managed harness uses `X86_Step` for precise instruction boundaries. Bounded `Run` tests exercise cold faults and SMC, and REP tests check exact iterations, restart EIP, and the independent long-string yield. B adds a warmed prefix-plus-REP test and an exact stop inside a warmed LLVM region member. The native `llvm_block_budget` case covers the JIT executable arena allocation limit. [Managed CPU test contract](../../../../Fiberish.X86.Tests/README.md) · [REP tests](../../../../Fiberish.X86.Tests/Regression/RepStringOperationTests.cs) · [Native LLVM tests](../../../decode/llvm_block_test.cpp)

Both candidates passed 24 CTest cases. `Fiberish.X86.Tests` passed 3,276 cases on A and 3,277 on B. The focused `Fiberish.Tests` MMU and scheduler run reproduced the same two i386 cache-allocation test failures on the baseline and A. The specialization index test needed a build-local `INFINITY` definition for Homebrew Clang's libc++ headers on this macOS SDK. The benchmark libraries were built with the usual compiler flags.

## Measurement

Apple M5 Max, macOS 27.2.1, Homebrew Clang/LLVM 21, Release ThinLTO, dual guests, stock 3m baked tables. The native baseline was rebuilt from `c2ff6c4a`. Both modes used the same dylib for a given variant, a 400 MiB block cache, default region worker settings, and disabled dynamic specialization and linked caches. A used a rebuilt managed CLI to call the scheduling API. Baseline and B reused the supplied managed CLI.

Six alternating rounds covered all 12 combinations. Even rounds reversed the entire run order. The first variant rotated between pairs of rounds. Each invocation used a fresh guest CLI process and the shared benchmark lock. The harness checked all 720 GNU tool outputs against the manifest's SHA-256 digests. [Workload manifest](https://github.com/meokit/podish/blob/12f9f5e68a1be159b5633e3ea7a131e2051662d7/benchmark/optimization/manifests/baking-workloads.json) · [Raw timings, memory and native binary hashes](measurements.json)

The ten-workload sum is the median of the per-round sums between workload markers. Changes use paired ratios relative to the baseline in the same round. GM-10 and GM-9 are the geometric means of the paired median per-workload ratios, with GM-9 excluding `du`. The peak RSS column shows the median and range from `/usr/bin/time -l`. The whole CLI measurement includes setup and shutdown. Negative time changes are faster.

## Results

Both builds use the existing 32-byte fixed block header and 16-byte decoded slots on both guests. A adds a signed reserve to the AArch64 stack run frame. The native text and Mach-O segment measurements apply to both modes.

| Guest | Mode | Build | 10-workload sum (s) | Paired sum change | GM-10 change | GM-9 change | Whole CLI (s) | Peak RSS (MiB) |
|---|---|---|---:|---:|---:|---:|---:|---:|
| AArch64 | off | Baseline | 1.329 | +0.00% | +0.00% | +0.00% | 1.58 | 145.7 [143.8–146.2] |
| AArch64 | off | A | 1.278 | -3.33% | -1.97% | -2.32% | 1.52 | 144.7 [143.4–145.6] |
| AArch64 | off | B | 1.312 | -1.62% | -0.48% | -0.29% | 1.56 | 145.1 [143.5–145.8] |
| AArch64 | region | Baseline | 1.798 | +0.00% | +0.00% | +0.00% | 2.08 | 243.4 [242.6–246.3] |
| AArch64 | region | A | 1.831 | +2.70% | +1.97% | +1.50% | 2.10 | 243.3 [242.4–244.8] |
| AArch64 | region | B | 1.779 | +0.41% | -1.11% | -1.13% | 2.05 | 244.7 [242.5–247.7] |
| i386 | off | Baseline | 1.963 | +0.00% | +0.00% | +0.00% | 2.21 | 149.7 [145.6–150.0] |
| i386 | off | A | 1.962 | -0.23% | -0.07% | -0.36% | 2.21 | 148.8 [145.5–149.8] |
| i386 | off | B | 2.021 | +2.76% | +2.36% | +2.51% | 2.27 | 152.4 [148.2–153.0] |
| i386 | region | Baseline | 3.358 | +0.00% | +0.00% | +0.00% | 3.64 | 517.2 [505.7–524.2] |
| i386 | region | A | 3.242 | +0.77% | +12.94% | +16.43% | 3.63 | 512.2 [506.8–516.9] |
| i386 | region | B | 3.200 | -3.46% | +6.23% | +11.50% | 3.60 | 525.3 [519.1–534.3] |

| Build | `__text` (B) | Change (B) | `__TEXT` (B) | Dylib on disk (B) |
|---|---:|---:|---:|---:|
| Baseline | 6,239,856 | +0 | 14,204,928 | 26,683,440 |
| A | 6,252,748 | +12,892 | 14,221,312 | 26,700,224 |
| B | 6,260,164 | +20,308 | 14,237,696 | 26,724,544 |

| Workload | AArch64 off A | AArch64 off B | AArch64 region A | AArch64 region B | i386 off A | i386 off B | i386 region A | i386 region B |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| gzip | -1.71% | -0.66% | -6.11% | +0.96% | +0.05% | +0.98% | -0.45% | +17.73% |
| gunzip | -1.52% | +0.66% | -1.88% | -3.84% | -0.56% | +0.00% | -0.56% | +0.48% |
| sed | -3.91% | -1.15% | -1.50% | +3.34% | -0.12% | -1.03% | +0.73% | -24.76% |
| grep-fixed | -1.30% | -0.39% | -1.06% | +5.41% | -0.33% | +1.44% | +82.41% | +26.90% |
| grep-regex | -1.54% | -0.70% | -0.81% | +10.56% | -0.65% | +0.30% | -2.91% | +2.02% |
| sort | -1.08% | +1.20% | +5.91% | -4.65% | +0.31% | -0.16% | +30.11% | +51.14% |
| sha256 | -4.17% | +0.24% | -1.94% | +0.95% | -2.14% | +6.84% | -9.21% | +20.99% |
| tar | +0.17% | +0.02% | +10.55% | -7.86% | -0.21% | -1.00% | +67.59% | +10.82% |
| du | +1.22% | -2.22% | +6.36% | -0.87% | +2.55% | +0.99% | -14.09% | -31.27% |
| ls | -5.65% | -1.77% | +11.81% | -12.96% | +0.45% | +16.42% | +12.42% | +14.06% |

| Guest | Mode | Build | Round 1 | Round 2 | Round 3 | Round 4 | Round 5 | Round 6 |
|---|---|---|---:|---:|---:|---:|---:|---:|
| AArch64 | off | A | -11.21% | -4.50% | -8.91% | -2.17% | -1.01% | +1.06% |
| AArch64 | off | B | -2.20% | +5.85% | -4.38% | -1.05% | +2.13% | -4.13% |
| AArch64 | region | A | -3.71% | +5.03% | +7.20% | +3.54% | -1.55% | +1.86% |
| AArch64 | region | B | -0.45% | +4.74% | +1.27% | -3.57% | +1.90% | -5.24% |
| i386 | off | A | -0.70% | +2.31% | -2.37% | +0.24% | -1.54% | +3.24% |
| i386 | off | B | +1.63% | +1.35% | +1.02% | +4.00% | +3.89% | +5.45% |
| i386 | region | A | -2.09% | -7.01% | +10.69% | +3.63% | +22.13% | -7.78% |
| i386 | region | B | -5.20% | -1.71% | +6.33% | -9.47% | +22.16% | -8.27% |
