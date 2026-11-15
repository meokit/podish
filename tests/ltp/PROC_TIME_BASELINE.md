# LTP process lifecycle, resource limits, timers, and inotify batch

Date: 2026-11-13 (first baseline), 2026-11-15 (third review fixes)
Manifest: `tests/ltp/cases-proc-time.json` (98 cases)
Backend: Windows Podish, tmpfs image, guest root
Native reference: WSL Linux 5.15.153.1, root, i386 static executables
LTP: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`
Branch: `fix/ltp-proc-time`

## Results

| Backend | PASS | PARTIAL | SKIP | FAIL | BROKEN | TIMEOUT |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Native Linux | 98 | 0 | 0 | 0 | 0 | 0 |
| Windows Podish, tmpfs (first baseline) | 49 | 1 | 1 | 26 | 12 | 9 |
| **Windows Podish, tmpfs (after fixes)** | **89** | **2** | **2** | **1** | **4** | **0** |

`PARTIAL` means at least one supported variant passed while others reported
`TCONF` (e.g. `waitpid01` skips coredump cases because `RLIMIT_CORE` is 0,
`prctl02` reports 6 unsupported `prctl` options). Both are reported as
`PARTIAL`, not `PASS`, on purpose.

## Excluded before the comparison

- Multi-binary `TEST_APP` cases (`execve01/02/04/05`, `execveat01/02`,
  `setpgid03`, `prctl06_execve`) are not in the manifest; the runner image only
  carries one executable per case.
- Cases whose native WSL2 result is not `PASS` (`fork13` timeout, `fork14`,
  `execve06`, `getrusage04`, `prctl07` skip, `clone10`, `execveat_errno`,
  `getrusage03` broken, `timerfd01`, `inotify13` fail) were dropped from the
  manifest instead of being counted.

## Fixes made

### 1. Process creation, exec, wait and process groups
- `CLONE_PARENT` is accepted (child is parented to the caller's parent) and
  rejected with `EINVAL` in init; namespace flags (`CLONE_NEW*`) now return
  `EPERM` without `CAP_SYS_ADMIN` and `EINVAL` when modelable instead of
  `EINVAL` from the generic flag check.
- `execve` reports `ENAMETOOLONG` / `ENOTDIR` from the path walker instead of
  collapsing everything to `ENOENT`, and rejects directories with `EACCES`.
- `wait4` validates `options` (`EINVAL`), returns `ESRCH` for `INT_MIN`, and
  keeps the emulator's `WNOWAIT` extension; `waitid` validates `idtype`,
  `options`, `id` and writes a cleared `siginfo` for `WNOHANG`.
- `setpgid` implements the `EINVAL`/`ESRCH`/`EPERM`/`EACCES` ordering
  (including "child already exec'd" via the new `Process.HasExeced`), and
  requires the target process group to exist in the same session.
- `setsid` also fails with `EPERM` when any live process is led by the caller.
- `getpgid(pid)` resolves the target process instead of always returning the
  caller's group.

### 2. Resource limits
- Legacy i386 `getrlimit(2)` (signed variant) reports `RLIM_INFINITY` as
  `LONG_MAX`, while `ugetrlimit`/`prlimit64` report `ULONG_MAX`, matching the
  Linux compat split that `getrlimit03` asserts.
- `RLIMIT_CPU` soft/hard limits are enforced by the scheduler
  (`SIGXCPU` once per second past the soft limit, `SIGKILL` past the hard one).
- `RLIMIT_FSIZE` truncates writes and raises `SIGXFSZ`/`EFBIG`, and
  `RLIMIT_NOFILE` above `fs.nr_open` is rejected with `EPERM`.

### 3. `prctl`
`PR_SET/GET_PDEATHSIG`, `PR_GET/SET_DUMPABLE`, `PR_GET/SET_TIMING`,
`PR_SET/GET_NAME`, `PR_GET/SET_TSC`, `PR_SET/GET_TIMERSLACK`,
`PR_SET/GET_CHILD_SUBREAPER`, `PR_SET/GET_NO_NEW_PRIVS`,
`PR_SET/GET_THP_DISABLE`, `PR_SET_SECUREBITS`, `PR_CAPBSET_DROP` implement the
Linux argument validation and error codes. `PR_GET_NAME`/`PR_SET_NAME` share
state with `/proc/<pid>/comm`, and `PR_SET_CHILD_SUBREAPER` drives real
reparenting (`KernelScheduler.ReparentChildrenToInit` walks up to the nearest
subreaper and delivers the parent-death signal).

### 4. Timers and itimers
- `ITIMER_VIRTUAL`/`ITIMER_PROF` are implemented as CPU-time interval timers
  (`SIGVTALRM`/`SIGPROF`), so `setitimer01`/`getitimer01` exercise all three
  timers; `it_value` is reported against the real clock so partial milliseconds
  no longer inflate `tv_usec`.
- `alarm(2)` returns the remaining time rounded up, as Linux does.
- `clock_nanosleep` returns `EOPNOTSUPP` for clocks that cannot be slept on, and
  writes `rem` (and reports `EFAULT`) only for relative sleeps.
- `timerfd_create/settime/gettime` validate `clockid`, `flags` and the
  `itimerspec`, distinguish `EBADF` from `EINVAL` for non-timerfd descriptors,
  and propagate `EFAULT` from `old_value`.

### 5. Host timer precision (fixes `nanosleep01`, `clock_nanosleep02`, `prctl09`)
`KernelScheduler.WaitForEvent` now waits on a Windows
`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` timer together with the scheduler wake
event for waits up to 2 s, falling back to `ManualResetEventSlim.Wait`
elsewhere. Measured on this host:

| request | ManualResetEventSlim | timeBeginPeriod(1) | high-resolution timer |
| --- | ---: | ---: | ---: |
| 10 ms | 10.55 ms | 10.01 ms | 10.04 ms |
| 25 ms | 27.09 ms | 25.00 ms | 25.05 ms |
| 100 ms | 105.46 ms | 106.04 ms | 100.06 ms |
| 1000 ms | 1001.04 ms | 1002.11 ms | 1000.06 ms |

`timeBeginPeriod(1)` is already active and does not help at 100 ms, which is
why the LTP threshold (2.5 ms) was missed before.

### 6. procfs entries needed by the batch
`/proc/<pid>/comm`, `/proc/<pid>/timerslack_ns` (read/write),
`/proc/sys/fs/nr_open`, `/proc/sys/fs/inotify/{max_queued_events,
max_user_instances, max_user_watches}`, `/proc/sys/kernel/core_pattern` and
`/proc/sys/net/ipv4/conf/{lo,default,all}/tag`.

### 7. inotify event delivery (new)
Previously `InotifyInode` only stored watches; no VFS operation ever produced an
event, so every inotify case blocked forever. Added:

- an inode-indexed watch registry (`InotifyEvents`) with parent-directory
  delivery (`IN_ISDIR`, name) and `move` cookies;
- `IN_OPEN`/`IN_ACCESS`/`IN_MODIFY`/`IN_CLOSE_WRITE`/`IN_CLOSE_NOWRITE`,
  `IN_ATTRIB`, `IN_CREATE`, `IN_DELETE`, `IN_MOVED_FROM`/`IN_MOVED_TO`,
  `IN_DELETE_SELF`, `IN_MOVE_SELF`;
- `IN_ONESHOT`/`IN_IGNORED` retirement, `IN_EXCL_UNLINK`, queue-limit
  `IN_Q_OVERFLOW` (`wd=-1`), and coalescing of repeated `IN_MOVE_SELF`;
- `/proc/<pid>/fdinfo/<fd>` `inotify wd:... ino:... sdev:... mask:...` lines.

Deletion handlers now capture the target inode **before** the backend unbinds
the dentry (`Rmdir`/`Unlink` null `Dentry.Inode`), and only emit
`IN_DELETE_SELF` when the last link is gone, so hard-link unlink reports
`IN_ATTRIB` alone.

### 8. Async syscall user-copy faults (fixes `clock_nanosleep01`)
`Engine.TryResolveUserAccessFault` fell back to the guest page-fault path when
`CurrentSyscallManager` was null, which happens while an async syscall's
continuation runs (`SyscallManager.Handle` restores the context in `finally`
before the handler resumes from its awaiter). A bad user buffer then queued a
guest `SIGSEGV` even though the syscall correctly returned `EFAULT`. The
resolver now uses the owner task's memory when `PendingSyscall != null`, so
every async syscall (`nanosleep`, `read`, `recvmsg`, `wait4`, `epoll_wait`,
`timerfd_settime` …) keeps `EFAULT` semantics. Regression tests:
`Fiberish.Tests/Core/UserCopyFaultTests.cs`.

### 9. VFS robustness
`Dentry.UntrackFromSuperBlock` repairs an already-cleared registration with a
warning instead of throwing, so a removal racing with address-space teardown no
longer kills the scheduler.

### 10. Review follow-up: inotify and process ABI correctness
- inotify watches are now keyed by `(SuperBlock, Ino)` (Linux semantics): two
  hard links to one inode share a descriptor, and re-adding a path that resolves
  to a different inode creates a new watch instead of leaving a stale registry
  entry. Per-user `max_user_instances` / `max_user_watches` are enforced
  (`EMFILE` / `ENOSPC`).
- Deleting a watched object retires the watch and queues `IN_IGNORED` even when
  the caller did not subscribe to `IN_DELETE_SELF`.
- `IN_CLOSE_WRITE`/`IN_CLOSE_NOWRITE` are emitted when the last reference to the
  open file description is closed (not on every `close` of a dup'd fd, and not
  for mmap holds).
- Rename cookies are unique (`1,2,3,…`), a partial read no longer resets the
  overflow marker, and inotify names are copied as raw bytes (non-UTF-8 names
  survive). Rename-overwrite marks the replaced inode as unlinked for
  `IN_EXCL_UNLINK`.
- `PR_SET_NO_NEW_PRIVS` now suppresses setuid/setgid privilege gain on both ELF
  and shebang exec; shebang exec also sets `HasExeced`.
- `waitid(P_PGID, 0)` targets the caller's process group, and `clock_nanosleep`
  distinguishes invalid clock IDs (`EINVAL`) from valid but non-sleepable ones
  (`EOPNOTSUPP`), including `CLOCK_TAI`.
- `/proc/sys/net/ipv4/conf/*/tag` is readable/writable, and inotify instance /
  watch sysctls take effect.

### 11. Second review follow-up: permissions, lifecycle and runtime state
- `PR_SET_NO_NEW_PRIVS` now preserves the capability sets across exec;
  setuid/setgid privilege gain and capability normalization only happen for an
  actual setid transition. Fork no longer restores capabilities that a root
  process deliberately dropped.
- `inotify_add_watch` requires at least one event bit, rejects
  `IN_MASK_CREATE|IN_MASK_ADD`, and checks read permission on the watched inode
  (`EACCES` for a non-owner on a 0600 file).
- Inotify registries, per-user instance/watch quotas, queue limits and
  `/proc/sys/net/ipv4/conf/*/tag` are owned by `MemoryRuntimeContext`, so
  independent containers no longer share limits or state. The registry follows
  the watched inode's runtime, not the inotify instance's.
- `ITIMER_VIRTUAL` arms, reports and expires against user CPU time only;
  `ITIMER_PROF` uses user + system.
- Unlink/rmdir of an inode with open/mmap references emits `IN_ATTRIB` now and
  defers `IN_DELETE_SELF`/`IN_IGNORED` until the last inode reference is gone.
  Rename-overwrite reports the replaced target; `RENAME_EXCHANGE` emits both
  move pairs, both `IN_MOVE_SELF` events and `IN_ISDIR` for directories.
- `IN_EXCL_UNLINK` is decided per removed dentry, so a surviving hard link still
  receives parent events.
- Consecutive identical unread events coalesce (not just `IN_MOVE_SELF`), the
  watch registry uses per-inode membership dictionaries (O(1) register/remove),
  wake signals are only issued when a waiter exists, and the absolute-path
  allocation was removed from `inotify_add_watch`.
- CPU accounting is O(1) per process snapshot: completed slices are accumulated
  and only the running thread contributes a live delta.
- `KernelScheduler.Dispose` releases both wait handles; `PR_GET_THP_DISABLE`
  reports the value set by `PR_SET_THP_DISABLE`.

### 12. Third review follow-up: eight fixes and targeted regressions
- `WakeUp()` publishes its coalesced work item before checking for waiters, so
  a waiter starting during publication cannot lose its only wake. A controlled
  channel-publication test covers this interleaving.
- `PR_CAPBSET_READ`/`PR_CAPBSET_DROP` maintain a real capability bounding set,
  validate capability numbers, and preserve independent copies across fork.
  Setid exec respects the bounding set; capset rejects permitted-set increases,
  effective bits outside permitted, and new inheritable bits outside the bound.
- Finalization scopes are per inode and use exception-safe `using` lifetimes.
  Nested scopes retain pending deletion notifications until their outermost
  disposal; unrelated inodes are delivered immediately. Injected unlink,
  unlinkat, rmdir and rename backend failures cover cleanup and pending delivery.
- Rename-overwrite reports `IN_ATTRIB` even if another hard link survives.
- Directory self-watches include `IN_ISDIR` on ACCESS/OPEN/CLOSE/ATTRIB, but not
  on MOVE_SELF/DELETE_SELF.
- `inotify_add_watch` rejects unknown mask bits while accepting known output
  bits alongside a requested event, as Linux does.
- Positive sub-millisecond itimer values and intervals round up to the
  scheduler's 1 ms granularity instead of silently disarming. CPU timer arming
  also rounds up the starting clock, preventing early expiry at fractional ms.
- The native precise wake event is created only for Windows high-resolution
  waits, with a pending-work recheck after first creation. Idle schedulers do
  not allocate either precise wait handle.

Added 33 regression cases, including `CapabilityBoundingSetTests` and
`InotifyFinalizationTests`, plus extensions to the scheduler, itimer and inotify
test classes. Upstream LTP tests and assertions remain unchanged.

### 13. Fourth review follow-up: attribute events, timer clocks and error paths
- `chmod`, `fchmod`, `fchmodat`, `chown`, `fchown`, `fchownat`, `lchown` and
  both `utimensat` variants emit `IN_ATTRIB` through one helper, so a watch on an
  open file sees metadata changes regardless of the syscall used.
- `setitimer(2)` validates the new `itimerval` before writing `old_value`; an
  `EINVAL` no longer clobbers the caller's output buffer.
- Absolute `timerfd` and POSIX timer deadlines use the same monotonic clock as
  `CLOCK_MONOTONIC` guest reads instead of scheduler-relative ticks. `timerfd`
  also stores its `clockid` and reports remaining time against the real clock.
  POSIX timer `itimerspec` values are validated before old-value output, and
  32/64-bit second-to-millisecond conversions are overflow-checked.
- `/proc/<pid>/comm` and `timerslack_ns` writes require the caller to target its
  own thread group or hold `CAP_SYS_RESOURCE` / `CAP_SYS_NICE`.
- `execve` clears `PR_SET_PDEATHSIG`; `setsid` clears the caller's controlling
  terminal association.
- A failed `VMAManager.Mmap` removes a partially inserted VMA, unregisters its
  rmap attachments and releases acquired mapping/anon references while leaving
  the caller's file ownership intact.
- The inotify finalization scope has a no-watch fast path (benchmarked at
  0.6 ns/op instead of roughly 106 ns/op), and `Podish.Benchmarks` covers the
  inotify, VFS, scheduler, CPU-accounting and mmap hot paths.

Added 10 regression cases in the owning modules: `CpuItimerTests`,
`InotifyLifecycleTests`, `ProcFsTests`, `ExecRawBytesTests`,
`LtpRegressionTests`, `VirtualFDsTests` and `PosixTimerTests`. Upstream LTP
tests and assertions remain unchanged.

## Remaining differences (not defects in the emulated ABI)

| Case | Status | Why |
| --- | --- | --- |
| `prctl10` | FAIL | `PR_TSC_SIGSEGV` requires RDTSC to raise `SIGSEGV`; the interpreter has no per-process TSC trap. |
| `clock_nanosleep03` | BROKEN | Requires time namespaces (`unshare(CLONE_NEWTIME)` + `/proc/self/timens_offsets`). |
| `timerfd04`, `inotify03` | BROKEN | Require a real block device (`.needs_device`), which the runner image does not provide. |
| `setrlimit04` | BROKEN | Needs `/bin/true` as a test asset; the image ships only LTP binaries. |
| `fork05` | SKIP | Native baseline also skips (`modify_ldt` unsupported on i386 compat). |
| `clone09` | SKIP | Requires `CLONE_NEWNET`; the emulator returns `EINVAL` and the test reports `TCONF`. |

`clock_nanosleep02` is host-jitter sensitive: three isolated reruns pass
(7/7 assertions), but a full-batch run on a busy host can exceed the 2.5 ms LTP
threshold on one sample group.

## Verification

- LTP proc-time batch: `89 PASS / 2 PARTIAL / 2 SKIP / 1 FAIL / 4 BROKEN`
  (0 timeouts) with `--timeout 15`; `timerfd_settime02` and `inotify09` carry a
  per-case `timeoutSeconds: 150` because they are multi-second race tests.
- `Fiberish.Tests`: 1941 passed, 0 failed, 1 skipped (includes
  `UserCopyFaultTests`, `InotifyEventTests`, `InotifyLifecycleTests`,
  `InotifyAddWatchValidationTests`, `RuntimeStateIsolationTests`,
  `CpuItimerTests`, `CpuAccountingTests`, `KernelSchedulerDisposeTests`,
  `CapabilityBoundingSetTests`, `InotifyFinalizationTests`, `ProcFsTests`,
  `ExecRawBytesTests`, `LtpRegressionTests`, `VirtualFDsTests` and
  `PosixTimerTests`).
- Targeted LTP rerun after the fixes: 11 PASS / 1 PARTIAL
  (`setsid01`, `prctl01/02/03/05/08/09`, `timerfd02`, `timerfd_create01`,
  `timerfd_gettime01`, `timerfd_settime01/02`).
- After merging main's net-io/fs-fd batches (merge `cc20f69`): 2029 passed,
  0 failed, 1 skipped. The full proc-time batch kept the same statuses; net-io
  (63 PASS / 5 PARTIAL / 4 BROKEN / 1 SKIP) and fs-fd (106 PASS / 8 SKIP /
  1 PARTIAL) matched their branch baselines case by case.
- LTP runner self-tests: 20/20.
