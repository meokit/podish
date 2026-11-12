# LTP signal and event batch — first baseline

Date: 2026-11-12
Manifest: `tests/ltp/cases-signals.json` (46 cases)
Backend: Windows Podish, tmpfs image, guest root
Native reference: WSL Linux 5.15.153.1, root, i386 static executables
LTP: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`

## Results

| Backend | PASS | FAIL | BROKEN | TIMEOUT | SKIP |
| --- | ---: | ---: | ---: | ---: | ---: |
| Native Linux | 46 | 0 | 0 | 0 | 0 |
| Windows Podish | 30 | 9 | 5 | 2 | 0 |

Podish assertion counts: 671 passed, 21 failed, 5 broken-test aborts.

## Excluded before the comparison

- `signal06`: native LTP reports `TCONF: Only test on x86_64.` The reference
  binary is i386 and cannot provide a comparable baseline.

## What the batch exposes

### 1. Alternate signal stack is not implemented

`sigaltstack01` and `sigaltstack02` return `ENOSYS` for `sigaltstack(2)`.
The second case also expects argument validation (`EINVAL` for bad flags and
`ENOMEM` for a stack below `MINSIGSTKSZ`) before any setup.

### 2. `rt_sigsuspend` crashes

`rt_sigsuspend01` is killed by `SIGSEGV` after the test installs a handler and
suspends. This is separate from the `sigsuspend` libc cases (`sigsuspend01/02`),
which pass, so the raw syscall path or its signal-frame delivery is suspect.

### 3. `SA_RESETHAND` semantics are incomplete

`sigaction01` fails three assertions:

- `SA_RESETHAND` incorrectly clears `SA_SIGINFO`;
- `SA_RESETHAND` does not mask the delivered signal while its handler runs;
- the signal remains unexpectedly masked after handler return when it was
  originally in `sa_mask`.

### 4. `kill` error and blocking semantics

- `kill05` accepts a signal delivery that should fail. The case targets a
  signal handling/permission distinction rather than a simple positive send.
- `kill10` hangs until the host timeout. It exercises signal blocking and
  retry behavior, pointing at the signal-wait wakeup path.

### 5. `rt_sigqueueinfo` / `rt_tgsigqueueinfo`

- `rt_sigqueueinfo01` reports `ESRCH` for a valid target.
- `rt_sigqueueinfo02` reports `ESRCH` where it expects an invalid
  `si_code` to be rejected with `EPERM`.
- `rt_tgsigqueueinfo01` hangs until the host timeout, separate from the
  immediate errors above.

### 6. `tgkill` does not enforce pending-signal limits

`tgkill02` expects `EAGAIN` when `RLIMIT_SIGPENDING` is zero and a real-time
signal is blocked. Podish accepts the send.

### 7. Signal wait family shares one defect

`sigtimedwait01`, `rt_sigtimedwait01` and `sigwaitinfo01` fail the same
assertions:

- an invalid timeout/size path returns `EINVAL` where `EINTR` is expected;
- `siginfo` fields do not match the queued signal;
- the test later crashes with `SIGSEGV`.

These three should be fixed together because they share `tse_sigwait.c` and
the signal queue/wait implementation.

### 8. `signalfd` return and flag validation

- `signalfd01` expects `signalfd()` to return the requested descriptor number
  when given a valid existing descriptor; Podish returns a different number.
- `signalfd02` accepts invalid flags that Linux rejects.

### 9. Missing procfs file blocks `kill13`

`kill13` aborts because `/proc/sys/kernel/tainted` is absent. The file is not
used for the syscall behavior itself; add a stable zero-valued entry unless a
better taint model is introduced.

## Next work items

1. Implement `sigaltstack(2)` with `MINSIGSTKSZ` / flag validation and register
   the alternate stack per thread.
2. Trace and repair the raw `rt_sigsuspend` signal-frame path and the
   `kill10` / `rt_tgsigqueueinfo01` hangs.
3. Correct `SA_RESETHAND` action mutation and automatic signal masking.
4. Enforce target existence and permission rules for `rt_sigqueueinfo` and
   `rt_tgsigqueueinfo`, including `si_code` validation.
5. Enforce `RLIMIT_SIGPENDING` in `tgkill`; distinguish `EAGAIN` from `ESRCH`.
6. Repair `sigtimedwait` / `rt_sigtimedwait` / `sigwaitinfo` together:
   timeout error precedence, `siginfo` population, and signal interruption.
7. Implement `signalfd` descriptor identity and strict flag validation.
8. Add `/proc/sys/kernel/tainted` so `kill13` can execute its assertions.

Do not remove LTP assertions, convert expected failures to skips, or widen the
host timeout to hide hangs.

## Reproduce

```powershell
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-signals `
  --manifest tests/ltp/cases-signals.json
```

Report: `artifacts/ltp-signals/results/podish-tmpfs.json`.
