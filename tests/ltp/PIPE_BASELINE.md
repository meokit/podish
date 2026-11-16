# LTP pipe and FIFO batch — selection and baseline

Date: 2026-11-15
Manifest: `tests/ltp/cases-pipe.json` (18 cases)
Backend: Windows Podish, tmpfs image, guest root, BusyBox tools
Native reference: WSL Linux 5.15.153.1, root, i386 static executables
LTP: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`
Scope: selection, baseline, and bounded runtime fixes; see “Fixed in this round”.

## Why this batch

Pipes are the most widely used IPC mechanism in the software Podish targets:
shells, command substitution, subprocesses, Git, Python multiprocessing and
most build tools depend on them. The basic batch only covered `pipe01` and
`pipe2_01`; the remaining pipe and FIFO semantics had no LTP coverage.

This batch adds the practical failure modes that userland actually depends on:

- `SIGPIPE`/`EPIPE` and broken-pipe status propagation;
- nonblocking writes when the pipe is full (`EAGAIN`), atomic `PIPE_BUF`
  behavior, and reader wakeup when the last writer closes;
- `EMFILE`/`ENFILE` descriptor exhaustion and `RLIMIT_NOFILE` interaction;
- `FIONREAD` on a pipe, `F_GETPIPE_SZ`/`F_SETPIPE_SZ`, and EOF after close;
- `pipe2(O_CLOEXEC)` propagation across `exec`;
- FIFO descriptor operations (`dup` on a FIFO, `fsync` rejecting a FIFO).

`pipe2_02` needs the separate `pipe2_02_child` executable. LTP copies it with
`system("cp ...")`, which now works because the guest image has BusyBox
`/bin/sh` and `/bin/cp`; the resource binary itself is declared in the manifest
and included by the runner. `pipe01` and `pipe2_01` remain in the basic batch
and were not duplicated here.

## Results

| Backend | PASS | FAIL | BROKEN | SKIP | TIMEOUT | PARTIAL |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Native Linux | 18 | 0 | 0 | 0 | 0 | 0 |
| Windows Podish, tmpfs, BusyBox | 18 | 0 | 0 | 0 | 0 | 0 |

Assertion totals: native 103 passed; Podish 103 passed.

## Other modern IPC and shared-memory gaps

These are used more heavily than SysV message queues but are not part of this
batch, either because LTP has no suitable syscall case or because the runner
needs more support first.

| Area | Current coverage | Gap / reason not selected |
| --- | --- | --- |
| POSIX shared memory (`shm_open`, `shm_unlink`, `sem_open`, `MAP_SHARED`) | No LTP syscall batch. Internal `Fiberish.Tests` exercises `MAP_SHARED` and `/dev/shm` VFS behavior. | This is the largest shared-memory gap. The upstream `open_posix_testsuite` has `shm_open`/`shm_unlink`/`mmap` cases, but the runner only builds `tst_test` binaries under `testcases/kernel/syscalls` and only classifies LTP summaries. Supporting that suite is a separate harness change. |
| Unix domain sockets and `SCM_RIGHTS` | Net-io covers `bind04/05`, `connect03`, `recvmsg01`, `socketpair02`; `sendmsg01` now passes with the BusyBox tools. | No dedicated AF_UNIX stream/seqpacket data-path batch; abstract namespace and fd passing are only indirectly covered. |
| POSIX message queues (`mq_*`) | 7 LTP cases available, none selected. | The runner image has no `mqueue` filesystem, so the cases cannot produce a meaningful baseline yet. Usage is lower than pipes and Unix sockets. |
| `memfd_create` and seals | 4 cases in the memory batch: `memfd_create01/02` PASS, `03/04` SKIP. | Already covered; seals are exercised by `memfd_create01`. |
| `io_uring` / legacy AIO | 4 `io_uring` syscall cases available, none selected. | Increasingly used by modern servers, but the implementation surface is large and is not a bounded compatibility batch. |
| `process_vm_readv`/`writev` | 4 cases under `cma`, none selected. | Used by debuggers/CRIU rather than ordinary userland IPC. |

## Fixed in this round

- `pipe12`: `FIONREAD` now reports the bytes available on anonymous pipe
  descriptors, and a nonblocking large write fills
  the remaining capacity instead of stopping at a guest-page boundary.
- `pipe15`: `/proc/sys/fs/pipe-user-pages-soft` and `-hard` are exposed with
  Linux defaults; anonymous pipes now account capacity per real UID. The soft
  limit caps new pipes at one page, and the hard limit fails creation with
  `ENFILE`.
- `fsync03`: `fsync`/`fdatasync` on a pipe, Unix socket or FIFO now return
  `EINVAL`.
- `pipe2_02` and the exec hardening round: replacement images are built in a
  private address space and committed only after every mapping, stack page and
  auxv entry succeeds. `execve` now clears GPRs, EFLAGS, FS/GS bases and
  `clear_child_tid`, terminates sibling threads, reports `ENOEXEC`/`EFAULT`/
  `E2BIG`/`ETXTBSY`/`EACCES` correctly, preserves failure side effects, uses
  the script path for `AT_EXECFN`, reports real auxv credentials and derives
  `comm` from the executable name.

## Remaining work

1. At the time of this pipe baseline, named FIFOs on tmpfs were descriptor-compatible
   placeholders backed by the page cache, not real pipe objects. `dup05` and
   `fsync03` only exercise descriptor checks, not the FIFO data path.
   [FIFO_BASELINE.md](FIFO_BASELINE.md) and [FIFO_FIX_REPORT.md](FIFO_FIX_REPORT.md)
   record the subsequent transport repair; [FIFO_TEST_PLAN.md](FIFO_TEST_PLAN.md)
   defines the separate LTP-first
   selection, missing differential probes and lifecycle-safe repair sequence;
   it does not change the recorded results above.
2. Decide whether to add a POSIX shared-memory batch by extending the runner for
   the `open_posix_testsuite` format. The pipe/FIFO descriptor batch is green.

## Verification

```powershell
python tests/ltp/build_busybox_image.py
wsl -d Ubuntu -- bash /mnt/c/Users/Jiang/repos/podish/tests/ltp/build.sh \
  /mnt/c/Users/Jiang/repos/podish/artifacts/ltp-pipe \
  /mnt/c/Users/Jiang/repos/podish/tests/ltp/cases-pipe.json
wsl -d Ubuntu -u root -- python3 /mnt/c/Users/Jiang/repos/podish/tests/ltp/run.py \
  --backend native --artifacts /mnt/c/Users/Jiang/repos/podish/artifacts/ltp-pipe \
  --manifest /mnt/c/Users/Jiang/repos/podish/tests/ltp/cases-pipe.json --timeout 60
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-pipe \
  --manifest tests/ltp/cases-pipe.json --timeout 60
```

Reports: `artifacts/ltp-pipe/results/native.json` and
`artifacts/ltp-pipe/results/podish-tmpfs.json`.
