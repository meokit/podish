# Handler ABI and GPR write-back on macOS ARM64

## Decision

Use direction B for the software data TLB. AArch64 loads the active table pointer from `State`, and the i386 TLB remains embedded in its MMU. This gives both interpreters state-derived L2 lookups. The micro-TLB tags and addend stay in the continuation arguments. i386 continues to pass `branch` for branch targets and the LLVM region entry selector. AArch64's slow link resolver uses its run frame for the exit edge and target.

Direction B costs 0.78% in the paired interpreter suite total and saves 2.02% in region mode. Direction A increases the paired totals by 2.10% and 1.67%, respectively, and adds 23,544 B of native text versus 9,240 B for B. A's i386 region Geo9 varied from −23.41% to +37.17% between rounds. B's AArch64 region Geo9 varied from −6.44% to +2.49%.

Keep AArch64 GPR pinning opt-in and retain the i386 EAX/ESP write-back default. The AArch64 implementation adopts the same write-back protocol as i386. Hot handlers forward authoritative register values and publish or reload them at exits, scalar paths and memory replay. Each guest selects its pin list and implements its own architectural register semantics. AArch64 distinguishes SP from the zero register and preserves W-register zero extension.

In the AArch64 interpreter, the three tested pin lists each improve Geo9 by about 2%. All three have higher paired Geo9 in region mode. SP alone has the smallest text section and the largest interpreter Geo9 gain. With i386 pinning disabled, gzip takes 6.16% longer in the interpreter, and the paired suite total rises by 0.81%. Its region suite total and RSS drop, while Geo10 rises by 0.77%. The per-case and per-round region spread makes it difficult to generalize the result across execution modes.

## Method

The baseline was `c2ff6c4a`. Every timing library used both guests, Release, LTO, the `3m` baking preset and Homebrew Clang 21.1.8 on an Apple M5 Max running macOS 27.2.1. The build enabled LLVM JIT so each binary could run both `JitMode=off` and `JitMode=region`. Both modes used a 400 MiB block cache, 1 GiB guest memory and the default JIT scheduling settings. Region timings include compilation and execution. The managed CLI was the same .NET 11 build for every binary.

Each panel and mode has five paired rounds, with configuration order reversed on even rounds. Every sample ran gzip, gunzip, sed, grep-fixed, grep-regex, sort, sha256, tar, du and ls at their manifest repetition counts. All 900 canonical output hashes matched. Builds took shared locks and timing took exclusive locks on `/tmp/x86emu-agsci-benchmark.lock`. Completed early rounds were retained when the remaining rounds moved from per-round to per-mode lock acquisition. [Workload manifest](https://github.com/meokit/podish/blob/12f9f5e68a1be159b5633e3ea7a131e2051662d7/benchmark/optimization/manifests/baking-workloads.json) · [Raw timings, peak RSS and build hashes](measurements.json)

The absolute columns are medians across five rounds. Sum is the ten shell marker intervals added together. Process is the elapsed wall time of the full CLI invocation. Sum Δ is the median of paired total time ratios. Geo10 and Geo9 are the medians of paired workload-ratio geometric means, with Geo9 excluding `du`. Negative deltas mean less elapsed time. Peak RSS measures the host CLI process. `__text` is the linked dual-guest Mach-O section measured by `size -m`.

## #7 ABI directions and #8 i386 reverse control

Direction A adds a const software TLB pointer to i386 threaded continuations and forwards it into L2 probes. Its pin list is the same as the baseline. Direction B removes that argument from AArch64, reads the table from CPU state in the interpreter and loads that field on LLVM entry. A scoped binding supplies the pointer for each run and restores it after reentry. Baseline AArch64 and direction B in this panel have pinning disabled. Each direction was timed on the guest whose ABI it changes.

### i386

| JIT | Configuration | Sum, s | Process, s | Sum Δ | Geo10 Δ | Geo9 Δ | Peak RSS, MiB | `__text`, B |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| off | Baseline | 1.941 | 2.24 | — | — | — | 149.09 | 6,234,556 |
| off | A · TLB argument | 1.999 | 2.27 | +2.10% | +1.74% | +1.33% | 145.30 | 6,258,100 |
| off | x86 pin off | 1.971 | 2.22 | +0.81% | -0.05% | -0.79% | 150.09 | 6,129,796 |
| region | Baseline | 3.163 | 3.51 | — | — | — | 513.92 | 6,234,556 |
| region | A · TLB argument | 3.259 | 3.60 | +1.67% | -2.51% | -4.42% | 511.88 | 6,258,100 |
| region | x86 pin off | 3.147 | 3.43 | -6.17% | +0.77% | -4.39% | 471.55 | 6,129,796 |

### AArch64

| JIT | Configuration | Sum, s | Process, s | Sum Δ | Geo10 Δ | Geo9 Δ | Peak RSS, MiB | `__text`, B |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| off | Baseline | 1.380 | 1.63 | — | — | — | 144.69 | 6,234,556 |
| off | B · TLB in state | 1.391 | 1.63 | +0.78% | +1.03% | +0.87% | 144.16 | 6,243,796 |
| region | Baseline | 1.817 | 2.09 | — | — | — | 245.73 | 6,234,556 |
| region | B · TLB in state | 1.762 | 2.03 | -2.02% | -2.47% | -2.30% | 244.84 | 6,243,796 |

Each direction leaves the decoded slot and block header sizes intact. A leaves the persistent CPU state footprint intact. B adds 8 B to each AArch64 CPU state, from 848 B to 856 B. The pin lists use continuation arguments and add zero bytes to persistent CPU state.

## #8 AArch64 pinning with direction B

| JIT | Configuration | Sum, s | Process, s | Sum Δ | Geo10 Δ | Geo9 Δ | Peak RSS, MiB | `__text`, B |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| off | B · pin off | 1.360 | 1.63 | — | — | — | 143.47 | 6,243,796 |
| off | B · SP | 1.356 | 1.61 | -2.80% | -2.46% | -2.40% | 144.00 | 6,240,576 |
| off | B · SP + X0 | 1.379 | 1.65 | -1.47% | -1.72% | -2.16% | 144.00 | 6,242,600 |
| off | B · SP + X0 + X1 | 1.327 | 1.57 | -3.97% | -2.84% | -2.18% | 144.27 | 6,251,192 |
| region | B · pin off | 1.764 | 2.04 | — | — | — | 244.16 | 6,243,796 |
| region | B · SP | 1.804 | 2.08 | +2.93% | +3.50% | +4.20% | 245.00 | 6,240,576 |
| region | B · SP + X0 | 1.802 | 2.08 | +2.14% | +2.17% | +1.27% | 243.67 | 6,242,600 |
| region | B · SP + X0 + X1 | 1.756 | 2.03 | +0.88% | +1.82% | +2.30% | 244.59 | 6,251,192 |

## Register allocation

| macOS ARM64 `preserve_none` | TLB pointer | µTLB tags, addend | Flags | Guest GPR pins |
| --- | --- | --- | --- | --- |
| i386 baseline | From state | `x23`, `x24` | `x26` | EAX `w27`, ESP `w28` |
| i386 A | `x27` | `x23`, `x24` | `x26` | EAX `w28`, ESP `w0` |
| AArch64 baseline | `x23` | `x24`, `x25` | `w26` | Off |
| AArch64 B | From state | `x23`, `x24` | `w25` | SP `x26`, X0 `x27`, X1 `x28` when configured |

In the i386 baked `MOV Gv,Ev` sample, A replaces the state-based addition and offset loads with an addition from `x27` and loads at offsets zero and eight. Baseline folded the MMU field offset into the loads. The instruction count of this L2 probe stays the same. The continuation branch moves from `br x0` to `br x1` because ESP occupies `w0`. The sampled MOV and four-POP SuperOpcode have no host-stack references in either build.

In AArch64's baked `ldr x0, [x0, #8]`, B adds `ldr x9, [x20, #0x350]` at L2 entry. The L1 path uses the same micro-TLB data. Pinning X0 removes the load and store of X0 from the register file on a hit and reduces this handler from 108 B to 96 B. The generic 64-bit store-pair handler is 268 B with B and pins off, 264 B with SP, 296 B with SP and X0, and 336 B with SP, X0 and X1. The sampled AArch64 handlers have no host-stack references. Their size differences illustrate the costs of variable-register selection and the benefits of exact operand binding.

## Verification

The final native build with SP, X0 and X1 pinned passed all 24 CTest cases. Coverage includes AArch64 instruction and MMU conformance, i386 decode and specialization, and exact and region JIT on both guests. AArch64 region conformance ran with zero and two JIT workers. A new test exercises both meanings of register encoding 31 in one threaded block.

The CTest build used a temporary test-only `INFINITY` definition to work around the Clang 21 and SDK header error in `<random>`. The workaround was applied in the verification checkout.

The final Release `3m` preset build with LLVM compiled out and pins off passed both AArch64 CTest cases. Its native text section measured 5,552,004 B. The same library passed all 3,276 tests from `dotnet test Fiberish.X86.Tests -c Release -p:FiberishSkipNativeBuild=true`.
