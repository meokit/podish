# Experiments 12 and 13 — decoded block limits and concatenation

Use a shared cap of **32 instructions** and remove static concatenation. The final build uses independent decoded blocks on both guests and preserves the native header offsets consumed by the managed CLI. It uses the existing direct links and LLVM regions to join execution across blocks.

With concatenation enabled, reducing i386 from 64 to 24 increased the `off` total by 3.39% and `region` GM-9 by 11.02%. A cap of 32 limited those changes to 1.58% and 0.81%. AArch64's original 24 cap had the lowest paired `region` total. On AArch64, 32 kept the 32-bit binding masks and stayed within 0.1% of baseline allocated bytes in both modes. The 64 cap increased allocated bytes by 3.6% in `off` and 3.5% in `region` and had larger `region` increases than 32 on all three time aggregates.

Removing i386 concatenation at 64 cut cumulative block bytes by 43.92% in `off` and 43.33% in `region`. The `off` total changed by −0.05% and GM-9 fell 3.38%. In `region`, the total rose 1.56%, GM-9 fell 4.13% and worker CPU rose 9.71%. The reverse AArch64 prototype took longer in all five `off` rounds and had nearly identical block bytes. Its `__text` grew by approximately 40 KB in each build.

The final i386 `region` total fell 4.61% and `off` rose 2.12%. The AArch64 totals rose 1.84% in `off` and 0.62% in `region`. The i386 `region` paired totals ranged from −16.96% to +3.75%. The final `__text` grew by 10,948 B in `off` and shrank by 4,680 B in `region`.

## Implementations

- **Block limits.** The baseline caps i386 decode and static concatenation at 64 instructions and AArch64 decode at 24. The i386 sweep changes both decode and concatenation. The AArch64 sweep preserves 32-bit binding masks at 32 and uses 64-bit scalar, fusion and dead-flag masks at 64. The 64-instruction build packs the seventh count bit into an aligned guest PC bit.
- **A, remove i386 concatenation.** Decode and cache each block independently. Remove the anchor bitmap and PC adjustments, and reconstruct guest EIPs from contiguous instruction lengths. Existing direct links and LLVM page regions connect the blocks.
- **B, add AArch64 concatenation.** Within the current executable page, follow one forward unconditional `B` during decode. Keep the branch's word and retired instruction ordinal while omitting its dispatch slot. One offset maps the successor's ordinals back to their guest PCs for interpreter replay and LLVM lowering.
- **Combined candidate.** Apply A at a shared cap of 32. Outline the i386 native handlers' cold EIP recovery because the first removal build expanded generated dispatch handlers during ThinLTO. LLVM can still inline EIP recovery and bind the current region member.

## Measurement

Apple M5 Max, macOS 27.2.1, Homebrew Clang and LLVM 21, Release ThinLTO, dual guest library, and stock `3m` baked tables. This rebuilds `b255c1c2` with the same toolchain and uses the matching managed CLI. The `off` build disables host LLVM. The `region` build uses two workers. Both use a 400 MiB block cache with dynamic specialization and linked caches disabled.

Each comparison has five timed rounds. Even rounds reverse the build order. Every invocation gets a fresh CLI process and checks all ten GNU outputs against the manifest's SHA-256 digests. Compilations take the shared benchmark lock and each group of matched runs takes the exclusive lock. Across all comparisons and diagnostic runs the harness verified 2,080 workload outputs. [Workload manifest](https://github.com/meokit/podish/blob/12f9f5e68a1be159b5633e3ea7a131e2051662d7/benchmark/optimization/manifests/baking-workloads.json)

Time is in seconds between the guest markers, including each workload's configured repetitions. Sum, GM-10 and GM-9 are the medians of the per-round quantities. GM-9 excludes `du`. Parentheses give the median paired change against the corresponding baseline. Negative time changes are faster. The paired calculation can give a different sign from comparing the two absolute medians.

Counters are medians from three additional alternating diagnostic rounds. Block bytes are cumulative requested native block allocations. Direct links count dispatches, so their level also reflects how often a candidate ends an interpreted block. Worker CPU is the native scheduler trace total in nanoseconds. `__text` measures the uninstrumented dual-guest Mach-O section. [Raw samples](measurements.json)

## Block cap sweep

| Guest | Mode | Variant | Sum (s) | GM-10 (s) | GM-9 (s) | Block allocations | Block bytes | Direct links | Worker CPU (ns) | `__text` (B) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| i386 | off | base | 1.835 (+0.00%) | 0.147 (+0.00%) | 0.134 (+0.00%) | 234,875 | 45,490,768 | 198,261,534 | — | 5,544,964 |
| i386 | off | max24 | 1.910 (+3.39%) | 0.150 (+1.57%) | 0.137 (+1.98%) | 235,009 | 44,354,838 | 212,894,242 | — | 5,544,964 |
| i386 | off | max32 | 1.873 (+1.58%) | 0.148 (+1.14%) | 0.135 (+1.14%) | 235,019 | 44,918,274 | 205,604,420 | — | 5,545,392 |
| i386 | region | base | 3.173 (+0.00%) | 0.277 (+0.00%) | 0.262 (+0.00%) | 234,875 | 52,855,104 | 197,642,585 | 3,071,822,046 | 6,258,388 |
| i386 | region | max24 | 3.222 (+1.55%) | 0.249 (+6.25%) | 0.225 (+11.02%) | 235,009 | 51,594,864 | 212,524,899 | 2,965,652,251 | 6,258,360 |
| i386 | region | max32 | 3.206 (+7.34%) | 0.263 (+0.37%) | 0.248 (+0.81%) | 235,019 | 52,221,136 | 205,465,588 | 3,112,671,250 | 6,258,816 |
| aarch64 | off | base | 1.351 (+0.00%) | 0.110 (+0.00%) | 0.099 (+0.00%) | 138,062 | 27,418,424 | 259,167,834 | — | 5,544,964 |
| aarch64 | off | max32 | 1.344 (+1.17%) | 0.110 (+1.20%) | 0.099 (-0.78%) | 137,637 | 27,399,221 | 256,264,869 | — | 5,545,392 |
| aarch64 | off | arm64 | 1.342 (+2.11%) | 0.110 (-0.28%) | 0.100 (-0.09%) | 137,354 | 28,413,155 | 252,422,985 | — | 5,557,736 |
| aarch64 | region | base | 1.752 (+0.00%) | 0.149 (+0.00%) | 0.135 (+0.00%) | 138,062 | 28,522,920 | 225,592,890 | 2,266,097,164 | 6,258,388 |
| aarch64 | region | max32 | 1.781 (+2.65%) | 0.152 (+2.05%) | 0.137 (+1.27%) | 137,632 | 28,499,243 | 223,965,141 | 2,180,663,998 | 6,258,816 |
| aarch64 | region | arm64 | 1.799 (+3.10%) | 0.157 (+3.86%) | 0.142 (+4.03%) | 137,353 | 29,511,911 | 221,474,588 | 2,346,099,831 | 6,298,524 |

## Static concatenation in both directions

| Guest | Mode | Variant | Sum (s) | GM-10 (s) | GM-9 (s) | Block allocations | Block bytes | Direct links | Worker CPU (ns) | `__text` (B) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| i386 | off | base | 1.842 (+0.00%) | 0.147 (+0.00%) | 0.134 (+0.00%) | 234,875 | 45,490,768 | 198,261,627 | — | 5,544,964 |
| i386 | off | no-concat | 1.838 (-0.05%) | 0.143 (-3.05%) | 0.130 (-3.38%) | 155,200 | 25,510,970 | 281,515,496 | — | 5,944,840 |
| i386 | region | base | 3.192 (+0.00%) | 0.258 (+0.00%) | 0.235 (+0.00%) | 234,875 | 52,855,104 | 197,640,503 | 3,036,180,997 | 6,258,388 |
| i386 | region | no-concat | 3.068 (+1.56%) | 0.247 (-1.88%) | 0.225 (-4.13%) | 155,200 | 29,954,552 | 269,972,060 | 3,331,070,541 | 6,641,792 |
| aarch64 | off | base | 1.330 (+0.00%) | 0.109 (+0.00%) | 0.099 (+0.00%) | 138,062 | 27,418,424 | 259,167,640 | — | 5,544,964 |
| aarch64 | off | arm-concat | 1.374 (+0.61%) | 0.112 (+0.30%) | 0.100 (+0.48%) | 137,959 | 27,418,694 | 259,044,743 | — | 5,584,956 |
| aarch64 | region | base | 1.749 (+0.00%) | 0.151 (+0.00%) | 0.136 (+0.00%) | 138,060 | 28,522,421 | 228,538,556 | 2,320,023,634 | 6,258,388 |
| aarch64 | region | arm-concat | 1.767 (-0.10%) | 0.151 (-0.33%) | 0.135 (+1.06%) | 137,963 | 28,523,364 | 228,121,018 | 2,320,531,949 | 6,298,668 |

## Final combination

| Guest | Mode | Variant | Sum (s) | GM-10 (s) | GM-9 (s) | Block allocations | Block bytes | Direct links | Worker CPU (ns) | `__text` (B) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| i386 | off | base | 1.822 (+0.00%) | 0.146 (+0.00%) | 0.133 (+0.00%) | 234,875 | 45,490,768 | 198,261,542 | — | 5,544,964 |
| i386 | off | final32 | 1.862 (+2.12%) | 0.143 (-1.43%) | 0.130 (-1.47%) | 155,976 | 25,548,200 | 287,661,728 | — | 5,555,912 |
| i386 | region | base | 3.281 (+0.00%) | 0.255 (+0.00%) | 0.234 (+0.00%) | 234,875 | 52,855,104 | 197,635,941 | 3,257,664,459 | 6,258,388 |
| i386 | region | final32 | 3.065 (-4.61%) | 0.233 (-8.98%) | 0.212 (-11.18%) | 155,977 | 30,004,280 | 283,737,933 | 3,356,190,286 | 6,253,708 |
| aarch64 | off | base | 1.351 (+0.00%) | 0.110 (+0.00%) | 0.099 (+0.00%) | 138,062 | 27,418,424 | 259,167,834 | — | 5,544,964 |
| aarch64 | off | final32 | 1.291 (+1.84%) | 0.107 (+1.33%) | 0.097 (-1.55%) | 137,635 | 27,398,738 | 256,265,081 | — | 5,555,912 |
| aarch64 | region | base | 1.752 (+0.00%) | 0.149 (+0.00%) | 0.135 (+0.00%) | 138,062 | 28,522,920 | 225,592,890 | 2,266,097,164 | 6,258,388 |
| aarch64 | region | final32 | 1.770 (+0.62%) | 0.153 (+2.09%) | 0.138 (+2.18%) | 137,634 | 28,499,742 | 231,152,330 | 2,344,189,709 | 6,253,708 |

## Verification

The final source passed all 24 CTest cases, including i386 and AArch64 interpreter and LLVM tests, and all 3,276 cases in `Fiberish.X86.Tests`. The specialization index test used a build-local `INFINITY=__builtin_inff()` definition for Homebrew Clang's libc++ headers on this macOS SDK. The timed builds used the standard release flags. The updated regressions cover Jcc fallthrough, EIP at a fault after a direct jump, and invalidation of reused decoded blocks.
