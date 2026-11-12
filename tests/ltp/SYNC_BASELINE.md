# LTP synchronization batch — first baseline

Date: 2026-11-12
Manifest: `tests/ltp/cases-sync.json` (32 cases)
Backend: Windows Podish, tmpfs image, guest root
Native reference: WSL Linux 5.15.153.1, root
LTP: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`

## Results

| Backend | PASS | PARTIAL | FAIL | TIMEOUT | BROKEN | SKIP |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Native Linux | 32 | 0 | 0 | 0 | 0 | 0 |
| Windows Podish | 3 | 12 | 10 | 2 | 2 | 3 |

Podish assertion counts: 76 passed, 48 failed, 18 skipped, 2 broken-test
aborts, 1 warning.

`PARTIAL` means the supported variant(s) passed, but one or more variants were
skipped by LTP (`TCONF`) and therefore the native assertion count was not
reached. It is deliberately not counted as a full PASS.

## Excluded before the comparison

- `futex_waitv01/02/03`: the WSL native kernel is 5.15; LTP requires 5.16.
  They remain candidates for a newer native reference.
- `semop02`: native LTP aborts in setup with `ftok() ... EACCES` on this WSL
  filesystem, so it cannot provide a valid reference.

## What the batch exposes

### 1. `futex_time64` is not dispatched (12 PARTIAL)

Every futex test that has a `__NR_futex_time64` variant gets `TCONF`:
`syscall(422) __NR_futex_time64 not supported on your arch`. The legacy futex
variant passes in the same binaries, and the native baseline runs both variants.
This is a real ABI gap: `futex_time64` is still in the not-dispatched syscall
set. The 12 PARTIAL cases are the futex tests whose supported legacy variant
passed but whose time64 variant was skipped.

### 2. `futex_wait03` hangs (TIMEOUT)

The test starts the futex/threads wait path but does not finish within the
30-second host timeout. The process tree is killed by the runner. This is a
real hang in the synchronization path, not a test setup skip; investigate with
the runner's `--trace` option and a reduced reproducer.

### 3. `futex_wake02` aborts because procfs has no per-thread task directory

```
futex_utils.h:46: TBROK: opendir(/proc/4/task/) failed: ENOENT
```

The test uses `/proc/<pid>/task` to enumerate thread IDs. Podish procfs
provides process entries but not task-directory entries, so the synchronization
test cannot reach its wake assertions. This is a procfs coverage gap that also
affects thread-aware diagnostics.

### 4. `futex_wait05` timing failure

Six assertions report `futex_wait() slept for too long`. The test also reports
that `oom_score_adj`/latency constraints are unavailable. This needs a focused
timing comparison after the functional futex path is stable; do not simply
widen the timing tolerance.

### 5. SysV semaphore metadata and operations are incomplete

- `semget01`: `IPC_STAT` reports `sem_nsems=0` instead of 10 and the wrong
  creator UID.
- `semget02`: an `IPC_CREAT | IPC_EXCL` collision unexpectedly succeeds.
- `semop01`: semaphore values after `semop` are wrong; the operations are not
  reflected in `GETALL`/state.
- `semctl01`: `IPC_STAT` content is wrong and `semctl(..., IPC_STAT)` then
  fails with `EINVAL`.
- `semctl02`: an unprivileged `IPC_STAT` unexpectedly succeeds, so permission
  checks are missing.
- `semctl03`: invalid user union arguments return `EINVAL` instead of `EFAULT`.
- `semctl04`: unprivileged `IPC_SET`/permission behavior is wrong.
- `semctl05`: a `SETVAL` beyond `SEMVMX` is accepted.
- `semctl06`: `SEM_UNDO`/array state is wrong across operations.
- `semctl07`: `GETPID`, `GETNCNT`, and `GETZCNT` return `EINVAL`.
- `semctl09`: `SEM_STAT_ANY` is reported unsupported (native kernel supports it).
- `semget05` skips because `/proc/sys/kernel/sem` is missing.
- `semop04` times out while two processes contend on a semaphore, pointing at
  blocking/wakeup behavior rather than a simple value update.
- `semop03` and `semop05` pass; `semctl08` passes.

The failures show that the current SysV semaphore implementation is enough for
some basic operations but does not maintain complete `semid_ds` metadata,
permission checks, semaphore value limits, `SEM_UNDO`, or blocking semantics.

## Next work items

1. Dispatch `futex_time64` and rerun the 12 PARTIAL cases; this is the largest
   single coverage gap.
2. Trace `futex_wait03` timeout and `semop04` blocking wakeup with `--trace`.
3. Add `/proc/<pid>/task` entries so thread-oriented futex tests can run.
4. Repair SysV semaphore metadata (`semid_ds`), `IPC_STAT`, permission checks,
   error precedence, `SEMVMX`, `SEM_UNDO`, `GETPID/GETNCNT/GETZCNT`, and
   blocking behavior.
5. Add `/proc/sys/kernel/sem` for `semget05`; decide whether `SEM_STAT_ANY`
   is in scope.
6. Keep `futex_waitv*` out until the native reference kernel is 5.16+, and
   keep `semop02` out until its native `ftok` prerequisite is stable.

Do not mark PARTIAL as PASS, and do not widen timing tolerances or remove
assertions to make this batch green.

## Reproduce

The sync manifest defaults to tmpfs (`"podishFsBackend": "tmpfs"` and
`"requiresRoot": true`):

```powershell
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-sync `
  --manifest tests/ltp/cases-sync.json
```

Report: `artifacts/ltp-sync/results/podish-tmpfs.json`.
