# Named FIFO: LTP-first testing and repair plan

Date: 2026-11-16
Status: bounded implementation plus review regressions delivered; upstream and expanded local gates pass.
Full-plan boundary/performance follow-ups remain explicit below. See [FIFO_BASELINE.md](FIFO_BASELINE.md) and
[FIFO_FIX_REPORT.md](FIFO_FIX_REPORT.md) for evidence and remaining limits.
Branch: `fifo-ltp-hardening`, based on `de6eef24465ae24240c78a601d561ad8caf455b2`.
LTP revision: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`.

## Goal and scope

Make guest named FIFOs behave as Linux pipe endpoints, not persistent regular
file storage. Start with unmodified upstream LTP, reproduce failures with the
same static i386 binaries on Linux and Podish, then repair bounded contracts.
Use local LTP-harness binaries only for gaps the pinned upstream cases do not
exercise; C# tests supplement them for scheduler/resource/allocation invariants.

Primary target is the Windows Podish tmpfs image backend. Extend structural
coverage to SilkFS/overlay after tmpfs works. Windows hostfs is not a reliable
FIFO/permission oracle; do not implement guest FIFOs using Windows named pipes.
Native Linux scratch must be on a Linux filesystem, not WSL `/mnt/c`.

Keep the existing pipe, exec, signal and filesystem batches unchanged. Do not
pull POSIX message queues, sockets, namespaces or filesystem-wide refactors into
this repair. `protected_fifos` sysctl policy is a separate security follow-up.

## Original pre-repair findings

- [PIPE_BASELINE.md](PIPE_BASELINE.md) records 18/18 pipe-batch passes, but its
  named-FIFO checks (`dup05`, `fsync03`) do not exercise stream transport.
- `IndexedMemoryInode.Mknod()` in `Fiberish.Core/VFS/IndexedMemoryFs.cs` creates
  ordinary metadata inodes with type `Fifo`. Its read path still uses the file
  page cache; this is not a rendezvous or a bounded consume-on-read stream.
- `ImplOpen()` in `SyscallHandlers.FileIO.cs` rejects every nonblocking
  write-only FIFO open with `ENXIO`, without checking for an existing reader.
  It is synchronous, as are the current `open`/`openat`/`openat2` wrappers.
- `PipeInode` already supplies ring-buffer I/O, wait queues, poll notifications,
  sizing and accounting. Its final close calls anonymous-inode finalization,
  which cannot be applied to a still-linked named FIFO.
- Pipe sizing, splice/tee and sendfile contain concrete `is PipeInode` checks.
  Adding an unrelated FIFO class alone would leave those dispatch paths behind.
- Existing managed FIFO tests cover type/access or metadata persistence, not
  open pairing, data flow or connection generations.

## 1. Upstream LTP first

Implemented `cases-fifo.json`, separate from `cases-pipe.json`, with
`requiresRoot: true`, `podishFsBackend: tmpfs`, and the following **15** binaries.
No upstream source changes, assertion changes, helper replacements or XFAILs.

### A. Transport gates and descriptor controls (8 binaries)

All paths below are relative to `upstream/testcases/kernel/syscalls/`.

| Case / directory | Executable contract | Role / limit |
| --- | --- | --- |
| `open06` / `open` | `O_WRONLY | O_NONBLOCK`, no reader -> `ENXIO` | Negative control; the current unconditional rejection may already pass it. |
| `read03` / `read` | With reader and writer open, empty nonblocking read -> `EAGAIN` | First direct transport gate; reader-present writer open must succeed before the assertion. |
| `write04` / `write` | Fill a FIFO, then nonblocking write -> `EAGAIN` | Bounded capacity/backpressure, not a growing file. |
| `select01` / `select` | Regular file, anonymous pipe and `O_RDWR` FIFO readiness | Already in basic batch; mixed control, not proof of all FIFO readiness semantics. |
| `lseek02` / `lseek` | Pipe/FIFO descriptors reject seek with `ESPIPE` | Already in basic batch; retain both mkfifo/mknod-created paths. |
| `dup05` / `dup` | Duplicate a FIFO descriptor | Existing pipe-batch control; no transport coverage claim. |
| `fsync03` / `fsync` | FIFO/pipe/socket `fsync`/`fdatasync` errors | Existing pipe-batch control. |
| `fcntl07` / `fcntl` | CLOEXEC on FIFO, pipe and regular-file descriptors | Old LTP harness; self-execs through PATH, no separate resource binary. |

`read03`/`write04` may currently stop in setup when opening the writer. Record
the actual `BROKEN`/errno rather than relabeling it as an assertion failure.
`select01` includes weak checks, so retain its unmodified result but do not use
it instead of a precise FIFO readiness probe. The runner already parses legacy
LTP output for `fcntl07`; verify classification and self-exec lookup natively.

### B. Creation/identity controls (7 binaries)

| Case / directory | Coverage |
| --- | --- |
| `mknod02` / `mknod` | New FIFO gid without parent SGID. |
| `mknod03` / `mknod` | Parent-group inheritance while the child creator uses another effective gid. |
| `mknod04` / `mknod` | Parent-group inheritance without adding SGID to a node not requesting it. |
| `mknod05` / `mknod` | Requested SGID and parent gid preservation. |
| `mknod06` / `mknod` | FIFO creation path errno, including existing nodes and a non-directory component. |
| `mknod08` / `mknod` | Effective gid without parent SGID; no implicit SGID. |
| `unlink05` / `unlink` | Remove a FIFO name; already in fs-fd batch, not an open-stream lifetime test. |

Use executable assertions, not stale prose comments, when describing metadata
coverage. A cross-listed control is intentional, not a newly invented test.

### Explicit exclusions / later candidates

- `open16`: changes host-global `protected_fifos`/`protected_regular` sysctls.
  Do not run it natively as part of this batch, even though LTP saves/restores.
- `mknodat02`: acquires/formats a block device; exclude from this bounded batch.
- `mknod07`: requires a read-only-filesystem fixture; defer rather than changing
  its fixture/assertions to fit the current image.
- `mknod01`: mixed regular/character/block device cases; not needed for this
  focused FIFO batch. `mknodat01` tests regular nodes, not FIFO transport.
- `testcases/kernel/fs/stream/stream02.c`: useful unmodified stdio/FIFO open
  follow-up, but outside the builder's current syscall-directory selection.
  Supporting that path is a separate small harness change, not assumed coverage.

## 2. Fill only the upstream coverage gaps

Implemented sources: `tests/ltp/fifo/`, using real `tst_test.h`, independent static
i386 binaries and a separate `cases-fifo-local.json`. Extend the builder's
explicit local-suite allowlist for `fifo` and record local source hashes, as for
`exec`; the upstream submodule remains unchanged. The table below is the target
coverage inventory, not a claim that every listed variant has been implemented.

| Binary | Target variants and observations |
| --- | --- |
| `fifo_open01` | Blocking reader-first and writer-first pairing; nonblocking reader succeeds without writer; writer succeeds with reader and fails without one; Linux `O_RDWR` immediate open; `O_PATH` is not a reader/writer; `open`, `openat` and supported `openat2` routes; access-denied ordering versus `ENXIO`. |
| `fifo_io01` | Independent opens share one consume-on-read stream; empty reads distinguish no writer (EOF) from live writer (`EAGAIN`/wait); close wakes readers/writers; full-buffer behavior, partial large writes, `PIPE_BUF` atomic multiwriter records and aggregate `writev` atomicity. |
| `fifo_poll01` | poll/select/epoll before the first writer, buffered data, final writer/reader close, drain and reconnect; level/edge notification, HUP/ERR without requested bits, no stale wakeups or busy loop. |
| `fifo_lifetime01` | dup/fork/CLOEXEC reference lifetime and shared `O_NONBLOCK`; hard-link/rename aliases share the same FIFO; unlink does not break open endpoints; unlink/recreate isolates old/new inode; final close drops unread data, reopening the linked node creates a fresh stream; path/fd stat identity and size remain correct. |
| `fifo_signal01` | Signal interruption/restart of blocked open/read/write with and without `SA_RESTART`; cancellation/exit releases pending-open counts and references; no phantom reader/writer or leaked FD. |
| `fifo_api01` | `FIONREAD`, `F_GETPIPE_SZ`/`F_SETPIPE_SZ`, positional I/O/seek, fsync/truncate/mmap errno; FIFO splice/tee/sendfile smoke cases where the API supports that endpoint. Establish exact flags/errno on Linux first. |
| `fifo_review01` | In-flight read versus close(fd); short packet pipe-to-pipe splice retains its tail; small writev EFAULT publishes no prefix; sendfile rejects same-pipe and pipe-to-regular-file input without changing either endpoint. |

The current seven-program native snapshot has 101 assertions. Managed review
regressions additionally cover both open descriptions during blocked copy APIs,
FD reuse, signal/retirement cleanup of files and rented arrays, overlay hardlinks
and lower copy-up/reopen with tmpfs/SilkFS uppers, O_ASYNC toggling, and zero
warmed LT/ET epoll allocations. These do not replace the unmodified LTP gate.

Special oracle requirements:

- Read EOF and poll HUP are not interchangeable. A newly opened nonblocking
  reader with no writer can read EOF without the same HUP history as a reader
  that observed a writer disconnect. Record per-open connection generations,
  reconnect transitions and select/epoll behavior before coding predicates.
- Derive capacity from `F_GETPIPE_SZ`; do not assume 64 KiB or a host default.
  Linux pipe-buffer slot/merge rules are not identical to free byte counts.
- `O_RDWR` FIFO success is a Linux contract, not a portable POSIX guarantee.
- FIFO transport is not ordinary file content: no persistent payload, file-size
  growth, seek position or regular-file `O_TRUNC`/`O_APPEND` data semantics.
- Add an isolated read-only tmpfs mount probe (with namespace prerequisites)
  or managed controlled-mount test for opening/writing an existing FIFO. Do not
  blindly apply regular-file `EROFS` rules to special-file transport.
- Use LTP checkpoints/control pipes and bounded poll/wait watchdogs, not sleeps
  as synchronization. Signal tests must establish that the child reached its
  wait state; supplement with managed queue-state checks where needed.
- Native baseline must pass with identical binary hashes before guest runs.
  Keep FAIL, BROKEN, TIMEOUT, TCONF and PARTIAL distinct; no skip-to-pass rule.

## 3. Repair design and order

Recommended direction: separate persistent FIFO metadata from ephemeral pipe
transport. Exact type boundaries are to be validated by the baseline, not a
commitment to replace the whole pipe subsystem.

### R1. Shared transport without anonymous-lifecycle coupling

- Extract/reuse the existing ring buffer, reader/writer accounting, sizing,
  waits and notifications rather than implementing a second data path.
- Associate one lazily created active transport with a FIFO inode, not a path
  string or per-open buffer. Hard links and renames retain identity; replacement
  inodes and separate runtime instances must not share callbacks/state.
- Last endpoint/pending-open retirement returns pooled buffers/queues, without
  finalizing a still-linked metadata inode. Persist mode/owner/link identity,
  never payload or waiters. Keep anonymous-pipe finalization unchanged.
- Preserve `stat`/`fstat` identity; simply swapping a path inode for a new
  anonymous `PipeInode` is insufficient. Audit existing `OpenedInode` dispatch.

### R2. Interruptible open rendezvous and rollback

- Keep ordinary-file open on a synchronous `ValueTask` fast path. Only FIFO
  pairing may suspend; never block the scheduler thread or use host `Task.Run`.
- Complete lookup/DAC first, retain the resolved inode across suspension, and
  release rented path bytes before waiting where possible. Rename/unlink must
  not force a pending open to resolve a different object on resume.
- Track readers/writers plus connection generations, including pending opens
  that must pair even when the peer closes quickly. Implement the NONBLOCK,
  RDWR and O_PATH rules from the native oracle.
- Reserve/publish descriptors and endpoint counts with explicit rollback for
  EINTR/restart, exit, EMFILE/ENFILE and OOM. Count open file descriptions, not
  every dup/fork FD. Reuse existing syscall restart and wait infrastructure.
- Audit read-only-mount guards in `ImplOpen`, `LinuxFile.WantWrite()` and the
  read/write wrappers; FIFO creation and FIFO endpoint use are different rights.

### R3. Read/write, readiness and ancillary dispatch

- Route scalar/vector I/O to the shared transport, not IndexedMemoryFs page
  cache. Preserve SIGPIPE/EPIPE, EOF, backpressure and whole-record atomicity.
- Integrate inode/per-open generation semantics into poll/select/epoll and
  their waiter registration/removal; do not infer FIFO HUP only from a global
  sticky `_writersClosed` flag.
- Audit `SyscallHandlers.Ioctl.cs`, `.SocketExtra.cs`, `.CopyRange.cs` and the
  `Structs.cs` vectored-I/O fast paths for concrete `PipeInode` dispatch.
  Use a narrow shared pipe capability/state lookup, not scattered FIFO stubs.
- Keep named FIFO metadata handling reusable for IndexedMemoryFs and
  `SilkFileSystemAdapter.cs`; validate overlay identity/copy-up separately.

### R4. Managed structural, failure and allocation regressions

- Proposed `NamedFifoTests`/`NamedFifoLifecycleTests`: deterministic scheduler
  pairing, canceled open, reader/writer counters, wait registration retirement,
  final-close/reopen, inode/dentry references and injected allocation failure.
- Reuse `PipeTests` and VFS tmpfs/SilkFS fixtures. Existing SilkFS FIFO remount
  test must keep metadata, but remount must not resurrect stream payload/state.
- No per-path global dictionary, VMA/page-cache payload or per-read/write
  wrapper/list/TCS allocation. Rent buffers/queues per active transport, not per
  syscall; allocate waiter state only when an operation actually blocks.
- Measure warmed ready read/write, nonblocking failure, open/close cycles,
  blocking wakeups and unchanged anonymous pipe throughput/B per operation.
  Separate unavoidable open-file objects from incremental transport allocations;
  do not claim an entire open syscall is allocation-free.

## 4. Delivery batches and acceptance gates

Current delivery state:

- [x] Upstream selection, Linux and preserved-CLI pre-fix baseline.
- [x] Six independent local LTP binaries and local-source build provenance.
- [x] Shared transport, open pairing, readiness/lifetime and rollback repairs.
- [x] Native-validated signal restart and cross-page/multiwriter vector probes.
- [x] Managed tmpfs, SilkFS/upper-overlay persistence and zero-allocation checks.
- [ ] Complete every target variant in section 2 (see report's explicit gaps).
- [ ] Controlled open/close, wakeup and anonymous-pipe throughput benchmarks.

The bounded repair can be delivered without claiming the unchecked full-plan
coverage. The report distinguishes passing gates from deferred contracts.

1. **Upstream selection/baseline:** add the 15-case manifest, build and record
   native/guest pre-fix results in `FIFO_BASELINE.md`. Preserve upstream hashes.
2. **Differential gap tests:** add local LTP binaries and minimal builder/runner
   tests; record Linux oracles and guest failures before repairs.
3. **Transport/open repair:** R1/R2 plus targeted managed regressions. Stop
   metadata/page-cache emulation without regressing ordinary-file opens.
4. **I/O/readiness/API repair:** R3, lifecycle/rollback tests and local probe gates.
5. **Backend/performance/report:** R4, tmpfs/SilkFS/overlay checks, repeated runs,
   allocation comparison and `FIFO_FIX_REPORT.md` with explicit remaining gaps.

Completion requires all applicable native-validated FIFO assertions passing,
no unexplained timeout/broken setup, and balanced descriptors, endpoint counts,
pooled buffers and wait queues after repeated close/cancel/reopen cycles.
Unavailable prerequisites remain visible coverage gaps, not successes.

Regressions: existing `cases-pipe.json` (18), `cases-exec.json`, basic
poll/select/epoll and signal batches, relevant fs-fd/permissions cases, full
`Fiberish.Tests` and runner unit tests. Recheck anonymous pipes explicitly when
extracting shared state. Use matching native/dependency DLL hashes and JIT
settings for performance comparisons; retain a pre-fix executable snapshot.

### First execution commands (after batch 1 creates the manifest)

From this worktree, using Linux/WSL for the build and root native reference:

```sh
bash tests/ltp/build.sh artifacts/ltp-fifo tests/ltp/cases-fifo.json
sudo python3 tests/ltp/run.py --backend native --artifacts artifacts/ltp-fifo \
  --manifest tests/ltp/cases-fifo.json
```

Then on the Windows host, build this worktree's CLI and run the same binaries:

```powershell
$env:DOTNET_ROLL_FORWARD = 'LatestMajor' # only needed on this net11-only host
dotnet build Podish.Cli/Podish.Cli.csproj -c Debug
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-fifo --manifest tests/ltp/cases-fifo.json
```

Do not use the main worktree's CLI as evidence for this branch. Keep native and
guest reports, hashes, logs, source revision/dirty state and assertion counts in
`artifacts/ltp-fifo/`; local gap probes use their own artifact directory.
