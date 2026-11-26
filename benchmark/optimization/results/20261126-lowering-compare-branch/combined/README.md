# Dual-guest 3 MiB baseline and integrated lowering

Reference revision `1738fa49`. Combined revision `e2d106dc` includes the AArch64 scalar memory and ALU lowering, the general compare/branch pair, direct native conditional branches, and i386 CMP/Jcc forwarding.

Both native libraries were built for arm64 macOS with Apple Clang 21, Release -O3 and ThinLTO. They used the same dual-guest 3 MiB baking plans and managed CLI. The host was Apple M5 Max with macOS 27.2.1. Build tests ran separately from timing.

The native `__text` section is 5,518,208 B at reference and 5,551,716 B after integration, a 33,508 B increase. The [isolated variant report](../README.md) has the preceding experiments, and the [holdout manifest](../holdout.json) records the two added workloads.

Each arm used a fresh CLI process per guest and round. Guest order alternates and the baseline and combined library order is counterbalanced within guest. The 19 commands and repetition counts follow the previous panel. All outputs passed the workload digest check.

Times are median milliseconds per command. Speed columns are medians of the paired reference/combined ratios for the same guest, with the combined library win count in parentheses. The last column measures i386/AArch64 using the combined library. Values greater than one favor AArch64 there. Families are weighted equally for the aggregate.

CTest passed 4/4. The native differential test compared 276 AArch64 encodings with 80 input states each against physical hardware, plus baked, invalid and dead-NZCV paths. The i386 gzip clone check matched the reference for registers, flags and faults. The [test output](test-results.txt) and [i386 state comparison](i386-fusion-state.json) record these checks. The merged library disassembles to the [post-index byte load](a64-post-index-ldrb.asm), [AArch64 compare/branch pair](a64-compare-branch.asm), and [i386 gzip CMP/JAE clone](i386-gzip.asm).

## Default host .NET tiering

| Workload | i386 base ms | i386 combined ms | i386 speed | A64 base ms | A64 combined ms | A64 speed | Combined i386 / A64 |
|---|---:|---:|---:|---:|---:|---:|---:|
| gzip | 273.10 | 266.75 | 1.023 (6/6) | 192.78 | 192.13 | 1.008 (4/6) | 1.385 |
| gunzip | 48.62 | 48.05 | 1.005 (4/6) | 35.53 | 35.60 | 0.991 (2/6) | 1.351 |
| sed | 165.00 | 163.05 | 1.014 (5/6) | 137.91 | 136.86 | 1.008 (4/6) | 1.196 |
| sort | 44.79 | 45.02 | 0.995 (2/6) | 34.09 | 34.10 | 0.998 (3/6) | 1.318 |
| sha256 | 102.99 | 102.58 | 1.000 (3/6) | 29.62 | 29.59 | 1.002 (3/6) | 3.456 |
| tar | 16.20 | 16.24 | 1.002 (3/6) | 12.69 | 12.79 | 0.989 (2/6) | 1.267 |
| du | 54.19 | 56.52 | 0.960 (2/6) | 45.17 | 43.74 | 1.022 (3/6) | 1.277 |
| ls | 19.21 | 27.92 | 0.958 (0/6) | 24.75 | 24.61 | 1.000 (3/6) | 1.129 |
| grep-fixed | 13.31 | 13.43 | 0.999 (3/6) | 12.56 | 12.28 | 1.019 (6/6) | 1.088 |
| grep-regex | 25.83 | 25.97 | 0.991 (1/6) | 26.41 | 25.38 | 1.034 (6/6) | 1.025 |
| node-minify | 1785.66 | 1789.18 | 0.996 (2/6) | 1573.92 | 1493.63 | 1.055 (6/6) | 1.199 |
| mawk-aggregate | 151.40 | 150.44 | 1.007 (4/6) | 139.47 | 124.99 | 1.115 (6/6) | 1.205 |
| diff-revisions | 36.89 | 36.88 | 1.002 (3/6) | 39.43 | 31.18 | 1.259 (6/6) | 1.185 |
| blind-perl-groups | 584.18 | 582.72 | 1.004 (4/6) | 449.13 | 414.84 | 1.083 (6/6) | 1.401 |
| blind-rev-lines | 323.35 | 319.14 | 1.014 (6/6) | 319.18 | 317.55 | 1.010 (4/6) | 1.007 |
| codegen-gzip-o1 | 53.27 | 53.38 | 0.994 (2/6) | 38.84 | 39.23 | 0.987 (3/6) | 1.370 |
| codegen-gzip-o3 | 55.74 | 55.88 | 1.000 (3/6) | 40.25 | 41.06 | 0.978 (2/6) | 1.356 |
| dpkg-deb-xz | 354.05 | 354.19 | 1.003 (4/6) | 273.89 | 255.85 | 1.072 (6/6) | 1.383 |
| find-dat | 20.33 | 20.21 | 1.001 (3/6) | 16.02 | 16.03 | 1.004 (5/6) | 1.262 |
| Family-weighted geomean | | | 1.002 | | | 1.049 | 1.251 |

The reference family-weighted i386/AArch64 ratio is 1.185. [Raw rounds](combined-default.json)

## Host .NET tiering disabled

| Workload | i386 base ms | i386 combined ms | i386 speed | A64 base ms | A64 combined ms | A64 speed | Combined i386 / A64 |
|---|---:|---:|---:|---:|---:|---:|---:|
| gzip | 311.63 | 303.01 | 1.029 (4/4) | 230.70 | 227.89 | 1.006 (3/4) | 1.329 |
| gunzip | 47.61 | 47.07 | 1.010 (4/4) | 34.88 | 35.06 | 0.992 (1/4) | 1.345 |
| sed | 161.02 | 161.09 | 0.998 (2/4) | 132.88 | 133.36 | 0.995 (0/4) | 1.208 |
| sort | 43.30 | 43.19 | 1.003 (3/4) | 31.65 | 31.56 | 1.003 (3/4) | 1.369 |
| sha256 | 100.46 | 98.93 | 1.015 (3/4) | 28.18 | 28.01 | 1.002 (4/4) | 3.532 |
| tar | 17.21 | 17.34 | 0.992 (0/4) | 12.27 | 12.24 | 1.002 (3/4) | 1.419 |
| du | 39.48 | 39.59 | 1.000 (2/4) | 34.95 | 34.83 | 1.002 (3/4) | 1.137 |
| ls | 17.31 | 17.37 | 0.995 (0/4) | 14.12 | 14.13 | 0.998 (2/4) | 1.237 |
| grep-fixed | 14.48 | 14.71 | 0.983 (0/4) | 12.70 | 12.48 | 1.019 (4/4) | 1.184 |
| grep-regex | 25.32 | 25.47 | 0.994 (2/4) | 24.65 | 23.84 | 1.033 (4/4) | 1.069 |
| node-minify | 1772.90 | 1783.31 | 0.992 (0/4) | 1532.21 | 1449.47 | 1.056 (4/4) | 1.233 |
| mawk-aggregate | 151.03 | 149.31 | 1.000 (2/4) | 135.58 | 122.25 | 1.109 (4/4) | 1.221 |
| diff-revisions | 38.07 | 38.45 | 0.995 (1/4) | 39.04 | 30.82 | 1.267 (4/4) | 1.251 |
| blind-perl-groups | 581.72 | 580.89 | 1.001 (2/4) | 443.18 | 403.22 | 1.096 (4/4) | 1.436 |
| blind-rev-lines | 340.48 | 337.50 | 1.009 (3/4) | 340.70 | 335.25 | 1.016 (4/4) | 1.010 |
| codegen-gzip-o1 | 52.80 | 52.79 | 1.001 (2/4) | 38.57 | 38.70 | 1.002 (2/4) | 1.362 |
| codegen-gzip-o3 | 55.27 | 55.40 | 0.997 (2/4) | 40.06 | 40.14 | 0.998 (1/4) | 1.379 |
| dpkg-deb-xz | 354.06 | 352.42 | 1.002 (2/4) | 272.49 | 253.62 | 1.074 (4/4) | 1.390 |
| find-dat | 20.52 | 20.55 | 0.998 (2/4) | 16.32 | 16.29 | 1.002 (2/4) | 1.260 |
| Family-weighted geomean | | | 0.999 | | | 1.051 | 1.284 |

The reference family-weighted i386/AArch64 ratio is 1.222. [Raw rounds](combined-no-tiering.json)

## AArch64 gunzip follow-up

The first two default-panel pairs had combined throughput ratios of 0.886 and 0.901. The six-pair median was 0.991. The full panel with host tiering disabled gave 0.992. A six-arm isolated control ran one gzip command followed by ten gunzip repetitions per process with tiering disabled. The combined median was 33.249 ms versus 33.109 ms for the reference, with a 0.991 median paired ratio. The large first-two-round drop did not reproduce in the focused test. [Focused rounds and all variants](gunzip-focus.json)
