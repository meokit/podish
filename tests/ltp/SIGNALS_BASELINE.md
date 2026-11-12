# LTP signal and event batch — baseline and fixes

Date: 2026-11-12
Manifest: `tests/ltp/cases-signals.json` (46 cases)
Backend: Windows Podish, tmpfs image, guest root
Native reference: WSL Linux 5.15.153.1, root, i386 static executables
LTP: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`
Branch: `fix/ltp-signal-semantics`

## Results

| Backend | PASS | FAIL | BROKEN | TIMEOUT | SKIP |
| --- | ---: | ---: | ---: | ---: | ---: |
| Native Linux | 46 | 0 | 0 | 0 | 0 |
| Windows Podish, tmpfs (before fixes) | 30 | 9 | 5 | 2 | 0 |
| **Windows Podish, tmpfs (after fixes)** | **46** | **0** | **0** | **0** | **0** |

After fixes: **737 assertions passed, 0 failed, 0 broken, 0 skipped**.

## Excluded before the comparison

- `signal06`: native LTP reports `TCONF: Only test on x86_64.` The reference
  binary is i386 and cannot provide a comparable baseline.

## Fixes made

### 1. `sigaltstack(2)` was not dispatched
- Added syscall 186 dispatch and `SysSigAltStack`.
- Implemented per-thread stack state, `SS_ONSTACK`, `SS_DISABLE`,
  `SS_AUTODISARM`, invalid-flag `EINVAL`, and `MINSIGSTKSZ` `ENOMEM`.
- `DeliverSignal` switches `SA_ONSTACK` handlers to the installed alternate
  stack. `SS_ONSTACK` is derived from the user SP, so returning from a nested
  handler cannot reset the stack to its top and overwrite an outer frame.
- Active alternate stacks cannot be replaced or disabled (`EPERM`). Initial
  queries report `SS_DISABLE`; Linux-compatible `SS_ONSTACK` input is accepted,
  and disabling ignores the supplied address/size.
- `SS_AUTODISARM` clears the configuration on handler entry. RT frames save
  `uc_stack` and `rt_sigreturn` restores it; legacy i386 frames do not restore
  alternate-stack configuration. Fork/vfork inherit it, while `CLONE_VM`
  without `CLONE_VFORK` starts disabled. Exec resets it.

### 2. `SA_RESETHAND` reset only the handler and still auto-blocked the signal
- Resetting a one-shot action now keeps the action metadata, including
  `SA_SIGINFO`, and sets only the handler to `SIG_DFL`, matching Linux.
- Automatic self-masking is now controlled only by `SA_NODEFER`.
  `SA_RESETHAND` therefore still masks the delivered signal unless
  `SA_NODEFER` is set. Explicit `sa_mask` entries apply in either case.

### 3. Forked processes lost the vDSO signal-return trampolines
- `SyscallManager.Clone` now copies `SigReturnAddr` and `RtSigReturnAddr`.
  Forked children share the copied vDSO mapping, but previously had zero
  addresses and returned through address 0 after a handler.
- This fixed the `rt_sigsuspend` and `rt_sigqueueinfo` crashes/timeouts and is
  the root cause behind several signal-frame failures in forked test workers.

### 4. Signal delivery lacked sender `siginfo` and permission checks
- `kill`, `tkill`, and `tgkill` populate the sender TGID and real UID;
  `kill` uses `SI_USER`, and thread sends use `SI_TKILL`.
- A sender's real OR effective UID may match the target real OR saved UID.
  Root/CAP_KILL, same-thread-group sends and same-session `SIGCONT` are also
  permitted. Process-group sends apply these checks per target.
- `rt_sigqueueinfo` resolves a supplied TID to its thread group and posts to
  the shared pending queue, so any eligible thread can consume the signal.
- Both queueinfo syscalls reject non-negative `si_code` and `SI_TKILL` before
  target lookup except when targeting the calling TID itself. Bad `siginfo`
  pointers return `EFAULT`.
- Reliable real-time sends enforce `RLIMIT_SIGPENDING` across processes and
  threads of the target real UID. Queue records retain their charged UID
  through credential changes; dequeuing releases the charge. Limits remain
  unsigned, including `RLIM_INFINITY`.
- `kill` never fails with `EAGAIN` on queue exhaustion: a coalescing pending
  bit still delivers a signal without allocating another `siginfo` record.

### 5. Signal wait and checkpoint races
- `rt_sigtimedwait` now accepts an empty signal set and is interruptible, as
  Linux does.
- Ignored signals that are blocked remain queued and consumable by
  `sigwaitinfo` / `sigtimedwait` / `signalfd`, while unblocked ignored signals
  are still discarded.
- A futex wake racing with signal delivery now completes the futex wait
  successfully instead of reporting `EINTR`, preventing LTP checkpoint wakeups
  from being lost.
- `WriteSigInfoForTask` keeps the syscall manager active during asynchronous
  signal-wait completion so an invalid `siginfo` pointer returns `EFAULT`
  instead of generating a guest `SIGSEGV`.
- `rt_sigsuspend` returns `ERESTARTSYS` so the handler runs, then resumes with
  `EINTR` through the never-restart syscall list.

### 6. `signalfd` descriptor and flags
- Reassigning an existing signalfd returns the same descriptor instead of 0.
- Invalid flags are rejected with `EINVAL`; invalid/non-signalfd descriptors
  keep `EBADF`/`EINVAL` precedence.

### 7. Missing procfs taint file
- Added `/proc/sys/kernel/tainted` with value `0`, allowing `kill13` to run its
  actual signal assertions instead of aborting during setup.

## Verification

- Signal LTP batch: **46/46 PASS**, 737 assertions.
- Core LTP batch: **50/50 PASS**.
- Permission LTP batch: **70/71 PASS**, only the documented `access02`
  `/bin/sh` asset dependency remains.
- Synchronization LTP batch: **31 PASS, 1 SKIP** (`futex_wake04`'s existing
  configuration skip), no failed/broken cases.
- C#: **1824 passed, 0 failed, 1 pre-existing skip**:
  - `Fiberish.Tests`: 1743 passed, 1 skip.
  - `Fiberish.SilkFS.Tests`: 13 passed.
  - `Podish.Wayland.Tests`: 19 passed.
  - `Podish.Pulse.Tests`: 49 passed.
- New/updated C# coverage:
  - sigaltstack install/query/disable, invalid flags and short stack;
  - signalfd reassignment and flag validation;
  - `rt_sigqueueinfo` `si_code`/`EFAULT`/`ESRCH` precedence;
  - Linux-correct `SA_RESETHAND` metadata, masking, and handler reset.
- Review follow-up adds **36 regression cases**, covering all 12 findings:
  alternate-stack initial/active/nested/autodisarm behavior, fork versus
  `CLONE_VM`, UID permission combinations, real sender identity, self-send
  `si_code` exceptions, shared signal delivery, unsigned/unlimited and per-UID
  queue limits, and allocation-free notification by `kill`.
- The new tests first reproduced **35 failures** before the fixes. All tests
  now pass, and all nine independent static-i386 probe groups match native
  Linux output without modifying upstream LTP sources or assertions.

## Reproduce

```powershell
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-signals `
  --manifest tests/ltp/cases-signals.json
```

Report: `artifacts/ltp-signals/results/podish-tmpfs-fixed.json`.
