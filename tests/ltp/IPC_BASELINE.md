# LTP SysV shared memory and message queue batch — selection and baseline

Date: 2026-11-15
Manifest: `tests/ltp/cases-ipc.json` (39 cases)
Backend: Windows Podish, tmpfs image, guest root
Native reference: WSL Linux 5.15.153.1, root, i386 static executables
LTP: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`
Scope: selection and first baseline only; no runtime fixes are included.

> Status: deferred. SysV IPC has lower real-world usage than pipes, Unix
> sockets and POSIX shared memory. The next active batch is
> [PIPE_BASELINE.md](PIPE_BASELINE.md); this document is kept as the reference
> baseline for SysV shared memory and message queues when that work is picked
> up.

## Why this batch

This is the next uncovered classic syscall family. `cases-sync.json` already
covers futexes and SysV semaphores, but SysV shared memory and message queues
have no LTP coverage in any earlier batch. The two halves are currently in very
different states:

- shared memory (`shmget`, `shmat`, `shmdt`, `shmctl`) is implemented and is a
  direct consumer of the post-refactor mapped-memory path, but it has never been
  compared case by case with Linux;
- message queues (`msgget`, `msgsnd`, `msgrcv`, `msgctl`) are not dispatched;
  every selected message test stops at `msgget` with `ENOSYS`.

POSIX message queues were intentionally left out of this batch. The runner
image has no `mqueue` filesystem, so `mq_open`, `mq_unlink`, `mq_timedsend`,
`mq_timedreceive` and `mq_notify` cannot yet produce a meaningful baseline.
They should follow once an mqueue filesystem exists.

The manifest contains only cases with a passing native baseline on the pinned
WSL2 reference. Upstream LTP sources and assertions are unchanged.

## Results

| Backend | PASS | FAIL | BROKEN | SKIP | TIMEOUT | PARTIAL |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Native Linux | 39 | 0 | 0 | 0 | 0 | 0 |
| Windows Podish, tmpfs | 4 | 6 | 26 | 3 | 0 | 0 |

Assertion totals: native 169 passed / 2 skipped; Podish 18 passed / 16 failed /
26 broken / 6 skipped / 6 warnings. The message-queue half accounts for 20 of
the 26 `BROKEN` results because it cannot create a queue at all.

## Excluded before the comparison

| Case | Reason |
| --- | --- |
| `shmget04` | Native `TBROK`: `ftok()` returns `EACCES` for the WSL ext4 temporary directory. |
| `shmctl03` | Native `TFAIL`: WSL2 reports `shmall=4294967295` while `IPC_INFO` returns `4278190079`. |
| `msgctl04` | Native `TBROK`: `ftok()` returns `EACCES` for the WSL ext4 temporary directory. |

These are reference-host limitations, not Podish results; they were removed from
the manifest rather than counted as compatibility failures.

## Remaining non-PASS after the baseline

### Shared memory

| Case | Status | Observation |
| --- | --- | --- |
| `shmat04` | PASS | Attach count returns to zero after `mprotect` splitting. |
| `shmdt01`, `shmdt02` | PASS | Detach and invalid-address rejection match the baseline. |
| `shmctl06` | PASS | High `time` fields are cleared. |
| `shmget02` | SKIP | `/proc/sys/kernel/shmmax` is missing, so the test reports `TCONF`. |
| `shmget03` | BROKEN | `/proc/sysvipc/shm` is missing. |
| `shmget05`, `shmget06` | BROKEN | `/proc/sys/kernel/shm_next_id` is not writable (`EOPNOTSUPP`). |
| `shmat01` | FAIL | `shm_nattch` accounting is wrong on some variants; one attach reports `EINVAL`. |
| `shmat02` | FAIL | A stale segment ID reports `EIDRM` where Linux reports `EINVAL`. |
| `shmat03` | FAIL | A requested NULL address is granted below 64 KiB, then the test dies with `SIGSEGV`. |
| `shmctl01` | BROKEN | `SHM_INFO` (cmd 14) returns `ENOSYS`. |
| `shmctl02` | FAIL | Stale IDs report `EIDRM` instead of `EINVAL`. |
| `shmctl04` | BROKEN | `SHM_STAT_ANY` returns `ENOSYS`. |
| `shmctl05` | SKIP | Raw `syscall(396)` is not dispatched (`ENOSYS`), so LTP reports the syscall unsupported instead of testing the race. |
| `shmctl07` | FAIL | `SHM_LOCK` succeeds but `SHM_LOCKED` is not reported in `shm_perm.mode`. |
| `shmctl08` | FAIL | `IPC_SET` does not apply the requested mode and does not update `shm_ctime`. |

### Message queues

| Case | Status | Observation |
| --- | --- | --- |
| `msgget01` | FAIL | `msgget` returns `ENOSYS`. |
| `msgget02`, `msgget04`, `msgget05` | BROKEN | `ENOSYS`; `msgget04/05` additionally need `/proc/sys/kernel/msg_next_id`. |
| `msgget03` | SKIP | `/proc/sys/kernel/msgmni` is missing. |
| `msgsnd01/02/05/06` | BROKEN | Setup stops at `msgget` with `ENOSYS`. |
| `msgrcv01/02/03/05/06/07/08` | BROKEN | Setup stops at `msgget` with `ENOSYS`. |
| `msgctl01/02/03/05/06/12` | BROKEN | Setup stops at `msgget` with `ENOSYS`. |

## Proposed next work

1. Dispatch the direct i386 IPC syscalls (`shmget` 395, `shmctl` 396,
   `shmat` 397, `shmdt` 398, `msgget` 399, `msgsnd` 400, `msgrcv` 401,
   `msgctl` 402) in addition to the `ipc` multiplexer. The shared-memory tests
   reach the multiplexer through glibc; `shmctl05` shows that the direct path is
   not dispatched, and the message tests show that the multiplexed message
   operations are not implemented either.
2. Add the missing SysV IPC procfs surface: `/proc/sys/kernel/shmmax`,
   `shmmni`, `shmall`, `shm_next_id`, `msgmni`, `msg_next_id`, `msgmax`,
   `msgmnb`, plus `/proc/sysvipc/shm` and `/proc/sysvipc/msg`.
3. Implement SysV message queues end to end: `msgget`, `msgsnd`, `msgrcv`,
   `msgctl`, ID/key lookup, permissions, `MSG_EXCEPT`/`MSG_NOERROR`/`MSG_COPY`,
   blocking sends/receives, queue limits, `IPC_STAT`/`IPC_SET`/`IPC_RMID`, and
   `MSG_INFO`/`MSG_STAT`.
4. Harden shared memory semantics: minimum attach address, `EIDRM` versus
   `EINVAL` on stale IDs, `SHM_LOCKED`, `IPC_SET` mode/`ctime`, `SHM_INFO` and
   `SHM_STAT_ANY`, and correct `shm_nattch` accounting across `mprotect` splits.
5. Keep the batch green on native Linux and re-run earlier batches after the
   changes; LTP counts alone do not replace an ABI review.

## Verification

```powershell
wsl -d Ubuntu -- bash /mnt/c/Users/Jiang/repos/podish/tests/ltp/build.sh \
  /mnt/c/Users/Jiang/repos/podish/artifacts/ltp-ipc \
  /mnt/c/Users/Jiang/repos/podish/tests/ltp/cases-ipc.json
wsl -d Ubuntu -u root -- python3 /mnt/c/Users/Jiang/repos/podish/tests/ltp/run.py \
  --backend native --artifacts /mnt/c/Users/Jiang/repos/podish/artifacts/ltp-ipc \
  --manifest /mnt/c/Users/Jiang/repos/podish/tests/ltp/cases-ipc.json
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-ipc \
  --manifest tests/ltp/cases-ipc.json --timeout 60
```

Reports: `artifacts/ltp-ipc/results/native.json` and
`artifacts/ltp-ipc/results/podish-tmpfs.json`.
