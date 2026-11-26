# Default 3 MiB preset on i386 and AArch64

Both guests ran from one Release build of Podish.Cli with its packaged dual-guest libfibercpu. The native baking preset and both guest switches used their defaults. The shipped plan uses generalization-aware marginal ranking at risk 0.5. The verified 3 MiB prefixes contain 8,191 i386 entries and 8,976 AArch64 entries.

The Apple M5 Max host ran macOS 27.2.1, Apple Clang 21.0.0, Release optimization and LTO. The interpreter used the default 64 MiB block cache and 1,024 data TLB entries. LLVM JIT was compiled out. The build contained no dynamic operand bundle. A separate matched standalone dual-guest calibration measured 771,084 B of unbaked native text. The packaged library measured 5,518,208 B, giving growth of 4,747,124 B against a 6,291,456 B combined allowance.

The Debian guest trees used architecture-specific ELF executables, matching input digests and matching package versions. Both used Node v14.21.3. GNU gzip 1.12 compiled with O1 and O3 supplies the two compiler-transfer cases. CoreMark was excluded.

Guest order alternated each round. Each table cell is the median of five wall times per shell command in milliseconds. The marker interval includes shell overhead, guest command startup, I/O and output redirection. Each round divides the loop time by its case repetition count. All 170 case outputs passed their fixed checksum checks. The reported ratio divides median i386 time by median AArch64 time. A value above one means AArch64 finished faster.

The geometric mean of the 17 case ratios was 1.231×. AArch64 had the lower median in 13 cases.

| Workload | Panel | Commands per sample | i386 ms | AArch64 ms | i386 / AArch64 | Paired ratio range |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| gzip | Training | 1 | 273.25 | 191.65 | 1.426× | 1.407–1.460 |
| gunzip | Training | 3 | 48.33 | 35.30 | 1.369× | 1.336–1.387 |
| sed | Training | 2 | 163.31 | 137.72 | 1.186× | 1.152–1.206 |
| sort | Training | 2 | 44.88 | 34.39 | 1.305× | 1.279–1.325 |
| sha256 | Training | 3 | 101.92 | 29.78 | 3.422× | 3.405–3.600 |
| tar | Training | 3 | 16.16 | 12.74 | 1.269× | 1.218–1.316 |
| du | Training | 6 | 54.66 | 45.15 | 1.211× | 1.073–1.338 |
| ls | Training | 6 | 19.26 | 24.48 | 0.787× | 0.755–1.116 |
| grep-fixed | Regression | 4 | 13.14 | 12.54 | 1.048× | 1.033–1.135 |
| grep-regex | Regression | 3 | 25.56 | 26.32 | 0.971× | 0.963–1.015 |
| node-minify | Regression | 1 | 1775.06 | 1576.08 | 1.126× | 1.123–1.148 |
| mawk-aggregate | Regression | 3 | 152.58 | 139.11 | 1.097× | 1.065–1.109 |
| diff-revisions | Regression | 5 | 36.94 | 39.50 | 0.935× | 0.922–0.943 |
| blind-perl-groups | Perl and rev | 2 | 581.17 | 465.21 | 1.249× | 1.114–1.319 |
| blind-rev-lines | Perl and rev | 2 | 322.21 | 323.61 | 0.996× | 0.973–1.008 |
| codegen-gzip-o1 | Compiler transfer | 4 | 53.19 | 39.05 | 1.362× | 1.331–1.371 |
| codegen-gzip-o3 | Compiler transfer | 4 | 55.56 | 40.46 | 1.373× | 1.315–1.395 |

Within each panel, the geometric means below weight each software family equally.

| Panel | i386 / AArch64 |
| --- | ---: |
| Training | 1.318× |
| Regression | 1.039× |
| Perl and rev | 1.115× |
| Compiler transfer | 1.368× |

The largest AArch64 advantage was sha256 (3.422×), while diff favored i386 in all five paired rounds. The paired ratio for ls ranged from 0.755× to 1.116×, so the size of its difference is sensitive to the sample.

The [raw measurement archive](measurements.json) contains the exact commands, repetitions, every round, input and ELF digests, managed and native binary hashes and the runtime fingerprint. The [training guide](../../../../docs/baked-instruction-variants.md) describes how the presets are generated.

## Repeating the workload panel

Build `Podish.Cli` in Release with the stock native settings. Prepare the two rootfs trees and inputs listed in the measurement archive, including the [compiler-transfer fixtures](../20261126-joint-ranking/blind/README.md). From the repository root, set `CONFIG` to a training configuration whose `managed_cli` points to the new Release CLI, then run the following:

```sh
PYTHONPATH=scripts python3 - "$CONFIG" <<'PYTHON'
import json
import sys
import tempfile
from pathlib import Path
from nested_baking_workloads import benchmark_lock, run_workloads

report = json.loads(Path("benchmark/optimization/results/20261126-default-3m/measurements.json").read_bytes())
cfg = json.loads(Path(sys.argv[1]).read_bytes())
cases = [case for panel in report["groups"].values() for case in panel]
cli = Path(cfg["managed_cli"]).resolve() / "Podish.Cli.dll"
with tempfile.TemporaryDirectory() as tmp, benchmark_lock(cfg["benchmark_lock"]):
    for n in range(5):
        guests = ("aarch64", "i386") if n % 2 else ("i386", "aarch64")
        for guest in guests:
            options = cfg | {"guest": guest, "rootfs": cfg["rootfs"][guest]}
            seconds = run_workloads(options, cli, cases, Path(tmp) / f"{n}-{guest}.log", native_defaults=True)
            print(json.dumps({"round": n + 1, "guest": guest, "seconds": seconds}))
PYTHON
```

The [paired reruns and disassembly profiles](profile/README.md) trace five workloads to guest code and host handlers and test the effect of host tiering on recursive ls.

The [independent lowering and conditional-branch experiments](../20261126-lowering-compare-branch/README.md) use the same 3 MiB plans and add two holdout families.
