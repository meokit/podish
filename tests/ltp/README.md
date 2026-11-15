# LTP syscall integration tests

This is a selected **50-binary** LTP suite, not all of LTP. The upstream source
is the `upstream` git submodule; the superproject's gitlink pins its revision.
Build scripts, selection and host runner live here, outside the submodule.
No upstream tests or assertions are patched. See [RESULTS.md](RESULTS.md) for
the initial Windows Podish results, [FIX_REPORT.md](FIX_REPORT.md) for the
subsequent fixes (50/50 passing on the repair branch), and
[PERMISSIONS_BASELINE.md](PERMISSIONS_BASELINE.md) for the separate permission
and identity batch (70/71 passing on the tmpfs image backend after the
permission fixes; the remaining `access02` failure is a missing `/bin/sh` test
asset, not a syscall assertion), [SYNC_BASELINE.md](SYNC_BASELINE.md) for
the synchronization batch (futex and SysV semaphores; 3 PASS, 12 PARTIAL,
10 FAIL, 2 TIMEOUT, 2 BROKEN and 3 SKIP), and
[SIGNALS_BASELINE.md](SIGNALS_BASELINE.md) for the signal and event batch
(46 cases; 46/46 passing on tmpfs after the signal fixes), and
[MEMORY_BASELINE.md](MEMORY_BASELINE.md) for the memory management and mapping
batch (66 cases; first baseline: 22 PASS). See
[MEMORY_FIX_REPORT.md](MEMORY_FIX_REPORT.md) for bounded repairs
(36 PASS, 11 FAIL, 14 BROKEN, 0 TIMEOUT, 5 SKIP) and deferred memory-model work.
[PIPE_BASELINE.md](PIPE_BASELINE.md) selects the next batch: pipes and FIFOs
(17 cases; first Podish baseline 14 PASS, 1 FAIL, 2 BROKEN). SysV IPC remains
available as a lower-usage compatibility reference in
[IPC_BASELINE.md](IPC_BASELINE.md) (39 cases; first Podish baseline 4 PASS,
6 FAIL, 26 BROKEN, 3 SKIP). The proposed next series is documented in the
[direct-mapped memory refactor plan](../../docs/direct-mapped-memory-refactor-plan.md)
and [hostfs/SilkFS test plan](MAPPED_FILESYSTEM_TEST_PLAN.md). These are plans,
not implemented runner support or new backend results.
Hostfs remains available only for comparison and forces mode
`0755` on Windows, so it is unsuitable for permission tests.

## Manifests

`cases.json` is the basic syscall batch. `cases-permissions.json` is the
permission/identity batch, `cases-sync.json` is the futex + SysV semaphore
batch, `cases-signals.json` is the signal + event notification batch,
`cases-memory.json` is the memory management + address-space mapping batch,
`cases-net-io.json` is the network sockets + I/O multiplexing batch,
`cases-fs-fd.json` is the filesystem metadata + fcntl/flock + zero-copy I/O
batch (115 cases; three `needs_device` cases are excluded because the tmpfs
image has no block device), `cases-proc-time.json` is the process lifecycle
+ rlimit/prctl + timers/inotify batch, `cases-pipe.json` is the pipe and FIFO
batch (17 cases), and `cases-ipc.json` is the deferred SysV shared memory +
message queue batch (39 cases; SysV semaphores remain in the sync batch). The
permission, sync, signal, memory, net-io, fs-fd, proc-time, pipe and IPC
manifests require a root native baseline and default to tmpfs. Build
and run each batch into its own artifact directory so `build.json` and baselines
do not mix with the basic batch:

```sh
git submodule update --init tests/ltp/upstream
bash tests/ltp/build.sh artifacts/ltp-permissions tests/ltp/cases-permissions.json
# Windows/WSL native reference:
wsl -d Ubuntu -u root -- python3 /mnt/c/Users/Jiang/repos/podish/tests/ltp/run.py \
  --backend native --artifacts /mnt/c/Users/Jiang/repos/podish/artifacts/ltp-permissions \
  --manifest /mnt/c/Users/Jiang/repos/podish/tests/ltp/cases-permissions.json
# Podish with a writable tmpfs root materialized from an imported image
# (the permission manifest defaults to tmpfs):
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-permissions \
  --manifest tests/ltp/cases-permissions.json
# Use --fs-backend hostfs only for an explicit old-backend comparison.
```

The synchronization batch uses the same workflow with
`cases-sync.json` and `artifacts/ltp-sync`. The signal and event batch uses
`cases-signals.json` and `artifacts/ltp-signals`. The memory management batch
uses `cases-memory.json` and `artifacts/ltp-memory`. The newer baseline
batches use `cases-net-io.json` (`artifacts/ltp-net-io`, see
[NET_IO_BASELINE.md](NET_IO_BASELINE.md)), `cases-fs-fd.json`
(`artifacts/ltp-fs-fd`, see [FS_FD_BASELINE.md](FS_FD_BASELINE.md)),
`cases-proc-time.json` (`artifacts/ltp-proc-time`, see
[PROC_TIME_BASELINE.md](PROC_TIME_BASELINE.md)), `cases-pipe.json`
(`artifacts/ltp-pipe`, see [PIPE_BASELINE.md](PIPE_BASELINE.md)), and the
deferred `cases-ipc.json` (`artifacts/ltp-ipc`, see
[IPC_BASELINE.md](IPC_BASELINE.md)). A manifest case may also set
`"timeoutSeconds"` to override the host timeout for long-running race tests.

The `tmpfs` backend imports one generated image containing all selected
binaries under `/opt/ltp/testcases/bin`, then runs each binary in a separate
container. Unix modes are written explicitly into the image tar; do not use a
Windows `tar` command, which commonly loses execute bits. The older `hostfs`
mode remains available with `--fs-backend hostfs` for comparison, but it
forces mode `0755` on Windows and is not suitable for permission assertions.

The Podish guest rootfs used by the runner contains root, daemon, bin, two
unprivileged test users (1000/1001) and nobody, plus matching groups. Do not
run the permission manifest as an ordinary Linux user: LTP's `.needs_root`
fixtures will abort or produce a non-comparable baseline.

## Selection

`cases.json` is the allowlist. It selects basic file IO, vectored IO, descriptor
operations, pipes, truncation, processes/wait, clocks, poll/select/eventfd/epoll,
uname and getrandom. Individual programs may contain multiple assertions and
libc/raw-syscall variants. There are deliberately no device, privileged,
namespace, network-configuration, stress or external-helper tests.

Do **not** use a wildcard over upstream's syscall directory: some LTP cases
format devices, alter system settings, or have large resource demands.

## Build assets (Linux / WSL x86_64)

Install these packages in a dedicated Ubuntu/Debian build environment:

```sh
sudo apt-get install gcc-multilib libc6-dev-i386 make autoconf automake libtool pkg-config python3 git
```

From the repository root:

```sh
git submodule update --init tests/ltp/upstream
bash tests/ltp/build.sh
```

On Windows, run the build through WSL, for example from PowerShell:

```powershell
wsl -d Ubuntu -- bash /mnt/c/Users/Jiang/repos/podish/tests/ltp/build.sh
```

The script exports the committed upstream source into a disposable Linux build
directory, bootstraps/configures LTP and builds only the selected targets plus
the LTP support library. It leaves the submodule clean, including when Windows
Git uses CRLF. `LTP_BUILD_CACHE` chooses the scratch parent, `LTP_JOBS` defaults
to 4, and the first argument overrides `artifacts/ltp` as the output directory.
The temporary source/build directory is removed on exit.

Assets use `gcc -m32 -static -O2 -g -march=i686 -mno-avx` and glibc, not musl.
The script rejects non-i386 or interpreter-dependent ELF files and records
the upstream commit, compiler, flags and every binary's SHA-256 in `build.json`.
Static glibc emits NSS linker warnings; these selected tests do not require
network NSS services. This does not promise that arbitrary future NSS tests
will work in a minimal rootfs.

## 1. Native Linux baseline

The basic manifest can use a normal user. The permission manifest requires a
root native baseline because LTP creates root-owned fixtures and then drops
privileges itself; see the manifest commands above.

```sh
python3 tests/ltp/run.py --backend native
```

Windows example:

```powershell
wsl -d Ubuntu -- python3 /mnt/c/Users/Jiang/repos/podish/tests/ltp/run.py --backend native
```

Run as a normal Linux user. Binaries can live on `/mnt/c`, but native scratch
files are created on Linux `/tmp`: WSL's Windows-backed filesystem can overflow
32-bit inode fields and does not supply normal FIFO semantics. Override this
with `--native-work-root` only when another Linux scratch filesystem is needed.
No native chroot or host mount changes are performed.

## 2. Windows Podish run

Build the native CLI first (the browser workload is not needed):

```powershell
$env:CARGO_NET_OFFLINE='true' # optional: only when dependencies are already cached
dotnet build Podish.Cli/Podish.Cli.csproj -c Debug
python tests/ltp/run.py --backend podish
```

The default CLI is `Podish.Cli/bin/Debug/net10.0/Podish.Cli.dll`. `--cli` can
select another build. On a net11-only host the runner defaults
`DOTNET_ROLL_FORWARD=LatestMajor`; the repository TFM remains net10.0.
The runner also works with a Linux-native Podish CLI build.

Every selected Podish case **must** have a passing native baseline from the
exact same build metadata and binary hashes. Native baseline results live at
`artifacts/ltp/results/native.json`; use `--baseline` to override.

Each test runs in a fresh rootfs/container, with the unmodified binary as the
guest executable, `--rm --init --network private --log-driver none`, and no shell.
`--init` is important: LTP's harness treats a parent PID of 1 as an orphaned
test. The basic manifest uses hostfs; permission and sync manifests default to
a tmpfs image because hostfs forces mode `0755` on Windows. This suite does not
yet provide the full filesystem-backend matrix of `Podish.PjdFs`.

The runner uses `KCONFIG_SKIP_CHECK=1` to bypass host kernel configuration
inspection, not syscall assertions. The suite is intentionally filtered to
behavioral tests which do not need a real kernel config. It does not fabricate
missing `/proc` entries, suppress ENOSYS, weaken expected errno, or mark failures
as passes. Native and guest effective UID differ for the basic manifest (normal
host user vs guest
root); privileged/identity-switching cases are excluded.

## Results and diagnostics

```powershell
python tests/ltp/run.py --backend podish --filter 'read*,pread*'
python tests/ltp/run.py --backend podish --filter wait401 --timeout 10 --trace
python -m unittest discover -s tests/ltp -p 'test_*.py'
```

- Default host timeout is 30 seconds **per binary**, independent of guest timers.
- Timed-out process trees are killed by the host (Windows `taskkill /T`, Linux
  process groups). Scratch files, guest logs and rootfs are retained for diagnosis.
- JSON reports: `artifacts/ltp/results/{native,podish}.json`, plus immutable
  run-local `report.json` and per-case `output.log` in `artifacts/ltp/runs/`.
- `--trace` additionally enables the trace log level and writes each case's
  syscall/engine trace to `engine.log` beside `output.log`.
- Exit code is nonzero for any FAIL, BROKEN, SKIP, PARTIAL or TIMEOUT. There is
  no XFAIL allowlist to silently turn known syscall defects green.
- `PARTIAL` is reported when at least one supported variant passed but one or
  more variants were `TCONF`-skipped and therefore native assertion coverage was
  not reached. It is never treated as PASS.
- LTP summary counts are retained, including partial skips and multiple variants.
  Warnings, crashes, malformed/missing summaries are not passes. Counts are
  aggregated assertions, not unique syscall counts.

When adding a case, inspect its source and setup/resource requirements, add it
to `cases.json`, rebuild, and first obtain a clean Linux baseline. Keep failures
visible and document unsupported behavior rather than changing upstream tests.

## Licensing

Upstream LTP retains its original per-file licenses and copyright notices
(commonly GPL-2.0-only or GPL-2.0-or-later). The pinned submodule contains the
source and license material corresponding to these test executables. Generated
assets are ignored by Git and are not part of the distributed Podish runtime.
