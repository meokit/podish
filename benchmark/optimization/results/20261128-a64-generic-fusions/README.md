# AArch64 generic two-instruction fusion screen

Reference revision `6a086b13`. The retained change rejects overlapping GPR writebacks and duplicate GPR load-pair destinations during decode. Invalid encodings take the existing undefined-instruction trap. The post-index LDRB overlap check was still present at the reference revision. Its pin-aware handler went from 64 to 61 host instructions, and the unpinned handler went from 40 to 38, counting each full handler body. The reference already specialized post-index byte loads, EXTR rotates and ADD UXTB.

## Candidate screen

Each prototype ran independently against the reference on the same 17 workloads. The table shows the geomean of case ratios within each round. Early rounds showed large shared timing shifts across unrelated workloads. The later rounds and the independent TST comparison drove pruning. [Raw screen rounds](screen.json)

| Prototype | Native text delta | Round 1 | Round 2 | Round 3 | Round 4 | Decision |
|---|---:|---:|---:|---:|---:|---|
| ADDS / SUBS immediate + B.cond | +18,904 B | 1.218× | 1.107× | 0.997× | 0.985× | Dropped |
| ADDS / SUBS unshifted register + B.cond | +21,472 B | 1.221× | 1.143× | 0.940× | 0.989× | Dropped |
| TST immediate / unshifted register + B.EQ / B.NE | +4,200 B | 1.065× | 1.131× | 1.009× | 0.997× | Dropped |
| LDRB / LDR + CBZ / CBNZ | +16,788 B | 0.989× | 0.891× | 0.994× | 0.975× | Dropped |
| Decode-time transfer overlap checks | -552 B | 1.087× | 1.056× | 1.019× | 0.987× | Kept |

The arithmetic variants used EQ, NE, HS, LO, GE, LT, GT and LE with W and X operands. The load-and-branch variant tested offset, post-index and register-LSL addressing. Its unit test exercised a TLB hit, a miss with successful replay, and a fault at the load. LLVM objdump found zero stack adjustments, stack accesses or calls across the 188 prototype fusion handlers.

The separate four-round, alternating TST comparison measured 0.996× overall. [TST confirmation rounds](tst-confirmation.json)

## Final paired panel

Apple M5 Max, macOS 27.2.1, Homebrew Clang 21.1.8, dual-guest Release ThinLTO with LLVM JIT compiled in. Both arms used the same in-tree .NET 11.0.100 Release CLI, default 3 MiB baking plans and AArch64 rootfs. Guest JIT was disabled with `-XX:JitMode=off`. Host .NET tiered compilation was disabled in both arms to control the variability documented in the reference panel. Each timed pair held `/tmp/x86emu-agsci-benchmark.lock`. A fresh CLI ran each arm in five alternating A/B and B/A rounds. All 170 outputs passed the frozen SHA-256 checks. [Raw final rounds](final.json), [workload manifest](../20261126-default-3m/measurements.json)

Speedup is the geomean of per-case medians of the five paired reference-to-candidate time ratios. Each workload has equal weight.

| Panel | Workloads | Speedup |
|---|---:|---:|
| Training | 8 | 1.000× |
| Regression | 5 | 1.006× |
| Blind | 2 | 0.999× |
| Compiler transfer | 2 | 1.015× |
| Overall | 17 | 1.003× |

| Workload | Reference median ms | Candidate median ms | Paired speedup | Wins |
|---|---:|---:|---:|---:|
| gzip | 210.07 | 208.24 | 1.009× | 5/5 |
| gunzip | 31.85 | 31.52 | 1.016× | 4/5 |
| sed | 112.84 | 113.01 | 0.995× | 2/5 |
| sort | 29.07 | 29.17 | 0.993× | 2/5 |
| sha256 | 27.12 | 27.06 | 1.002× | 3/5 |
| tar | 11.56 | 11.57 | 0.999× | 2/5 |
| du | 29.29 | 29.18 | 0.999× | 2/5 |
| ls | 12.50 | 12.66 | 0.989× | 0/5 |
| grep-fixed | 11.70 | 11.59 | 1.004× | 4/5 |
| grep-regex | 21.41 | 21.25 | 1.000× | 2/5 |
| node-minify | 1358.03 | 1364.32 | 0.995× | 1/5 |
| mawk-aggregate | 112.78 | 109.96 | 1.023× | 4/5 |
| diff-revisions | 30.70 | 30.38 | 1.008× | 4/5 |
| blind-perl-groups | 299.42 | 299.98 | 0.996× | 1/5 |
| blind-rev-lines | 278.56 | 277.00 | 1.001× | 3/5 |
| codegen-gzip-o1 | 35.78 | 35.86 | 1.013× | 3/5 |
| codegen-gzip-o3 | 36.81 | 36.19 | 1.017× | 4/5 |

## Size and verification

| Measure | Reference | Candidate | Delta |
|---|---:|---:|---:|
| Native `__text` | 3,992,300 B | 3,991,748 B | -552 B |
| Wasm raw | 2,402,842 B | 2,402,396 B | -446 B |
| Wasm Code section | 2,139,841 B | 2,139,395 B | -446 B |
| Wasm Brotli-11 | 345,870 B | 345,461 B | -409 B |

Both Wasm arms used the Emscripten 5.0.4 dual-guest static library and the same standalone ThinLTO reactor link. The link rooted creation, destruction, i386 execution and AArch64 reset and execution through the public exports.

- Native arm64 LLVM JIT CTest passed 24/24. The overlap test checked that single-step and block dispatch trap before accessing memory or changing registers.
- Native differential passed 276 encodings with 80 states each, plus 82 baked handlers, 8 invalid encodings and 22 dead-NZCV handlers.
- .NET 11 passed 3,276/3,276. Python passed 48/48.
- Emscripten static build, standalone Wasm link and Wasm validation passed.
- LLVM objdump found zero stack adjustments, stack accesses or BL/BLR calls across all 228 native generic load/store handlers.
