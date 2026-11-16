# execve hardening review round

Date: 2026-11-16
Baseline: Windows Podish, tmpfs guest root, BusyBox image
Reference: WSL Linux 5.15.153.1, 32-bit static i386 probes

This round started from the `pipe2_02` SIGSEGV fix. The GPR reset was correct,
but deeper comparison with real Linux exposed failure-atomicity, auxv, segment,
thread-group and file-state gaps. The replacement image is now built in a
private address space and committed only after a fully successful load.

## WSL comparison

| Probe | WSL Linux | Podish before | Podish after |
| --- | --- | --- | --- |
| exec entry GPRs | `EAX..EBP=0`, ESP 16-byte aligned | same | same |
| exec entry EFLAGS | `0x202` | stale SF/PF/CF from parent | `0x202` |
| exec GS base | cleared; `%gs:0` faults | old base survived | cleared |
| auxv IDs as uid 1000 | 1000/1000/1000/1000 | 0/0/0/0 | 1000/1000/1000/1000 |
| setuid auxv | new EUID, `AT_SECURE=1` | old EUID, 0 | new EUID, 1 |
| shebang `AT_EXECFN` | script path | interpreter path | script path |
| invalid argv/env pointer | `EFAULT` | silently truncated | `EFAULT` |
| 200 KiB environment | `E2BIG` | silently dropped | `E2BIG` |
| malformed ELF | `ENOEXEC`, process continues | ENOENT + SIGSEGV | `ENOEXEC`, image intact |
| failed exec side effects | fd/handlers/altstack preserved | closed/reset early | preserved |
| FIFO as executable | `EACCES` | `ENOEXEC` | `EACCES` |
| file open for write | `ETXTBSY` | exec succeeded | `ETXTBSY` |
| `comm` with `argv[0]=foo` | executable basename | `foo` | executable basename |
| `comm` for a shebang | script basename | interpreter basename | script basename |
| long shebang script path | intact `AT_EXECFN`/argv | stack overlap, corrupted `AT_EXECFN` | intact |
| robust futex held at exec | owner-died + wake | list dropped without cleanup | owner-died + wake |
| multithreaded exec | siblings killed | sibling `#UD`/fault | siblings terminated with per-task cleanup |
| `sendmsg` bad msghdr + `MSG_OOB` | `EFAULT` | `EOPNOTSUPP` | `EFAULT` |
| `sendmsg` null control, len 16 | `EINVAL` | success | `EINVAL` |
| named FIFO `FIONREAD` | bytes buffered | `ENOTTY` | still `ENOTTY` (known) |

## Regression coverage

`Fiberish.Tests/Syscalls/ExecSemanticsRegressionTests.cs` now covers malformed
ELF rollback, failed-exec side effects, invalid/oversized vectors, real and
set-uid auxv, shebang `AT_EXECFN` (including a long script path), shebang
`comm`, FS/GS reset, executable-derived `comm`, successful-exec signal/altstack
reset, FIFO/noexec/ETXTBSY rejection, sibling-thread teardown, argv error
precedence, and guest-visible EFLAGS at entry (including a parent `stc` that
previously leaked CF).

`SocketSyscallTests` covers `MSG_OOB` error precedence, null control with a
nonzero length, unreadable control data and invalid `SCM_RIGHTS` fds.
`PipeTests` covers soft-limit capacity shrinking, release on close, hard-limit
`ENFILE`, and no wait-queue leak on a rejected creation.

## Verification

- `Fiberish.Tests`: 2065 PASS / 0 FAIL / 1 SKIP (this follow-up round).
- LTP basic hostfs re-run: 50 PASS. Pipe/FIFO batch re-run: 18 PASS.
  Focused proc-time re-run: `execve03` (6 assertions) and `wait402` PASS.
- Other LTP batch numbers below were recorded before the follow-up fixes and
  were not re-run; the affected exec/pipe paths are covered by the unit and
  focused LTP regressions above.
- LTP permissions: 71 PASS.
- LTP net-io: 64 PASS / 5 PARTIAL / 0 FAIL / 3 BROKEN / 1 SKIP; proc-time:
  90 PASS / 2 PARTIAL / 1 FAIL / 3 BROKEN / 2 SKIP; fs-fd: 106 PASS / 1 PARTIAL
  / 8 SKIP; memory/sync/signals match their existing baselines.
- Guest probes: malformed exec returns `ENOEXEC` and continues; entry flags are
  `0x202`; `mt_exec` exits 0 with siblings gone; invalid vectors return
  `EFAULT`; oversized env returns `E2BIG`; `comm=showargs`; ETXTBSY/FIFO/noexec
  match WSL; `sendmsg` error order matches WSL.

## Performance / allocation follow-up

- Exec argument reads start at a 256-byte probe and grow only for long strings,
  instead of renting `MAX_ARG_STRLEN` (128 KiB) for every argv/env entry.
- The staged syscall path takes ownership of the freshly read argv arrays; the
  public `LoadExecutable` overload still copies caller-owned inputs.
- `LoaderResult.InitialStack` is an `ArraySegment<byte>` over the loader buffer,
  removing one full copy of the initial stack before it is written to guest
  memory.
- Expensive log arguments (argv decoding, `AT_RANDOM` hex dump, file names) are
  now guarded by `ILogger.IsEnabled`.
- `DetachSiblingThreads` avoids a list allocation for single-threaded exec and
  the staged register snapshot no longer allocates an array.

## Remaining known gaps

- Named FIFOs on tmpfs remain page-cache placeholders; `dup05`/`fsync03` only
  exercise descriptor checks. See `PIPE_BASELINE.md`.
- i386 segment selectors (DS/ES/SS) are not modeled; only FS/GS bases are reset.
- `AT_SYSINFO`/`AT_SYSINFO_EHDR` are not provided by the current minimal vDSO.
- argv and envp vector budgets are enforced independently; a combined vector
  slightly above `RLIMIT_STACK/4` can still pass.
- `CLONE_FILES` is unshared on successful exec; `CLONE_FS` isolation is not.
- `PT_INTERP` is opened without the noexec/DAC/ETXTBSY checks applied to the
  main executable and shebang interpreter; only the ELF loader path is covered.
- Writable `MAP_SHARED` mappings do not count towards `ETXTBSY`; only writable
  open file descriptions do.
- `MS_NOSUID` and the `S_ISGID | S_IXGRP` rule are not applied when computing
  exec credentials, and file capabilities are not modeled.
- The commit phase (thread teardown / SysV detach) runs after a successful
  staged load. A scheduler invariant failure there would leave the old image in
  place but with updated process-image metadata; normal scheduler paths do not
  hit it.
- `ElfLoader` still logs `Failed to zero-fill BSS tail` for some read-only BSS
  tails; this predates the exec round and does not affect the selected tests.
