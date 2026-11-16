# Named FIFO baseline (2026-11-16)

Branch: `fifo-ltp-hardening`, based on `de6eef24465ae24240c78a601d561ad8caf455b2`.
Pinned, unmodified LTP: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`.

## Method

Build static i386 binaries with `build.sh`. Run the **same binary hashes** on
root WSL Ubuntu (Linux scratch under `/tmp`, not `/mnt/c`) and Windows Podish
with the tmpfs image backend. The preserved pre-repair CLI is
`artifacts/fifo-baseline-cli/Podish.Cli.dll`; source is never reverted to obtain
old behavior. Reports retain build hashes, source state, counts and full logs.
No upstream assertions, harness helpers or failure classifications are changed.

## Unmodified upstream selection

[`cases-fifo.json`](cases-fifo.json) contains 15 binaries: `open06`, `read03`,
`write04`, `select01`, `lseek02`, `dup05`, `fsync03`, `fcntl07`, `mknod02`,
`mknod03`, `mknod04`, `mknod05`, `mknod06`, `mknod08`, `unlink05`.

| Backend | Programs | Assertions |
| --- | --- | --- |
| Linux | 15 PASS | 85 passed |
| Podish before | 13 PASS, 2 BROKEN | 83 passed, 2 broken |

`read03` and `write04` stop in setup: opening a nonblocking writer returns
`ENXIO` despite an existing reader. These are genuinely **BROKEN**, not failed
transport assertions. The passing descriptor/metadata controls do not prove
FIFO data-path correctness. Artifacts: `artifacts/ltp-fifo/results/native.json`
and `podish-before.json`.

Excluded: host-global sysctl mutation (`open16`), block-device formatting
(`mknodat02`), and the unimplemented read-only-filesystem fixture (`mknod07`).

## Independent gap probes

[`cases-fifo-local.json`](cases-fifo-local.json) now selects seven standalone real
LTP-harness programs from [`fifo/`](fifo/). They supplement, not replace,
upstream cases. Local source hashes are recorded in `build.json`.

The first six programs' original 54-assertion artifacts remain under `artifacts/ltp-fifo-local/`.
The next snapshot adds aggregate atomic `writev` checks; compare only reports
from the same snapshot:

| Program | Linux passed | Podish before: passed / failed / broken |
| --- | ---: | ---: |
| `fifo_open01` | 8 | 3 / 5 / 0 |
| `fifo_io01` | 12 | 1 / 4 / 1 |
| `fifo_poll01` | 13 | 1 / 2 / 0 |
| `fifo_lifetime01` | 7 | 3 / 4 / 0 |
| `fifo_signal01` | 6 | 3 / 3 / 0 |
| `fifo_api01` | 11 | 6 / 4 / 0 |
| Total | 57 | 17 / 22 / 1 |

Linux: 6 PASS. Podish before: 5 FAIL, 1 BROKEN. Early setup failures prevent
later assertions from executing; the totals are not equal coverage. Artifacts:
`artifacts/ltp-fifo-local-next/results/{native,podish-before}.json`.
Further native-validated probes and repaired results are in
[FIFO_FIX_REPORT.md](FIFO_FIX_REPORT.md).

The first repair's 86-assertion snapshot is `artifacts/ltp-fifo-local-validated/`:
Linux 6 PASS; original Podish 3 FAIL, 3 BROKEN (18 passed, 24 failed,
3 broken assertions; setup failures again prevent equal coverage). It adds
blocked-write EINTR, `SA_RESTART` open/read/write, cross-page and multiwriter
atomic records, pipe-to-pipe splice backpressure, tee/vmsplice and sendfile
offset/retry contracts. Its predecessor `ltp-fifo-local-final/` deliberately
retains an invalid native fixture: a one-slot FIFO could not accept two separate
`vmsplice` vectors. The corrected fixture reserves two slots; no runtime or
expected-result relaxation was used to make that native run pass.

### Review regression snapshot

`artifacts/ltp-fifo-review/` adds `fifo_review01` (15 assertions) to the six
existing programs: **7 programs / 101 assertions**. The identical static i386
binaries pass all 101 assertions on Linux. Against the preserved first-repair
CLI (`artifacts/fifo-review-before-cli/Podish.Cli.dll`), the new program records
4 passed / 11 failed, while the original six still pass their 86 assertions.
This is a different baseline from the original pre-FIFO CLI above.

The new assertions cover short packet pipe-to-pipe splice, atomic small
writev faults (invalid suffix and cross-page fault), invalid sendfile inputs,
and close(fd) while another thread has a blocked FIFO read. Deterministic
managed tests supplement the thread's bounded native poll checkpoint and
cover overlay identity, retirement, notification reentrancy and allocations.
See [FIFO_FIX_REPORT.md](FIFO_FIX_REPORT.md) for the review repairs and results.

## Reproduce

```sh
bash tests/ltp/build.sh artifacts/ltp-fifo tests/ltp/cases-fifo.json
sudo python3 tests/ltp/run.py --backend native --artifacts artifacts/ltp-fifo \
  --manifest tests/ltp/cases-fifo.json
bash tests/ltp/build.sh artifacts/ltp-fifo-review tests/ltp/cases-fifo-local.json
sudo python3 tests/ltp/run.py --backend native --artifacts artifacts/ltp-fifo-review \
  --manifest tests/ltp/cases-fifo-local.json
```

```powershell
$env:DOTNET_ROLL_FORWARD = 'LatestMajor' # this host has .NET 11, projects target net10
dotnet build Podish.Cli/Podish.Cli.csproj -c Debug
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-fifo --manifest tests/ltp/cases-fifo.json
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-fifo-review --manifest tests/ltp/cases-fifo-local.json
# Add --cli artifacts/fifo-baseline-cli/Podish.Cli.dll --output <separate-report.json>
# only when the preserved baseline executable is available.
```
