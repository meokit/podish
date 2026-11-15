# LTP network sockets and I/O multiplexing batch — baseline and fixes

> BusyBox image update (2026-11-15): `sendmsg01` now runs (BusyBox
> `ip`/`ifconfig`) and is **FAIL**: invalid `MSG_*` flags return success instead
> of `EOPNOTSUPP`, and an invalid control-message pointer returns `EINVAL`
> instead of `EFAULT`. The batch is now 63 PASS / 5 PARTIAL / 1 FAIL / 3 BROKEN
> / 1 SKIP. See [BUSYBOX_IMAGE.md](BUSYBOX_IMAGE.md).

> Final review/integration checkpoint: see [NET_IO_MERGE_VERIFICATION.md](NET_IO_MERGE_VERIFICATION.md)
> for the four reproduced semantic fixes, reconciliation with main's fs-fd work, and fresh guest regression
> results. Historical checkpoint counts below do not replace that final record.

Date: 2026-11-14
Manifest: `tests/ltp/cases-net-io.json` (73 cases)
Backend: Windows Podish, tmpfs image, guest root, `--network private`
Native reference: WSL Linux 5.15.153.1, root, i386 static executables
LTP: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`
Branch: `fix/ltp-net-io` (baseline, first repair round, timer and readiness round)

> Review update (2026-11-15): the 62-PASS row below is the historical `5bff69f`
> result; the new full run is recorded in the next row. Whole-branch review
> found successful no-op socket options and incomplete readiness/namespace
> semantics; see `NET_IO_BRANCH_REVIEW.md`. Passing option-validation tests does
> not establish that those options affect the data plane. No upstream assertions
> have been changed.

## Results

| Backend | PASS | PARTIAL | FAIL | BROKEN | TIMEOUT | SKIP |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Native Linux | 73 | 0 | 0 | 0 | 0 | 0 |
| Windows Podish, tmpfs (before) | 25 | 1 | 25 | 18 | 4 | 0 |
| Windows Podish, tmpfs (first round) | 52 | 4 | 4 | 5 | 7 | 1 |
| Windows Podish, tmpfs (`5bff69f`, historical) | 62 | 5 | 0 | 5 | 0 | 1 |
| Windows Podish, tmpfs (2026-11-15, uncommitted review checkpoint) | 63 | 5 | 0 | 4 | 0 | 1 |
| Windows Podish, tmpfs (2026-11-15, P1 lifecycle/cancellation follow-up) | 63 | 5 | 0 | 4 | 0 | 1 |
| Windows Podish, tmpfs (2026-11-15, socket option semantics) | 63 | 5 | 0 | 4 | 0 | 1 |

The new 73-case run used the current-worktree CLI, unchanged upstream executables
and a 40-second per-case timeout. `epoll_wait10` moved from BROKEN to PASS;
`epoll_wait16`, `setsockopt05`, `setsockopt08`, and `sendmsg01` remain BROKEN.
Report: `artifacts/ltp-net-io/results/podish-tmpfs.json`, source revision
`5bff69f9a2433b8c0fa387ccdca69edaaee0a1d8` + dirty changes digest
`46507a6f8a86a35ab30df84f5e544a94e5265a0089fd344771c77255dfbc86d7`.
Later documentation/probe-only edits may change that digest without changing the
tested runtime. The runner's nonzero exit reflects PARTIAL/BROKEN/SKIP, not FAIL.

Checkpoint regression gates: `Fiberish.Tests` 1882 PASS / 0 FAIL / 1 SKIP;
SilkFS/Pulse/Wayland 13/49/19 PASS; LTP runner 22/22 PASS; Node matrix 24/24 PASS
across tmpfs/silkfs (each: `6 3000 32768` ×10, `12 3000 32768` ×1,
`12 200000 64` ×1), all with actual exit 0 plus completion marker. Node summary:
`artifacts/verification/node-matrix.json`. Harness-only exit/marker/timeout/path
tests also pass 6/6; they are not counted as guest Node runs.

P1 follow-up: cancellable foreign-dispatcher deliveries/trampoline and owner-routed
epoll close cleanup add nine regressions. Latest C# core is **1891 PASS / 0 FAIL /
1 SKIP**, other suites remain 13/49/19 PASS, runner 22/22. All 73 net-io case
statuses are unchanged; Node was rerun and again passed **24/24** with genuine
exit 0 plus marker (`artifacts/verification/node-matrix-p1.json`). Latest LTP JSON
source digest is `e68c2a29680fa18ca106c6965df95f6811b90a10d5418ad7db7d9ff4199b876e`;
the preceding JSON is retained at `artifacts/verification/net-io-pre-p1.json`.
These fixes do not enable EPOLLEXCLUSIVE or finish the remaining source/namespace
implementation. See the review report for unresolved scope.

Socket-option round: netstack buffers/Nagle/keepalive/linger/multicast now act on the
backend, and unsupported knobs return real errno (matrix: `NET_IO_SOCKET_OPTION_MATRIX.md`).
Native `probes/sockopts.c` confirms Linux doubling/clamp values and the options that Linux
accepts but this backend cannot fully implement. Full net-io statuses are unchanged
(63/5/0/4/0/1), and Node passed 24/24 again (`node-matrix-sockopts.json`). The first guest
run exposed an optlen-ordering regression in `setsockopt01`; it was fixed (EINVAL before
ENOPROTOOPT) and the case is PASS. Latest LTP digest:
`7fe518f46ef3ad69877ac5d504ddf6d2d2f251a9473ab9b47fe44ff054ce9451`.
Final gates for this round: `Fiberish.Tests` **1900 PASS / 0 FAIL / 1 SKIP**; SilkFS 13,
Pulse 49, Wayland 19 PASS; runner 22/22; Node 24/24 (`node-matrix-sockopts.json`).

`PARTIAL`/`SKIP` mean the case ran with a supported subset (unsupported
`SCTP`/`UDP-Lite` protocol or unavailable fd types), not a fabricated pass.
In that historical run no case failed or timed out; the remaining `BROKEN` cases need kernel
features or guest tooling that this batch does not provide (see below).

## Excluded before the comparison

- `setsockopt10`: `TCONF` on native WSL2 (TLS kernel module unavailable).
- `recvmsg03`: `TCONF` on native WSL2.
- `select02`: exceeds the 30s default host timeout on native WSL2 (36.09s).
- Cases requiring `SOCK_RAW`, `AF_PACKET`, `AF_INET6`, or `NETLINK` are not in this batch.

## Fixes made

### 1. Socket constants and option handling
- `LinuxConstants` used wrong `SOL_SOCKET` numbers: `SO_SNDBUF` was 8 (really
  `SO_RCVBUF`), `SO_RCVBUF` 9 (really `SO_KEEPALIVE`), `SO_KEEPALIVE` 6 (really
  `SO_BROADCAST`) and `SO_OOBINLINE` 7 (really `SO_SNDBUF`). Corrected to
  7/8/9/10; this alone fixed `setsockopt01` and several option tests.
- The first repair round added validation/acceptance for `SO_REUSEADDR`, `SO_REUSEPORT`,
  `SO_KEEPALIVE`, `SO_OOBINLINE`, `SO_SNDBUF`, `SO_RCVBUF`, `SO_LINGER`,
  `SO_RCVTIMEO`/`SO_SNDTIMEO`, `TCP_NODELAY`/keepalive knobs, and rejects
  `IPPROTO_UDP`/unknown levels with `ENOPROTOOPT` or `EOPNOTSUPP`.
  **Review caveat:** several accepted options are still no-ops with fixed getter
  defaults. This is an unresolved ABI defect, not completed option support.
- `SO_SNDBUFFORCE`/`SO_RCVBUFFORCE` accept the value only for callers with
  `CAP_NET_ADMIN` (root model), otherwise `EPERM` (CVE-2016-9793 shape).
- `MCAST_JOIN_GROUP`/`MCAST_LEAVE_GROUP` track group metadata per socket so a
  group is not inherited by an accepted socket (`accept02`, CVE-2017-8890).
  This does not implement actual multicast reception membership.

### 2. Address parsing, endpoint copy-out and descriptor limits
- `ReadSockaddr`/`ReadAnySockaddr` now return the proper errno (`EFAULT` for a
  missing pointer where required, `EINVAL` for a short/negative length,
  `EAFNOSUPPORT` for an unknown family) instead of `EINVAL` for everything.
- `WriteSockaddr*` return errors and validate the user `socklen_t`
  (`EFAULT`/`EINVAL`), including Unix and netlink families; `getsockname`/
  `getpeername` propagate them (`getsockname01`, `getpeername01`).
- `listen(0)`/unbound `listen()` allocate an ephemeral port; privileged
  `bind(<1024)` without `CAP_NET_BIND_SERVICE` fails with `EACCES` (`bind01`,
  `bind02`, `bind04`).
- `AllocFD`/`DupFD` honour `RLIMIT_NOFILE` (`EMFILE`/`EINVAL`), so
  `epoll_create03` and `fcntl12` behave like Linux; `pipe`/`pipe2`/
  `socketpair` roll back partially allocated descriptors.
- `accept`/`accept4` return `EBADF` for `O_PATH` and `open_tree(2)` descriptors
  instead of `ENOTSOCK` (`accept03`).

### 3. Datagram and stream send/receive semantics
- Oversized UDP datagrams (> 65507 bytes) now fail with `EMSGSIZE` instead of
  `EAGAIN` (`send01`, `sendto01`).
- `send`/`sendto` reject `MSG_OOB` with `EOPNOTSUPP`; `recv`/`recvfrom`/
  `recvmsg` reject `MSG_OOB` with `EINVAL` and `MSG_ERRQUEUE` with `EAGAIN`.
- `sendto` on a connected stream socket ignores the destination address, as
  Linux does, and an unconnected stream send returns `EPIPE`.
- `recvfrom`/`recvmsg` validate the address length (`EINVAL`) and `msg_iovlen`
  (`EMSGSIZE` beyond `UIO_MAXIOV`).
- `shutdown` validates `how`, returns `ENOTCONN` for unconnected sockets, and
  `MSG_NOSIGNAL` now suppresses `SIGPIPE` for Unix sockets.
- UDP `MSG_MORE` corks the payload into a pending datagram and flushes it on the
  next send without `MSG_MORE`, while `send()` still reports only the caller's
  byte count (`send02`).

### 4. Connect and loopback routing
- `connect()` to `0.0.0.0` is treated as loopback (Linux behaviour).
- The private netstack reports `ECONNREFUSED` immediately when no listener owns
  the target port, instead of retransmitting SYNs until the host test timeout.
- A connect that completes and is then reset by the peer is no longer reported
  as `ECONNREFUSED`; established-then-closed is a successful connect.
- Every post-handshake state (`ESTABLISHED`, `FIN_WAIT*`, `CLOSE_WAIT`,
  `CLOSING`, `LAST_ACK`, `TIME_WAIT`) now counts as a completed connect, and a
  second `connect()` on such a socket returns `EISCONN` (`connect01`,
  `accept02`).
- `Drive()` now advances the netstack until no deadline is due, so a loopback
  handshake finishes inside the syscall instead of sleeping for the TCP RTO
  (`send02` ran 1000 iterations per protocol after this).

### 5. `epoll` correctness
- `epoll_ctl` rejects descriptors that cannot be polled with `EPERM`
  (directory, regular file, `/dev/zero`, procfs) and returns `EBADF` for
  `O_PATH`/`open_tree` descriptors (`epoll_ctl02`, `epoll_ctl06`).
- Nested epoll sets are limited to 5 levels and cycles are rejected with
  `ELOOP` (`epoll_ctl04`, `epoll_ctl05`, `epoll_ctl07`).
- `EPOLLRDHUP` is reported when the read side is shut down or the peer closed,
  and the netstack no longer advertises `POLLOUT` while its TX buffer is full
  (`epoll_wait05`).
- `epoll_wait` reports at most `maxevents` and keeps the remaining ready
  descriptors for later calls (`epoll_wait13`).
- `epoll_wait`/`poll`/`select` reject `nfds` above `RLIMIT_NOFILE` and negative
  `select` timeouts with `EINVAL` (`poll03`, `select03`, `ppoll01`).
- `select`/`pselect` return `EBADF` when a set names a closed descriptor before
  blocking, which fixes the forked `pselect02`/`select03` hangs.
- `F_SETPIPE_SZ`/`F_GETPIPE_SZ` resize the pipe capacity, and pipe writability
  is page-buffer granular like Linux, so `epoll_wait06`'s edge-triggered pipe
  reports `EPOLLOUT` only once the pipe is fully drained.
- Edge-triggered watches are sampled at `epoll_wait` entry (rising edge against
  the last observed mask) so a readiness notification still queued in the
  scheduler cannot report an empty set.
- A stale ready entry no longer consumes one of the caller's `maxevents` slots.

### 6. Millisecond timer precision on Windows
`tst_timer_test` measures `epoll_wait`/`poll`/`pselect` timeouts and compares the
truncated mean against the requested interval. The batch failed because a 1 ms
timeout took 2–15 ms. Two root causes were measured on the host:

| Wait primitive | 1 ms request (median) | 5 ms request (median) |
| --- | ---: | ---: |
| `WaitHandle.WaitOne`, no `timeBeginPeriod` | 15.05 ms | 15.04 ms |
| `WaitHandle.WaitOne` with `timeBeginPeriod(1)` | 15.04 ms | 15.02 ms |
| `CreateWaitableTimerEx(HIGH_RESOLUTION)` | 1.02 ms | 5.50 ms |
| `Stopwatch` spin | 1.002 ms | 5.002 ms |

`timeBeginPeriod(1)` reports success (`NtQueryTimerResolution` = 1.000 ms) but
Win32 waitable objects are still coalesced to the ~15.6 ms system tick, so the
old `_wakeEvent.Wait(timeoutMs)` could not express a millisecond sleep.

- New `Fiberish.Core/HighResolutionWait.cs` wraps
  `CreateWaitableTimerExW(CREATE_WAITABLE_TIMER_HIGH_RESOLUTION)` and waits on
  `[scheduler wake event, timer]` with `WaitForMultipleObjects`, falling back to
  a plain `WaitOne` when the timer cannot be created.
- `KernelScheduler` replaced the `ManualResetEventSlim` idle wait with a gated
  `AutoResetEvent` plus the high-resolution timer. `EnqueueWake()` publishes the
  work item before reading the waiter flag, and the waiter re-checks the
  channels after publishing the flag, so no wake is lost.
- Timer placement now maps the request onto the tick axis at its ceiling
  (`ceil(elapsed + delay) - 1`) so a timer never fires before the requested
  delay, and the idle wait targets the exact wall-clock instant at which the
  next tick becomes current. This removed the extra whole millisecond that
  `Advance()` used to add, and a zero-delay timer scheduled from inside
  `Advance()` no longer lands in an already-drained slot (256-tick delay).

Measured after the change: 1 ms timeout ≈ 2.0 ms median (the remaining 1 ms is
the wheel's 1 ms tick and is inside the LTP threshold), 100 ms ≈ 100.9 ms, and
the 1 s case ≈ 1000.7 ms. `epoll_wait02`, `epoll_pwait03`, `poll02`,
`pselect01` and `pselect02` now pass.

## Remaining cases

| Case | Status | Why it is not a pass |
| --- | --- | --- |
| `epoll_wait16` | BROKEN | `EPOLLEXCLUSIVE` is still rejected with `EINVAL`: one-event-wakes-one-waiter needs a per-target exclusive wakeup arbiter shared by all epoll instances. Needs its own change with the epoll wakeup contract. |
| `epoll_wait10` | BROKEN | Needs `/proc/mounts` to emit `POLLPRI` on mount-namespace changes; procfs has no poll notifications yet. |
| `setsockopt05`, `setsockopt08` | BROKEN | The tests call `unshare(CLONE_NEWUSER\|CLONE_NEWNET)` in `tst_setup_netns`; `unshare` returns `ENOSYS`. Namespace support, not a socket defect. |
| `sendmsg01` | BROKEN | The legacy test shells out to `ip`/`ifconfig` to bring up `lo`; the guest image has no such tools. |
| `bind04`, `bind05`, `sendto02` | PARTIAL/SKIP | `SCTP`/`UDP-Lite` protocols are not implemented; native Linux runs them. |
| `accept03`, `epoll_ctl06`, `epoll_pwait06` | PARTIAL | Remaining variants need `pidfd`, `fanotify`, `userfaultfd`, `io_uring`, `bpf`, `fsopen`/`fspick` fd types. |

## Verification

- Podish net-io batch: **62 PASS / 5 PARTIAL / 0 FAIL / 5 BROKEN / 0 TIMEOUT / 1 SKIP** (from 25 PASS).
- `Fiberish.Tests`: **1860 passed, 0 failed, 1 skipped**.
- `tests/ltp/test_runner.py`: **20 tests passed**.
