# Initial LTP baseline — 2026-11-12

This is an observed baseline, **not** an expected-failure allowlist. Runtime
compatibility fixes are intentionally separate from the test integration.

**Follow-up:** all 50 cases now pass on the repair branch. See
[FIX_REPORT.md](FIX_REPORT.md) for root causes, fixes and regression results.
The numbers below preserve the original pre-fix snapshot.

## Environment and results

- LTP gitlink: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`
  (`syscalls/select: use kernel timeval for _newselect`, LTP reports 20260930).
- Compiler: Ubuntu GCC 13.3.0, static glibc 2.39 i386 binaries; flags in `build.json`.
- Reference: Ubuntu 24.04 / WSL2 Linux 5.15.153.1, normal non-root user, Linux
  `/tmp` scratch filesystem. The same binary hashes are used in both runs.
- Target: Windows x64, .NET SDK 11.0.100 (net10.0 TFM, runtime roll-forward),
  Debug Podish CLI, hostfs rootfs, guest root, private network, engine init.
- Native: **50 PASS**, 364 passing assertions, no skips or warnings.
- Podish: **33 PASS, 12 FAIL, 4 BROKEN, 1 TIMEOUT**, no fully skipped binaries.
  Across completed/partially completed tests: 226 passing assertions, 26 failing
  assertions, 4 broken and 4 skipped assertions. These are not unique syscall
  counts; a broken/timeout test did not necessarily reach its main assertions.
- A second full Podish run after rebuilding the CLI reproduced the same binary
  statuses. The final run used the default 30-second host timeout per binary.
- Host runner's 11 unit tests pass on both Windows Python and WSL Linux Python.

Detailed machine-readable results and raw logs are generated under
`artifacts/ltp/`; they are not checked into Git. At this initial pre-fix baseline,
a full Podish run returned exit code 1. No failures were automatically exempted.

## Observed incompatibilities

| Binary | Status | Observed behavior (not all root causes established) |
| --- | --- | --- |
| read02 | FAIL | Reading a directory returns EIO instead of EISDIR. |
| dup201 | FAIL | Negative newfd gives EPERM instead of EBADF; newfd at RLIMIT_NOFILE succeeds. |
| pipe2_01 | BROKEN | LTP setup aborts because `pipe2(O_DIRECT)` returns EINVAL; packet-mode pipes are not available. |
| pread02 | FAIL | Reading a pipe and a negative offset succeeds; directory gives EIO instead of EISDIR. |
| pwrite02 | FAIL | Writing a pipe and a negative offset succeeds instead of ESPIPE/EINVAL. |
| readv02 | FAIL | Oversized lengths/counts give EPERM/EFAULT instead of EINVAL; directory gives EIO. |
| writev01 | FAIL | Invalid iov_len/iovcnt give EFAULT instead of EINVAL. |
| ftruncate03 | FAIL | Socket case gets ENOSYS instead of EINVAL; negative length unexpectedly succeeds. |
| getpid01 | BROKEN | Harness setup cannot read `/proc/sys/kernel/pid_max`; getpid assertions are not reached. |
| getppid01 | BROKEN | Same missing procfs pid_max prerequisite. |
| uname02 | FAIL | Invalid destination pointer is reported as success rather than EFAULT. |
| gettimeofday01 | FAIL | Invalid timezone destination is reported as success rather than EFAULT. |
| clock_getres01 | FAIL | Invalid clock ID accepted in some variants; alarm clock variants also skip. |
| nanosleep04 | FAIL | Invalid timespec cases return ENOSYS, not EINVAL. Trace confirms glibc calls legacy i386 `clock_nanosleep` (267), which is not dispatched. |
| epoll_create1_02 | FAIL | Invalid flags are accepted instead of EINVAL. |
| wait401 | TIMEOUT | Harness starts but test does not complete within 30 seconds; host kills process tree. |
| wait402 | BROKEN | Missing `/proc/sys/kernel/pid_max` prerequisite. |

The tests producing TBROK are deliberately retained as coverage gaps, not
misreported as direct getpid/getppid/wait4 syscall failures. Likewise, the
wait401 timeout is not claimed to prove that wait4 itself is the root cause.

## Suggested repair order

1. Pointer validation and errno/argument validation: read/pread/pwrite,
   readv/writev, dup2 limits, ftruncate, uname, gettimeofday, epoll flags.
2. Procfs prerequisite `pid_max`, then rerun process tests to reach assertions.
3. Missing legacy `clock_nanosleep` behind glibc nanosleep; clock_getres variant consistency.
4. Investigate wait401 with syscall trace and bounded reproduction.
5. Decide whether Linux packet-mode pipe semantics are in scope; keep their
   absence visible until implemented or explicitly classified by future policy.

Native-versus-Podish discrepancies are diagnostic leads. Before changing
runtime semantics, add a minimal reproducer and confirm the particular ABI and
backend involved; do not simply change the expected result in the LTP source.
