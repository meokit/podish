# Named FIFO repair report (2026-11-16)

Branch: `fifo-ltp-hardening`. See
[FIFO_BASELINE.md](FIFO_BASELINE.md) for the unmodified LTP revision, original
failures and comparison method. This is a bounded compatibility repair, not a
claim that all Linux FIFO/pipe semantics are implemented.

## Design and fixes

- Keep the filesystem inode as the metadata/namespace identity. Attach one
  lazy hidden `PipeInode` transport per active FIFO inode; reuse its ring buffer,
  waits, readiness notifications, sizing and quota accounting. Do not use
  Windows named pipes or persistent file-page-cache payload.
- `LinuxFile.OpenedInode` still serves stat, permissions and lifecycle metadata;
  `IoInode` selects stream I/O. Route scalar/vector I/O, select/poll/epoll,
  ioctl/sizing and supported splice/tee/vmsplice/sendfile paths through it.
- Support blocking reader/writer rendezvous, reader-present nonblocking writer
  opens, Linux `O_RDWR`, and inactive `O_PATH` handles. Peer generations retain
  short-lived connections. Ordinary file opens remain synchronous; rented
  pathname bytes are returned before a FIFO wait can suspend.
- Reserve the FD without publishing a half-open file. Allocations skip reserved
  FDs; `dup2` cannot overwrite them. Roll back reservations, transport endpoints
  and file references on signal, retirement, descriptor exhaustion and injected
  OOM. Retirement has explicit cleanup because canceled guest continuations do
  not execute their async `finally` blocks.
- Preserve EOF versus initial poll HUP history. Report terminal readiness with
  an empty requested mask; reconnect clears HUP while a writer is live.
- Final endpoint close drops unread bytes and pooled state, without finalizing
  a linked metadata inode. Dup aliases keep the same open description alive;
  hard-link/rename aliases share the transport; unlink/recreate isolates inodes.
- Existing FIFOs can carry traffic on a read-only mount; creation remains a
  namespace mutation. FIFO data does not grow `st_size` or advance file offsets.
- Apply `PIPE_BUF` atomicity to an entire small `writev`, including a single
  cross-page iovec. Keep scalar/single-page writes direct; gather split records
  into an `ArrayPool` buffer only when required. A blocking pipe read returns
  already available bytes instead of waiting again to fill the guest request.
- Pipe-to-pipe splice peeks before writing and consumes only accepted bytes;
  output backpressure must leave the source intact.
- Sendfile writes into the transport rather than metadata page cache; input
  offsets advance only by accepted bytes. EAGAIN preserves the source position,
  explicit offsets leave the shared file position unchanged, and pipe wait
  registration uses the I/O inode. Four dedicated sendfile assertions failed
  before this follow-up and now pass with unchanged binary hashes.

## First-repair results (historical snapshot)

| Gate | Result |
| --- | --- |
| Unmodified upstream FIFO selection | Linux 15 PASS / 85 assertions; repaired tmpfs 15 PASS / 85 |
| Initial expanded local snapshot | Linux and repaired tmpfs 6 PASS / 57 assertions |
| Signal/cross-page expansion | Linux and repaired tmpfs 6 PASS / 74 assertions |
| First-repair local snapshot (including multiwriter and API backpressure) | Linux and repaired tmpfs 6 PASS / 86 assertions |
| First-repair local snapshot, configured SilkFS image backend | 6 PASS / 86 assertions |
| Two further tmpfs repeats | 6 PASS / 86 assertions each |
| Existing anonymous-pipe batch, final build | 18 PASS / 103 assertions |
| Full managed suite, first-repair build | 2131 PASS, 0 FAIL, 1 existing SKIP |
| Host runner unit tests | 28 PASS |

Artifacts for these snapshots are `artifacts/ltp-fifo/`,
`artifacts/ltp-fifo-local-next/`, and `artifacts/ltp-fifo-local-expanded/`.
Do not compare counts or hashes across different snapshots. Final local results
are in `artifacts/ltp-fifo-local-validated/results/`; final upstream and pipe
reports are `artifacts/{ltp-fifo,ltp-pipe}/results/podish-final.json`.
Managed log: `artifacts/ltp-fifo/managed-final.log`.
Repeat reports are `podish-repeat-{1,2}.json`; the configured SilkFS image run is
`podish-silkfs.json`. No direct-mapping/zero-copy backend claim is inferred from
that configuration-only runner label; actual SilkFS inode/persistence behavior
is covered separately by the managed remount fixtures.

The native oracle was WSL Linux `5.15.153.1-microsoft-standard-WSL2`; guest
binaries are static i386 built with the pinned LTP harness. All eight local
source hashes match final `build.json`. The final CLI and managed test copies
of `Fiberish.Core.dll` both hash to
`98de8ff4848890354ee4443115b34f279a3287d51b21a6ac8eddba68dcdf7c28`.

### First-repair broader regression checkpoint

The copied, hash-verified existing binary batches live under
`artifacts/fifo-regression-*`; their original artifacts were not changed:

| Batch | Result |
| --- | --- |
| Basic, tmpfs | 49 PASS, 1 PARTIAL (`read02`: 3 passed / 2 skipped) |
| Local exec | 3 PASS, 2 PARTIAL; 56 passed / 7 namespace-dependent skips |
| Signals/events | 46 PASS |
| fs-fd selected fcntl/copy APIs (50 binaries) | 43 PASS, 6 SKIP, 1 PARTIAL |
| Final sendfile/splice/tee/vmsplice recheck (20 binaries) | 18 PASS, 1 SKIP, 1 PARTIAL; 283 passed / 154 skipped |

The final API recheck retains `splice06` SKIP and `splice07` PARTIAL (246 passed,
153 skipped). All non-PASS basic/exec/fs-fd statuses above were independently
reproduced using the preserved pre-FIFO CLI with identical counts. They are
existing coverage limitations, not successes or newly introduced failures.
There were no failed/broken assertions or timeouts at this regression checkpoint.
The broader checkpoint predates the final sendfile change; all affected copy
API cases and the full managed suite were rerun after it.

The managed suite includes real SilkFS and overlay-with-SilkFS-upper remounts:
type/mode/inode identity persist, unread stream bytes do not. It also covers
read-only mounts, initial-HUP waiter registration, brief-peer pairing,
retirement, descriptor exhaustion and three allocation-failure checkpoints.

The first full-suite attempt lacked host `sqlite3.dll`; it was aborted and its
log retained as `managed-missing-sqlite.log`. The passing rerun supplies the
existing x64 SQLite DLL via process-local `PATH` only. No product or machine-wide
configuration was changed to hide those failures.

## Review follow-up: regression tests and repairs

The seventh independent program, `fifo_review01`, passes 15 assertions on Linux
but records **4 passed / 11 failed** on the preserved first-repair CLI. The six
existing programs still pass there. Thus the current local snapshot contains
**7 programs / 101 assertions**, rather than the historical six/86 above.
Artifacts: `artifacts/ltp-fifo-review/results/{native,podish-before}.json`.

Before implementation, the expanded managed FIFO tests recorded 25 PASS and
5 FAIL: in-flight read lost its endpoint, two overlay-upper variants isolated
hardlink transports, and both LT/ET epoll modes allocated 40960 bytes per 256
roundtrips. Logs are `artifacts/fifo-review/managed-before.log`.

Repairs:

- `FileIoLease` pins open descriptions, not FD numbers, across read/write,
  sendfile, splice, tee and vmsplice. Its ready path uses stack-local references;
  actual suspension alone allocates task-retirement cleanup. FD reuse cannot
  redirect the pending operation. Signals and retirement release both pins.
- `TaskIoBuffer<T>` returns rented copy buffers and iovecs even when task
  retirement suppresses async continuations/finally. Ready completion creates
  no cleanup object; the retirement registration returns each array at most once.
- Overlay hardlinks share an active transport registry keyed by backing inode
  identity, registering lower and upper identities on copy-up. Last close
  removes registry entries. A cached empty alias first joins an existing active
  identity on reopen. Managed tests cover tmpfs and real SilkFS uppers, upper
  FIFO hardlinks and lower FIFO copy-up, final close/reopen and registry cleanup.
- Packet pipe-to-pipe splice/sendfile advance only accepted bytes, retaining the
  packet tail. A later short read still discards only that packet's remainder,
  not the following packet. Small atomic writev returns EFAULT without publishing
  a gathered prefix, including a fault within a cross-page iovec.
- Sendfile rejects the same pipe and pipe input to a nonpipe output before
  touching queued bytes or output. Pipe input with an explicit offset is ESPIPE.
- Poll notifications use immutable topology snapshots and a bounded pool of
  at most 16 publications. Snapshot changes allocate only on subscribe/dispose,
  not ordinary signals. Tests cover cancellation, stale posted callbacks,
  reentrancy, foreign dispatch (including inline Post), exceptions and backlogs.
- O_RDWR read-only epoll interests no longer subscribe to the unused output
  direction. Ordinary writes scan only explicitly O_ASYNC-enabled readers,
  not all idle endpoints. F_SETFL enable/disable has a SIGIO regression test.

### Current guest validation

| Gate | Result |
| --- | --- |
| Unmodified upstream FIFO selection | 15 PASS / 85 assertions |
| Seven-program local suite, tmpfs | 7 PASS / 101 assertions, identical to Linux |
| Two further tmpfs repeats | 7 PASS / 101 assertions each |
| Local suite, configured SilkFS image backend | 7 PASS / 101 assertions |
| Existing pipe batch | 18 PASS / 103 assertions |
| sendfile/splice/tee/vmsplice (20 binaries) | 18 PASS, 1 SKIP, 1 PARTIAL; 283 passed / 154 skipped |
| Basic batch | 49 PASS, 1 PARTIAL; 362 passed / 2 skipped |
| Local exec batch | 3 PASS, 2 PARTIAL; 56 passed / 7 skipped |
| Signals/events | 46 PASS / 737 assertions |

All broader status/count pairs match their previous checkpoint, including the
existing PARTIAL/SKIP cases; no new failed/broken assertions or timeouts occurred.
These coverage limitations have not been relabeled as PASS.

Current local reports are `artifacts/ltp-fifo-review/results/podish-final.json`,
`podish-repeat-{1,2}.json` and `podish-silkfs.json`. Upstream and pipe reports are
`artifacts/{ltp-fifo,ltp-pipe}/results/podish-review-final.json`. The broader
reports are `artifacts/fifo-regression-*/results/podish-review-final.json`,
except the copy API selection is `fifo-regression-ltp-fs-fd/results/podish-review-copy-apis.json`.
`artifacts/fifo-review/final-validation-summary.json` records the checked totals.
As in the first repair, the guest SilkFS runner label is configuration evidence,
not a zero-copy claim; real SilkFS/overlay behavior is also tested by managed
fixtures. Upstream LTP sources and assertions remain unmodified.

### Current managed and allocation validation

| Gate | Result |
| --- | --- |
| Focused FIFO/pipe/poll notification tests | 89 PASS |
| Full managed suite | 2164 PASS, 0 FAIL, 1 existing SKIP |
| Host runner unit tests | 28 PASS |
| Warmed scalar, single/multi-iovec transport | 0 B/roundtrip |
| Warmed SysWrite + SysRead handlers | 0 B/roundtrip |
| Warmed O_RDWR FIFO with EPOLLIN watch | 160 B before -> 0 B/roundtrip after |
| Warmed managed LT/ET epoll write/harvest/read | 0 B over 256 roundtrips, both modes |

Logs: `artifacts/fifo-review/managed-lifecycle-fixed.log`, `managed-final.log`,
and `allocations-and-overlay-fixed.log`. Release measurements use 20000 warmups,
100000 roundtrips and `DOTNET_TieredCompilation=0`. The 128-idle-reader sample
and after-close sample are 58.4 and 58.2 ns/roundtrip respectively (both 0 B),
versus the review's 340.3/145.9 ns; the old all-endpoint scan is removed.
Timings are indicative only: concurrent host workload was not controlled, so
these samples are not a controlled throughput or latency guarantee.

The final Debug CLI and full managed-test copies of `Fiberish.Core.dll` both
hash to `fb4506f4837fef1196e91282f125cf85f8ad8bef8341adc4bc93130b922c5c6d`.
All nine current local LTP source hashes match the review `build.json`; the
upstream submodule remains clean at the pinned revision. The full suite's one
skip is the pre-existing image-backed `ContainerRestartTests` fixture.

## Allocation and performance constraints

- Read/write/poll has no new per-I/O object, delegate or closure allocation.
  Ready polling was changed from predicate delegates to direct sampling.
- The managed warmed scalar FIFO write/poll/read test asserts **0 B** across
  256 iterations after 32 warmups. This measures the transport, not an entire
  guest syscall or open operation.
- Atomic vector writes have a synchronous ready fast path and a separate async
  blocking path. A dedicated warmed empty-read/poll/vector-write/read regression
  also asserts **0 B** across 256 iterations; buffers are pooled and not retained
  across waits. This removed a measured 168 B/iteration Debug async-state-machine
  allocation in the initial vector implementation, without weakening the test.
- Open-file objects and actual blocking registrations still allocate. Per-file
  peer history uses one generation field, not a separate object or global map.
- The review's x64 layout measurements remain approximately LinuxFile 152 B
  (136 B before FIFO) and TmpfsInode 320 B (312 B before). These global field
  costs also affect ordinary files; this follow-up does not claim to remove them.
- No controlled throughput, open/close or wakeup latency claim is made here.
  Concurrent host workload and Debug timings are not a sound speedup benchmark.

## Explicit remaining boundaries

- Linux page-slot/merge behavior is not fully represented by the shared byte
  ring. In particular `vmsplice` vectors can occupy separate Linux pipe slots;
  their oracle fixture must reserve those slots, not assume byte capacity alone.
- Full large-write partial-progress, generalized packet-pipe splice behavior,
  adversarial guest-memory faults beyond the covered small writev cases, and
  socket-side splice cancellation remain follow-ups.
- The legacy pipe-to-nonpipe splice buffered-copy path still needs a dedicated
  no-loss audit under output backpressure; this report does not certify it.
- Overlay alias-cache eviction, independent copy-up of pre-existing lower
  hardlinks and cross-mount transport identity need additional tests before
  claiming complete overlay support. Upper FIFO hardlinks, lower FIFO copy-up
  with a live transport and both-path reopen are now covered.
- Local `openat2` flags/resolve-policy, access-denied ordering, FIFO CLOEXEC/exec
  lifetime and multi-reader fairness are not fully covered by the local suite.
- `VfsStream` is currently an ELF-loader regular-file adapter; its direct
  `OpenedInode` use was audited, not generalized into a FIFO stream adapter.
- Hostfs Windows FIFO creation, host-global `protected_fifos`, namespace-dependent
  assertions and device-requiring cases remain outside this bounded task.
