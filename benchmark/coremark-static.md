# Static i386 CoreMark comparison

The comparison uses a **single** Linux i386 static ELF, not separate guest
builds. CoreMark source: `eembc/coremark` commit
`1f483d5b8316753a742cbf5590caf5bd0a4e4777`.
Build on WSL Ubuntu (GCC 13.3.0, 32-bit static glibc development files):

```sh
git clone https://github.com/eembc/coremark.git coremark-static-src
git -C coremark-static-src checkout 1f483d5b8316753a742cbf5590caf5bd0a4e4777
make -C coremark-static-src PORT_DIR=linux CC=gcc \
  XCFLAGS='-m32 -static -O3 -march=i686 -DPERFORMANCE_RUN=1' \
  ITERATIONS=3000 REBUILD=1 compile
mkdir -p artifacts/coremark-static
cp coremark-static-src/coremark.exe artifacts/coremark-static/coremark_i686_linux
file artifacts/coremark-static/coremark_i686_linux
sha256sum artifacts/coremark-static/coremark_i686_linux
```

The resulting ELF is statically linked and has SHA-256
`0E8D1D0063EFB338E1DFF9039BA13BA42572BD3B4B541BA1C54500DF850B9A38`.
`artifacts/` is ignored by git. `ITERATIONS=3000` is a compile-time default;
both measured variants use the **same** run-time arguments
`0x0 0x0 0x66 24000`, sufficient for a CoreMark-valid run (>10 seconds).

Build both `Podish.Cli` versions in Release, using the unmodified
`320880455fde9b81c7b9d83d436051e7a69057c0` as baseline. On Windows,
with the SDKs required by both checkouts installed, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./benchmark/compare_static_coremark.ps1 `
  -BaselineCli 'C:\path\to\baseline\Podish.Cli\bin\Release\net10.0\Podish.Cli.dll' `
  -ChangedCli './Podish.Cli/bin/Release/net11.0/Podish.Cli.dll' `
  -Rootfs './artifacts/coremark-static'
```

The script alternates variant order across three rounds, verifies guest
correctness, and saves all samples to ignored
`artifacts/coremark-static/comparison.json`. Interpret medians in light of
host load; a single run is not a reliable regression signal.

## Measured result (Windows, Release CLI)

Three rounds, alternating baseline/changed, run-time arguments exactly as
above; all six runs reported `Correct operation validated`:

| Variant | Round 1 | Round 2 | Round 3 | Median (iterations/s) |
| --- | ---: | ---: | ---: | ---: |
| Baseline | 1981.015270 | 1969.634797 | 1979.707993 | 1979.707993 |
| Changed | 2010.555416 | 1988.565747 | 1987.577640 | 1988.565747 |

The changed median for the final build is **0.45% higher**. An earlier paired
round on this host measured 1983.798975 → 2036.141512 iterations/s (+2.64%);
an unpaired smoke check included a slower changed outlier (1859.024012).
The data do not establish a repeatable speedup, but show no consistent
regression on this workload. Host load and thermals can exceed the measured
difference; avoid treating a single sample as a guarantee.
