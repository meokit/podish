# LTP pipe and FIFO batch — selection and baseline

Date: 2026-11-15
Manifest: `tests/ltp/cases-pipe.json` (18 cases)
Backend: Windows Podish, tmpfs image, guest root, BusyBox tools
Native reference: WSL Linux 5.15.153.1, root, i386 static executables
LTP: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`
Scope: selection and first baseline only; no runtime fixes are included.

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
| Windows Podish, tmpfs, BusyBox | 14 | 1 | 3 | 0 | 0 | 0 |

Assertion totals: native 103 passed; Podish 93 passed / 3 failed / 3 broken.
The batch is largely green already, which makes the four remaining findings
high-signal rather than broad missing implementation.

## Remaining non-PASS after the baseline

| Case | Status | Observation |
| --- | --- | --- |
| `pipe12` | BROKEN | `ioctl(fd, FIONREAD, ...)` on a pipe returns `ENOTTY`; Linux reports the number of bytes available. The nonblocking-full and `PIPE_BUF` write assertions pass before this. |
| `pipe15` | BROKEN | `/proc/sys/fs/pipe-user-pages-soft` is missing (`ENOENT`), so the test cannot compute the per-user pipe budget. |
| `pipe2_02` | BROKEN | The BusyBox `cp` resource copy now succeeds, but the forked child `exec`s `pipe2_02_child` and that child faults at its first stack access after `fcntl(F_GETFL)` (`SIGSEGV`). Directly executing the same static binary succeeds; the failure is in Podish's fork/exec path, not in the pipe assertion. |
| `fsync03` | FAIL | `fsync()` on a pipe, a Unix socket and a FIFO returns 0; Linux returns `EINVAL` for all three. The closed/invalid-descriptor variants pass. |

## Other modern IPC and shared-memory gaps

These are used more heavily than SysV message queues but are not part of this
batch, either because LTP has no suitable syscall case or because the runner
needs more support first.

| Area | Current coverage | Gap / reason not selected |
| --- | --- | --- |
| POSIX shared memory (`shm_open`, `shm_unlink`, `sem_open`, `MAP_SHARED`) | No LTP syscall batch. Internal `Fiberish.Tests` exercises `MAP_SHARED` and `/dev/shm` VFS behavior. | This is the largest shared-memory gap. The upstream `open_posix_testsuite` has `shm_open`/`shm_unlink`/`mmap` cases, but the runner only builds `tst_test` binaries under `testcases/kernel/syscalls` and only classifies LTP summaries. Supporting that suite is a separate harness change. |
| Unix domain sockets and `SCM_RIGHTS` | Net-io covers `bind04/05`, `connect03`, `recvmsg01`, `socketpair02`; `sendmsg01` is BROKEN because it shells out to `ip`/`ifconfig`. | No dedicated AF_UNIX stream/seqpacket data-path batch; abstract namespace and fd passing are only indirectly covered. |
| POSIX message queues (`mq_*`) | 7 LTP cases available, none selected. | The runner image has no `mqueue` filesystem, so the cases cannot produce a meaningful baseline yet. Usage is lower than pipes and Unix sockets. |
| `memfd_create` and seals | 4 cases in the memory batch: `memfd_create01/02` PASS, `03/04` SKIP. | Already covered; seals are exercised by `memfd_create01`. |
| `io_uring` / legacy AIO | 4 `io_uring` syscall cases available, none selected. | Increasingly used by modern servers, but the implementation surface is large and is not a bounded compatibility batch. |
| `process_vm_readv`/`writev` | 4 cases under `cma`, none selected. | Used by debuggers/CRIU rather than ordinary userland IPC. |

## Proposed next work

1. Implement `FIONREAD` for pipes and FIFOs (available-byte count, `ENOTTY`
   for unrelated descriptor types).
2. Expose `/proc/sys/fs/pipe-user-pages-soft` and enforce the per-user pipe
   page budget so `pipe15` can run; add the matching hard limit if the test
   needs it later.
3. Reject `fsync`/`fdatasync` on pipes, Unix sockets and FIFOs with `EINVAL`;
   keep `EBADF` for invalid descriptors.
4. Fix the fork/exec crash seen by `pipe2_02_child`: direct exec works, but a
   child forked from an LTP test faults on its first stack read after
   `fcntl(F_GETFL)`.
5. Re-run this batch and the basic `pipe01`/`pipe2_01` cases. Then decide
   whether to add a POSIX shared-memory batch by extending the runner for the
   `open_posix_testsuite` format.

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
