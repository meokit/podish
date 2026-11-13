# LTP network sockets and I/O multiplexing batch — baseline and fixes

Date: 2026-11-13
Manifest: `tests/ltp/cases-net-io.json` (73 cases)
Backend: Windows Podish, tmpfs image, guest root, `--network private`
Native reference: WSL Linux 5.15.153.1, root, i386 static executables
LTP: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`
Branch: `fix/ltp-net-io` (baseline plus first repair round)

## Results

| Backend | PASS | PARTIAL | FAIL | BROKEN | TIMEOUT | SKIP |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Native Linux | 73 | 0 | 0 | 0 | 0 | 0 |
| Windows Podish, tmpfs (before) | 25 | 1 | 25 | 18 | 4 | 0 |
| **Windows Podish, tmpfs (after first round)** | **52** | **4** | **4** | **5** | **7** | **1** |

`PARTIAL`/`SKIP` mean the case ran with a supported subset (unsupported
`SCTP`/`UDP-Lite` protocol or unavailable fd types), not a fabricated pass.

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
- `NetstackSocketInode` now validates/serves `SO_REUSEADDR`, `SO_REUSEPORT`,
  `SO_KEEPALIVE`, `SO_OOBINLINE`, `SO_SNDBUF`, `SO_RCVBUF`, `SO_LINGER`,
  `SO_RCVTIMEO`/`SO_SNDTIMEO`, `TCP_NODELAY`/keepalive knobs, and rejects
  `IPPROTO_UDP`/unknown levels with `ENOPROTOOPT` or `EOPNOTSUPP` as Linux does.
- `SO_SNDBUFFORCE`/`SO_RCVBUFFORCE` accept the value only for callers with
  `CAP_NET_ADMIN` (root model), otherwise `EPERM` (CVE-2016-9793 shape).
- `MCAST_JOIN_GROUP`/`MCAST_LEAVE_GROUP` track joined groups per socket so a
  group is not inherited by an accepted socket (`accept02`, CVE-2017-8890).

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
- `accept4` returns `EBADF` for `O_PATH` descriptors instead of `ENOTSOCK`.

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

### 4. Connect and loopback routing
- `connect()` to `0.0.0.0` is treated as loopback (Linux behaviour).
- The private netstack reports `ECONNREFUSED` immediately when no listener owns
  the target port, instead of retransmitting SYNs until the host test timeout.
- A connect that completes and is then reset by the peer is no longer reported
  as `ECONNREFUSED`; established-then-closed is a successful connect.

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
  `select` timeouts with `EINVAL` (`poll03`, `select03` first case, `ppoll01`).
- `F_SETPIPE_SZ`/`F_GETPIPE_SZ` resize the pipe capacity, so
  `epoll_wait06`'s edge-triggered pipe test can fill and drain deterministically.
- `epoll_wait` no longer lets a stale ready entry consume the only harvest slot.

## Remaining failures and analysis (next round)

| Case | Status | Root cause / next step |
| --- | --- | --- |
| `accept02` | TIMEOUT | Multicast assertions pass; the client thread's `connect()` remains pending after the server accepted and closed. Needs netstack state tracing for "established then peer close" on the client side. |
| `connect01` | TIMEOUT | Setup `connect()` to `0.0.0.0` is still pending; handshake does not complete for a listener bound to `INADDR_ANY` when the client also binds `INADDR_ANY`. |
| `send02` | TIMEOUT | `MSG_MORE` is treated as an immediate flush; the second iteration's `connect()` hangs after the first stream is closed. Needs `MSG_MORE` buffering plus listener-replacement audit. |
| `select03` | TIMEOUT | After the negative-`nfds` pass, the second forked case hangs; `select` with a closed fd in the set does not return `EBADF` in the child. |
| `epoll_wait02`, `epoll_pwait03`, `poll02`, `pselect01`, `pselect02` | FAIL/TIMEOUT | `tst_timer_test` measures 1004–1014 ms for a 1000 ms timeout, above the LTP upper bound. Needs sub-millisecond timer accuracy in `SleepAwaitable`/`TimeWheel` (Windows 15.6 ms tick granularity is the likely floor). |
| `epoll_wait06` | FAIL | Edge-triggered pipe: the write end is reported at the wrong point; needs `EPOLLET` re-check bookkeeping that keeps the existing `NetworkTests` rearm contract. |
| `epoll_wait10` | BROKEN | Requires `/proc/mounts` `POLLPRI` notifications on mount namespace changes; procfs does not emit poll events yet. |
| `epoll_wait16` | BROKEN | `EPOLLEXCLUSIVE` is rejected with `EINVAL`; needs cross-epoll exclusive wakeup policy. |
| `setsockopt05`, `setsockopt08` | BROKEN | Both use `unshare(CLONE_NEWUSER|...)` in `tst_setup_netns`; `unshare` returns `ENOSYS`. Environment/namespace support, not a socket bug. |
| `sendmsg01` | BROKEN | Legacy LTP test shells out to `ip`/`ifconfig` to bring up `lo`; the guest has no such tools. |
| `bind04`, `bind05`, `sendto02` | PARTIAL/SKIP | `SCTP`/`UDP-Lite` protocols are not implemented; native runs them. |
| `epoll_ctl06`, `epoll_pwait06` | PARTIAL | Remaining variants need `pidfd`, `fanotify`, `userfaultfd`, `io_uring`, `bpf`, `fsopen`/`fspick` fd types. |

## Verification

- Podish net-io batch: **52 PASS / 4 PARTIAL / 4 FAIL / 5 BROKEN / 7 TIMEOUT / 1 SKIP** (from 25 PASS).
- `Fiberish.Tests`: **1860 passed, 0 failed, 1 skipped**.
