# General-purpose lowering and conditional branches

Every candidate branched from `1738fa49`. The AArch64 ALU candidate includes the memory candidate and is compared with it. The AArch64 native-condition candidate includes the generic pair and is compared with it. The i386 candidate uses its own branch. No baked preset JSON or generated plan inputs were changed.

| Candidate | Worktree branch | Revision | Release `__text` bytes |
|---|---|---|---:|
| Reference | feature/aarch64-guest | 1738fa49 | 5,518,208 |
| A64 scalar pre/post-index and SXTW | experiment/a64-lowering-nov26 | 6a61c507 | 5,539,676 |
| A64 memory plus ALU | experiment/a64-lowering-nov26 | 94f82d88 | 5,545,276 |
| A64 CMP+B.cond | experiment/a64-fusion-nov26 | f6047966 | 5,527,060 |
| A64 CMP+B.cond plus direct host condition | experiment/a64-native-branch-nov26 | bcc92ccf | 5,519,740 |
| i386 CMP/Jcc forwarding | experiment/i386-flags-nov26 | 1a3f5b8a and 9608477d | 5,523,116 |

Apple M5 Max, macOS 27.2.1, Apple Clang 21, Release -O3 with ThinLTO. Dual guests, same stock 3 MiB plans and managed assemblies in every build. The non-instrumented rebuilt reference exactly matches the shipped native text size. Its Mach-O bytes and SHA-256 differ from the historical packaged binary. Each arm uses the same CMake configuration, and the baseline was remeasured alongside every variant.

The 19 cases combine the 17-case panel from the [default 3 MiB benchmark](../20261126-default-3m/README.md) with two new program families. `dpkg-deb --fsys-tarfile` extracts an XZ-compressed deterministic package and `find` walks the existing fixture tree with a filename and file-type predicate. The holdout commands, fixture digest, guest ELF digests and expected outputs are in [holdout.json](holdout.json). The package contains a USTAR archive of 24 consecutive 86,000-byte slices of the shared input.txt, compressed with XZ. Each run checks its result digest. Each guest/variant/round uses a fresh CLI process. Workload order and repetitions are identical. Variants within a round are counterbalanced by reversing the previous round's order, and guest order alternates. The benchmark lock serializes the timed invocations. No builds run in parallel with timed invocations.

The table uses the median of paired baseline/variant elapsed-time ratios. A ratio greater than one favors the candidate. The report also gives sample medians and winning-pair counts. The aggregate first takes a geometric mean within software families and then gives families equal weight.

## Host disassembly and correctness

- A64 `ldrb w21,[x28],#1`, ordinary register case, valid VA and first cache-page hit including tail jump, is 77 host instructions in the stock generic handler versus 32 in the default post-index binding. The handler binds width, operation and addressing mode while the slot carries register IDs and displacement. [Released candidate disassembly](a64-memory-release-ldrb.asm)
- A64 generic compare/branch preserves the two decoded members and scalar execution under a one-instruction budget. The host-condition variant keeps complete guest NZCV in a GPR on both successors. Native HI and GT consume host condition flags directly, while simple EQ already uses a single-bit branch in the first fusion and can acquire an extra control-flow jump after compiler block layout. [Generic pair](a64-pair-release-reg-branch.asm) · [Direct host condition](a64-native-release-reg-branch.asm)
- The i386 gzip CMP/JAE clone now has adjacent `subs; b.hs`. The stock sequence was 21 instructions through the first conditional branch. The same fallthrough path including EFLAGS packing and the next tail dispatch changes from 23 to 18 static instructions. [Reference clone](base-release-gzip.asm) · [Candidate clone](i386-flags-release-gzip.asm)
- Core A64 instruction conformance and A64 MMU tests pass with the lowering. The ALU candidate passed 276 instructions × 80 states on real AArch64, plus 64 memory combinations covering inline/replay, reads/writes, width and writeback. The fusion arms passed differential tests across conditions, W/X and instruction budget. The native-condition arm also checks exact fusion with overflow and a fault in the successor. The i386 result forwarding checks all 65,536 byte operand pairs, signed boundaries and preserved flags, and matches the reference for taken/fallthrough and original gzip clone memory faults at both load sites.

## Default .NET tiering

| Workload | memory vs base | ALU vs memory | memory + ALU vs base | pair vs base | native conditions vs pair | pair + native conditions vs base | flags vs base |
|---|---:|---:|---:|---:|---:|---:|---:|
| gzip | 0.997 (2/6) | 1.002 (4/6) | 0.998 (1/6) | 1.000 (3/6) | 1.005 (5/6) | 1.009 (5/6) | 1.021 (5/6) |
| gunzip | 0.984 (2/6) | 0.994 (2/6) | 0.983 (1/6) | 0.995 (2/6) | 1.001 (4/6) | 0.996 (2/6) | 1.004 (5/6) |
| sed | 1.004 (3/6) | 0.990 (1/6) | 0.999 (3/6) | 1.010 (4/6) | 1.001 (3/6) | 1.006 (4/6) | 0.996 (3/6) |
| sort | 0.994 (2/6) | 0.995 (3/6) | 0.978 (1/6) | 0.984 (2/6) | 1.003 (4/6) | 1.006 (4/6) | 1.000 (3/6) |
| sha256 | 1.001 (3/6) | 1.001 (3/6) | 0.998 (2/6) | 0.988 (1/6) | 1.000 (3/6) | 0.994 (1/6) | 1.015 (4/6) |
| tar | 1.004 (4/6) | 0.999 (3/6) | 1.005 (3/6) | 0.985 (2/6) | 0.993 (2/6) | 1.005 (3/6) | 1.020 (5/6) |
| du | 0.992 (3/6) | 0.996 (3/6) | 0.998 (3/6) | 1.027 (3/6) | 1.052 (4/6) | 1.030 (3/6) | 1.040 (5/6) |
| ls | 0.993 (3/6) | 1.002 (3/6) | 0.988 (3/6) | 1.012 (3/6) | 0.989 (3/6) | 0.989 (2/6) | 1.002 (4/6) |
| grep-fixed | 0.992 (3/6) | 0.991 (3/6) | 0.982 (2/6) | 0.987 (2/6) | 1.004 (3/6) | 1.004 (4/6) | 0.981 (1/6) |
| grep-regex | 1.007 (3/6) | 0.999 (3/6) | 1.005 (4/6) | 1.037 (6/6) | 1.006 (4/6) | 1.034 (5/6) | 1.002 (3/6) |
| node-minify | 1.022 (5/6) | 1.002 (3/6) | 1.016 (5/6) | 1.050 (6/6) | 1.003 (5/6) | 1.050 (6/6) | 0.988 (2/6) |
| mawk-aggregate | 1.039 (6/6) | 1.003 (4/6) | 1.043 (6/6) | 1.055 (6/6) | 1.011 (6/6) | 1.064 (6/6) | 0.984 (2/6) |
| diff-revisions | 1.157 (6/6) | 1.112 (6/6) | 1.284 (6/6) | 1.036 (6/6) | 0.996 (2/6) | 1.030 (6/6) | 0.996 (2/6) |
| blind-perl-groups | 1.007 (3/6) | 1.008 (4/6) | 1.016 (4/6) | 1.076 (6/6) | 0.989 (1/6) | 1.079 (5/6) | 0.999 (3/6) |
| blind-rev-lines | 1.003 (4/6) | 0.997 (2/6) | 1.005 (5/6) | 1.014 (5/6) | 1.005 (4/6) | 1.020 (5/6) | 1.006 (4/6) |
| codegen-gzip-o1 | 1.013 (6/6) | 0.993 (2/6) | 1.008 (5/6) | 0.971 (0/6) | 1.017 (5/6) | 0.986 (0/6) | 0.994 (2/6) |
| codegen-gzip-o3 | 1.020 (6/6) | 0.974 (0/6) | 0.996 (3/6) | 0.977 (0/6) | 0.997 (3/6) | 0.982 (1/6) | 0.994 (2/6) |
| dpkg-deb-xz | 1.000 (3/6) | 0.991 (2/6) | 0.996 (2/6) | 1.074 (6/6) | 0.998 (3/6) | 1.064 (6/6) | 1.000 (3/6) |
| find-dat | 0.999 (3/6) | 0.994 (2/6) | 1.000 (3/6) | 1.002 (4/6) | 1.002 (4/6) | 1.002 (4/6) | 0.998 (2/6) |
| Family-weighted geometric mean | 1.019 | 1.006 | 1.026 | 1.025 | 1.001 | 1.028 | 1.000 |

[Per-round timing and digests](native-default.json)

## Host .NET tiering disabled

| Workload | pair vs base | native conditions vs pair | pair + native conditions vs base | flags vs base |
|---|---:|---:|---:|---:|
| gzip | 1.000 (2/4) | 1.006 (3/4) | 1.009 (3/4) | 1.019 (4/4) |
| gunzip | 0.986 (1/4) | 1.007 (4/4) | 1.001 (3/4) | 1.005 (4/4) |
| sed | 0.991 (2/4) | 1.015 (3/4) | 1.009 (3/4) | 1.000 (2/4) |
| sort | 1.008 (3/4) | 1.004 (2/4) | 1.004 (2/4) | 0.995 (1/4) |
| sha256 | 0.997 (1/4) | 1.005 (3/4) | 0.999 (2/4) | 1.026 (3/4) |
| tar | 1.002 (3/4) | 0.995 (1/4) | 0.997 (0/4) | 1.002 (3/4) |
| du | 0.994 (1/4) | 1.009 (4/4) | 1.004 (4/4) | 1.003 (2/4) |
| ls | 0.995 (0/4) | 0.998 (2/4) | 0.995 (1/4) | 1.005 (3/4) |
| grep-fixed | 1.003 (3/4) | 1.003 (4/4) | 1.008 (3/4) | 0.993 (1/4) |
| grep-regex | 1.031 (4/4) | 1.000 (2/4) | 1.033 (4/4) | 0.995 (2/4) |
| node-minify | 1.040 (4/4) | 1.002 (3/4) | 1.040 (4/4) | 0.995 (1/4) |
| mawk-aggregate | 1.059 (4/4) | 0.985 (1/4) | 1.043 (4/4) | 0.992 (1/4) |
| diff-revisions | 1.031 (3/4) | 1.000 (2/4) | 1.034 (4/4) | 0.995 (1/4) |
| blind-perl-groups | 1.079 (4/4) | 1.006 (3/4) | 1.080 (4/4) | 0.999 (2/4) |
| blind-rev-lines | 1.009 (3/4) | 0.993 (1/4) | 1.002 (2/4) | 1.009 (2/4) |
| codegen-gzip-o1 | 0.975 (1/4) | 1.026 (4/4) | 0.993 (1/4) | 0.993 (1/4) |
| codegen-gzip-o3 | 0.979 (1/4) | 1.009 (3/4) | 0.990 (1/4) | 1.025 (3/4) |
| dpkg-deb-xz | 1.066 (4/4) | 0.995 (1/4) | 1.065 (4/4) | 1.001 (2/4) |
| find-dat | 1.007 (4/4) | 0.994 (1/4) | 1.001 (3/4) | 0.998 (2/4) |
| Family-weighted geometric mean | 1.023 | 1.000 | 1.024 | 1.000 |

[Per-round timing and digests](no-tiering.json)
