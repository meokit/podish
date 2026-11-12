# LTP synchronization batch — baseline and fixes

Date: 2026-11-12
Manifest: `tests/ltp/cases-sync.json` (32 cases)
Backend: Windows Podish, tmpfs image, guest root
Native reference: WSL Linux 5.15.153.1, root
LTP: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`
Branch: `fix/ltp-sync-semantics`

## Results

| Backend | PASS | PARTIAL | FAIL | TIMEOUT | BROKEN | SKIP |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Native Linux, root (WSL) | 32 | 0 | 0 | 0 | 0 | 0 |
| Windows Podish, tmpfs (before fixes) | 3 | 12 | 10 | 2 | 2 | 3 |
| **Windows Podish, tmpfs (after fixes)** | **31** | **0** | **0** | **0** | **0** | **1** |

After fixes:
- 204 assertions passed, 0 failed, 0 broken, 3 skipped (2 in `semctl03` skipped by LTP upstream for libc variant, 1 in `futex_wake04` due to `hugetlbfs` requirement).
- Every failing/timed-out/broken/partial test now passes.

## Fixes made

### 1. `futex_time64` dispatch (syscall 422)
- Added syscall 422 (`futex_time64`) dispatch in `SyscallHandlers.cs` to `SysFutexTime64`.
- Implemented `TryReadAbsoluteTimespec64TimeoutMs` and 64-bit timespec parsing for `FUTEX_WAIT`, `FUTEX_WAIT_BITSET`, `FUTEX_WAIT_REQUEUE_PI`, and `FUTEX_LOCK_PI`.
- Updated `FiberTask.cs` to prevent restarting `futex_time64` waits on signals.
- This fixed all 12 `PARTIAL` cases (`futex_wait01/02/04/06/07`, `futex_wait_bitset01`, `futex_wake01/03/05`, `futex_cmp_requeue01/02/03`).

### 2. Thread state and `/proc/<pid>/task` directory
- In `ProcFsManager.cs`, corrected `GetProcessState`: previously required `process.Threads.All(Waiting)`, meaning a multi-threaded process where a helper thread polled for the leader thread to sleep could never see state `'S'`, causing a 30s timeout deadlock in `futex_wait03`. It now reflects the leader thread's status.
- Added `/proc/<pid>/task` and `/proc/<pid>/task/<tid>/stat` to `ProcFs.cs` and `ProcFsManager.cs`, enabling `futex_utils.h`'s `wait_for_threads` to enumerate thread states.
- This fixed `futex_wait03` (timeout) and `futex_wake02` (broken `opendir(/proc/4/task/) ENOENT`).

### 3. Windows timer precision (`timeBeginPeriod`)
- In `KernelScheduler.cs`, enabled `timeBeginPeriod(1)` during scheduler execution on Windows, reducing timer interrupt granularity from 15.6 ms to 1 ms.
- This resolved `futex_wait05` timing oversleep failures (slept for 16ms instead of 2ms) and enabled `semop04`'s 1000 contention loops to complete in ~5 seconds rather than hitting the 30-second host timeout.

### 4. `/proc/sys/kernel/sem` and `/proc/sysvipc/sem`
- Added `/proc/sys/kernel/sem` under `ProcSysKind.Kernel` reporting `32000\t1024000000\t500\t128\n` (`SEMMSL`, `SEMMNS`, `SEMOPM`, `SEMMNI`).
- Added `/proc/sysvipc/sem` providing kernel-compatible sequential listings of active semaphore sets.
- This fixed `semget05` (was skipped due to missing sysctl file) and `semctl09` (verifies `semusz` and `semaem` against `/proc/sysvipc/sem`).

### 5. SysV semaphore metadata, permissions, limits, and operations
- **Metadata**: implemented complete 64-byte `struct semid64_ds` serialization for `IPC_STAT` and `SEM_STAT`/`SEM_STAT_ANY`, including `cuid`, `cgid`, `uid`, `gid`, `mode`, `sem_nsems`, `otime`, and `ctime`.
- **Permissions**: added DAC permission checks (`CheckPermission`) for owner, group, and other across `semget`, `semop`, and `semctl` (`IPC_STAT`, `GETVAL`, `GETALL`, `GETPID`, `GETNCNT`, `GETZCNT`, `SETVAL`, `SETALL`).
- **Privilege checks**: `IPC_RMID` and `IPC_SET` enforce creator or owner check and return `EPERM` for unprivileged callers.
- **Commands**: implemented `GETALL`, `SETALL` (with `ERANGE` for values > `SEMVMX`), `GETPID`, `GETNCNT`, `GETZCNT`, `IPC_INFO`, `SEM_INFO`, `SEM_STAT`, and `SEM_STAT_ANY`.
- **SEM_UNDO**: implemented `process.SemUndo` tracking per process and automatic unwinding on process termination (`SysVSemManager.OnProcessExit` invoked from `FinalizeProcessExit`).
- **Array indexing**: matched Linux's 0-based semaphore array indexing for `SEM_INFO`, `SEM_STAT`, and `SEM_STAT_ANY`.

## Verification
- Sync LTP batch (`cases-sync.json`): **31 PASS / 0 FAIL / 0 TIMEOUT / 0 BROKEN / 1 SKIP** (204 passed assertions).
- Core LTP batch (`cases.json`): **50/50 PASS**.
- Permission LTP batch (`cases-permissions.json`): **70/71 PASS** (1 known `/bin/sh` asset dependency).
- All C# tests: **1784 passed, 0 failed, 1 pre-existing skip**.
  - `Fiberish.Tests`: 1703 passed, 0 failed, 1 skip.
  - `Fiberish.SilkFS.Tests`: 13 passed, 0 failed.
  - `Podish.Wayland.Tests`: 19 passed, 0 failed.
  - `Podish.Pulse.Tests`: 49 passed, 0 failed.

## Reproduce
```powershell
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-sync `
  --manifest tests/ltp/cases-sync.json
```
Report: `artifacts/ltp-sync/results/podish-tmpfs-fixed.json`.
