# AArch64 baking preset retraining

The compact `1.5m` and `3m` presets use the sequence-lean hierarchy. The original 18,168-binding positive-marginal ceiling remains the `10m` tier. Membership is nested across all four binding classes.

The reference is the AArch64 `3m` preset at `3035ce51`. Measurements used an Apple M5 Max, macOS 27.2.1, Homebrew Clang 21.1.8 and Release ThinLTO. The [initial retraining measurements](measurements.json) include the larger plans from `d52e08d3`.

## Candidate selection

Method C (`joint-general`, risk 0.5) used fresh, locked AArch64 captures of gzip, gunzip, sed, sort, sha256, tar, du and ls. Regression, blind and compiler-transfer workloads were evaluated after candidate membership was fixed. The reconstructed positive prefix shared 18,070 of its 18,168 bindings with the committed `10m` plan. Both designs selected from those shared bindings and preserved the complete original `10m` plan.

The prefix designs stop at about 0.90 and 1.30 MiB of native text growth. The sequence-lean design targets about 0.80 and 1.15 MiB and caps sequence counts at 1,850 and 2,600. It multiplies scalar partial and dynamic-pair ranking positions by 0.55 and retains the smaller tier when filling the larger one. Candidate costs were fitted from three linked trial plans using `scripts/baking_rank_cost.py`.

| Candidate | Native growth (B) | Brotli-11 (B) | Training | Regression | Blind | Compiler transfer | Overall |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| prefix 1.5m | 962,392 | 398,285 | 0.995× | 1.006× | 1.043× | 1.002× | 1.005× |
| prefix 3m | 1,368,000 | 468,808 | 0.986× | 1.001× | 1.058× | 0.997× | 1.000× |
| lean 1.5m | 868,344 | 364,406 | 0.994× | 1.011× | 1.040× | 0.997× | 1.005× |
| lean 3m | 1,228,892 | 429,213 | 0.987× | 1.003× | 1.054× | 0.998× | 1.001× |

The sequence-lean hierarchy kept smaller native and Wasm builds in both tiers. Its default reached 1.001× overall, 1.003× on Regression and 1.054× on Blind. Compiler transfer was 0.998×. The compact default improved on the earlier 0.994× overall and 0.966× compiler-transfer results against the same reference. Short workloads still had regressions, including tar and ls in both tiers.

## Preset composition

| AArch64 preset | Entries | Scalar partials | Exact pairs | Dynamic pairs | Sequences |
| --- | ---: | ---: | ---: | ---: | ---: |
| old `3m` | 4,212 | 504 | 1,849 | 0 | 2,411 |
| `1.5m` | 4,715 | 805 | 2,403 | 819 | 1,850 |
| `3m` | 6,329 | 805 | 3,434 | 819 | 2,600 |
| `10m` | 6,485 | 807 | 3,472 | 825 | 6,579 |

## Native size

All three single-guest growth checks and the default dual-guest `3m` growth check passed against matched unbaked libraries.

| Native build | `__text` (B) | Growth (B) | Limit (B) |
| --- | ---: | ---: | ---: |
| AArch64 unbaked | 320,072 | 0 | — |
| AArch64 old `3m` | 960,144 | 640,072 | — |
| AArch64 new `1.5m` | 1,188,416 | 868,344 | 1,572,864 |
| AArch64 new `3m` | 1,548,964 | 1,228,892 | 3,145,728 |
| AArch64 `10m` | 2,543,476 | 2,223,404 | 10,485,760 |
| Dual guest unbaked | 782,860 | 0 | — |
| Dual guest default `3m` | 3,763,636 | 2,980,776 | 6,291,456 |
| Dual guest `3m` with LLVM JIT | 4,408,224 | — | — |

## Wasm size

Emscripten 5.0.4 built dual-guest static archives and linked standalone Release ThinLTO reactors with creation, destruction, i386 execution, and AArch64 reset and execution exported. CODE, DATA and ELEM sizes are section payload bytes.

| Preset | Static archive (B) | Raw Wasm (B) | CODE (B) | DATA (B) | ELEM (B) | Brotli-11 (B) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| old `3m` | 15,679,530 | 2,380,900 | 2,143,332 | 209,366 | 18,182 | 337,869 |
| new `1.5m` | 19,265,992 | 2,759,184 | 2,474,843 | 245,348 | 25,530 | 364,406 |
| new `3m` | 24,252,192 | 3,453,108 | 3,094,321 | 309,279 | 32,628 | 429,213 |

## Paired interpreter benchmarks

Each candidate ran five alternating pairs against old `3m` with the AArch64-only libraries, the same staged .NET 11 CLI, `-XX:JitMode=off`, a 64 MiB block cache, 1 GiB of guest memory and host .NET tiered compilation disabled. Every timed pair held `/tmp/x86emu-agsci-benchmark.lock`. Every comparison passed all 170 output SHA-256 checks. Speedups are medians of paired old-to-new ratios. Panels use the geometric mean of their workload speedups.

| Panel | Cases | New `1.5m` | New `3m` |
| --- | ---: | ---: | ---: |
| Training | 8 | 0.994× | 0.987× |
| Regression | 5 | 1.011× | 1.003× |
| Blind | 2 | 1.040× | 1.054× |
| Compiler transfer | 2 | 0.997× | 0.998× |
| Overall | 17 | 1.005× | 1.001× |

Times are median milliseconds per command. Each comparison has its own reference.

| Panel | Workload | Old ms | New `1.5m` ms | Speedup | Old ms | New `3m` ms | Speedup |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Training | `gzip` | 206.672 | 204.955 | 1.009× | 207.609 | 203.587 | 1.021× |
| Training | `gunzip` | 31.410 | 28.104 | 1.116× | 31.528 | 27.916 | 1.117× |
| Training | `sed` | 107.211 | 112.021 | 0.956× | 107.714 | 111.124 | 0.970× |
| Training | `sort` | 28.812 | 30.673 | 0.938× | 28.665 | 30.722 | 0.933× |
| Training | `sha256` | 27.092 | 26.952 | 1.005× | 26.926 | 27.157 | 0.991× |
| Training | `tar` | 11.674 | 12.053 | 0.972× | 11.583 | 12.237 | 0.945× |
| Training | `du` | 28.678 | 28.827 | 0.991× | 28.741 | 29.330 | 0.979× |
| Training | `ls` | 12.372 | 12.792 | 0.971× | 12.371 | 13.007 | 0.952× |
| Regression | `grep-fixed` | 11.826 | 11.988 | 0.990× | 11.755 | 12.061 | 0.975× |
| Regression | `grep-regex` | 20.913 | 21.389 | 0.982× | 20.948 | 21.236 | 0.978× |
| Regression | `node-minify` | 1302.079 | 1271.487 | 1.022× | 1302.442 | 1280.770 | 1.013× |
| Regression | `mawk-aggregate` | 101.987 | 96.384 | 1.061× | 101.986 | 96.710 | 1.057× |
| Regression | `diff-revisions` | 29.963 | 29.877 | 1.003× | 29.767 | 29.992 | 0.995× |
| Blind | `blind-perl-groups` | 275.473 | 254.868 | 1.081× | 275.395 | 248.537 | 1.113× |
| Blind | `blind-rev-lines` | 273.333 | 272.394 | 1.001× | 273.832 | 274.751 | 0.997× |
| Compiler transfer | `codegen-gzip-o1` | 34.976 | 34.947 | 1.004× | 35.280 | 35.149 | 1.001× |
| Compiler transfer | `codegen-gzip-o3` | 36.223 | 35.244 | 0.990× | 36.032 | 36.274 | 0.995× |

## Verification

- Dual-guest Release ThinLTO with LLVM JIT passed 24/24 CTests. The specialization index build used deferred `INFINITY=__builtin_inff()`.
- The AArch64 native differential test passed. .NET 11 passed 3,276/3,276 and Python passed 55/55.
- All standalone Wasm binaries passed validation and Brotli round-trip checks.
- `llvm-objdump` found zero frame adjustments, branch-and-link calls or stack accesses across all 14,871 AArch64 baked wrappers in the final single-guest `3m` library, including 544 wrappers for the top 100 bindings per class.

[Raw candidate and finalist rounds](compact-measurements.json) · [Frozen workload definitions](../20261126-default-3m/measurements.json)
