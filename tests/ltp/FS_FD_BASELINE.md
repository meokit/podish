# LTP filesystem metadata, fcntl/flock, and zero-copy I/O batch — baseline and fixes

Date: 2026-11-13
Manifest: `tests/ltp/cases-fs-fd.json` (115 cases)
Backend: Windows Podish, tmpfs image, guest root
Native reference: WSL Linux 5.15.153.1, root, i386 static executables
LTP: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`
Branch: `fix/ltp-fs-fd`

## Results

| Backend | PASS | FAIL | BROKEN | TIMEOUT | PARTIAL | SKIP |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Native Linux | 115 | 0 | 0 | 0 | 0 | 0 |
| Windows Podish, tmpfs (before fixes) | 61 | 29 | 16 | 3 | 1 | 8 |
| **Windows Podish, tmpfs (after fixes)** | **106** | **0** | **0** | **0** | **1** | **8** |

After fixes: **0 FAIL, 0 BROKEN, 0 TIMEOUT**. The single `PARTIAL` and the eight
`SKIP`s are configuration/feature availability, listed below.

## Excluded before the comparison

- `rename11`, `renameat01`, `linkat02`: require `tst_acquire_device()` plus
  `mkfs`/`mount` of a real block device, which the container image cannot
  provide. They were removed from the manifest rather than counted as failures.
- `fcntl40` (kernel 6.12), `splice08`/`splice09` (kernel 6.7): newer than the
  WSL2 5.15 reference kernel.
- Native `BROKEN`/`SKIP` on WSL2 i386 (`symlink03`, `fcntl01`, `fcntl07`,
  `fcntl11`, `fcntl16`, `fcntl19`, `fcntl20`, `fcntl21`, `sendfile09`) were
  removed from the manifest because there is no passing native baseline.

## Remaining non-PASS after fixes

| Case | Status | Reason |
| --- | --- | --- |
| `getcwd04` | SKIP | needs at least 2 online CPUs in the container |
| `fcntl24`/`25`/`26`/`32`/`33` | SKIP | `F_SETLEASE` break semantics are only exercised on non-tmpfs filesystems; the batch runs on tmpfs |
| `lseek11` | SKIP | `SEEK_DATA`/`SEEK_HOLE` are not implemented, test reports `TCONF` |
| `splice06` | SKIP | requires kernel 5.11 |
| `splice07` | PARTIAL | 246 passed, 0 failed; remaining variants need `pidfd`, `userfaultfd`, perf events, io_uring, BPF maps, `memfd_secret` (all `ENOSYS`) |

## What was fixed

### 1. Patch-based metadata and directory error semantics
- `PathWalker` now propagates `ENOENT` for a missing starting dentry and enforces
  the 40-link limit on total symlink traversals (not only nesting depth), fixing
  `stat03`, `lstat02`, `link08`, `linkat01`, `symlinkat01`, `readlinkat01`,
  `mkdirat02` and `rmdir02`.
- `statx` validates an unsupported `mask` and always reports `STATX_MNT_ID`;
  `/proc/self/mountinfo` now prints the real device `major:minor` from the mount
  inode so `statx01` can match `stx_mnt_id` (`statx01`, `statx03`).
- `fchdir` performs the search-permission check (`fchdir03`).

### 2. `O_PATH` and descriptor semantics
- Added `FileFlags.O_PATH`; `open()` honors it (no create/truncate/access checks,
  no read/write permission requirement) so `accept()`/`fcntl()` behave like
  Linux for `O_PATH` descriptors (`readlinkat01`, `close_range02`).
- Every `opendir`/`openat` now requires a directory for relative paths
  (`ENOTDIR`), matching `linkat01`, `symlinkat01`, `readlinkat02`,
  `renameat`/`unlinkat` semantics.
- `AllocFD`/`dup` now enforce `RLIMIT_NOFILE` (`EMFILE`/`EINVAL`), fixing `fcntl12`.
- `readlink*` accepts `ssize_t == 0` as `EINVAL` (`readlinkat02`).

### 3. POSIX record locks, `flock`, leases and dnotify
- New unified `FileLockState`: byte-range POSIX/OFD locks with conflict
  detection, `F_GETLK` reporting the real holder (`l_whence = SEEK_SET`,
  `l_pid`), unlock splitting, merge on acquire, `EDEADLK` via a wait-for graph,
  and cooperative blocking waits (`F_SETLKW`) instead of returning success.
  Covers `fcntl05/13/14/15/17/22/30/31/33..39`.
- Locks are released when a process exits (`ReleasePosixLocksForProcess`) and
  when the owning descriptor is closed, so a blocked waiter observes the lock
  becoming free (`fcntl17`).
- `flock` is now synchronous-safe and blocking waits are cooperative, fixing
  `flock02`/`flock07` timeouts.
- `F_SETLEASE`/`F_GETLEASE` implement read/write lease grants with conflicting
  open checks (`fcntl23`, `fcntl27`).
- `F_NOTIFY` (dnotify) with `DN_ATTRIB`/`DN_RENAME`/`DN_MULTISHOT` and
  `F_SETSIG`/`F_GETSIG`, delivering `si_fd` in the SIGIO/SIGPOLL siginfo
  (`fcntl38`, `fcntl39`), including the corrected i386 `siginfo_t` layout.
- `F_SETPIPE_SZ`/`F_GETPIPE_SZ` with real pipe resizing, `CAP_SYS_RESOURCE`
  gating (`EPERM` for unprivileged callers over `/proc/sys/fs/pipe-max-size`),
  `/proc/sys/fs/pipe-max-size` and `/proc/sys/fs/lease-break-time`
  (`fcntl30`, `fcntl34..37`).

### 4. Zero-copy and modern descriptor syscalls
- `splice` now validates access modes and `O_APPEND`, rejects non-file
  descriptors (`ENOTDIR`/`EINVAL`) and unconnected sockets, and still supports
  pipe↔file/socket transfers (`splice02/03/05/07`).
- `tee` rejects two ends of the same pipe (`tee02`).
- Implemented `vmsplice` (user↔pipe, `SPLICE_F_NONBLOCK` semantics) and
  `copy_file_range`, plus `close_range` with `CLOSE_RANGE_CLOEXEC` and
  `CLOSE_RANGE_UNSHARE` descriptor-table fork (`vmsplice01..04`,
  `copy_file_range03`, `close_range02`).
- `sendfile` now writes back the updated offset instead of the original value
  (`sendfile02`).
- `preadv2`/`pwritev2` reject unknown `RWF_*` flags with `EOPNOTSUPP`
  (`preadv202`, `pwritev202`).
- `RLIMIT_FSIZE` is enforced for `write`/`writev` (`EFBIG` + `SIGXFSZ`),
  fixing `llseek01`.

## Verification

- FS/FD batch: **106 PASS**, 0 FAIL, 0 BROKEN, 0 TIMEOUT, 1 PARTIAL, 8 SKIP.
- Regression batches re-run with the same binary: Core 50/50, Signals 46/46,
  Permissions 70/71 (pre-existing `access02` fixture gap), Sync 30 PASS
  (1 known FAIL, 1 SKIP), Memory 59 PASS (1 known FAIL, 1 BROKEN, 5 SKIP).
- C# tests: `Fiberish.Tests` 1860 PASS / 0 FAIL / 1 SKIP, SilkFS 13,
  Wayland 19, Pulse 49.
- `python -m unittest discover -s tests/ltp -p 'test_*.py'`: 20/20.

## Code review follow-up (2026-11-14)

The 11 review findings are covered by the project regression tests
in `Fiberish.Tests/Syscalls/FsFdReviewRegressionTests.cs` and
`Fiberish.Tests/VFS/FileLockReviewRegressionTests.cs`. These are project tests;
no upstream LTP source, assertion, case manifest or expected errno was changed.

| Review | Fix | Regression coverage |
| --- | --- | --- |
| 1 | Normalize `O_PATH` flags, do not invoke data-open hooks, and reject data/metadata operations that require a normal fd | `OPath_UnprivilegedOpen_DoesNotGrantReadOrWrite`, `OPath_CloexecIsPreserved_ButNotReportedAsAStatusFlag` |
| 2 | Process only the submitted `nrSegs`, not the full rented array | `Vmsplice_UsesOnlyTheSubmittedIovecs` (1 and 3 iovecs, with a deliberately primed pool tail) |
| 3 | Wait for the whole atomic pipe-write chunk instead of one byte | `Vmsplice_SmallAtomicWrite_YieldsUntilTheWholeChunkFits` (4095/4096 bytes occupied; reader frees the needed space) |
| 4 | Reference-count descriptor tables per task; inherit the caller's table and release it on thread retirement | `CloseRange_Unshare_DoesNotChangeSiblingDescriptors`, `CloseRange_Unshare_PrivateTableIsClosedOnThreadExit`, `CloseRange_Unshare_ForkUsesTheCallingThreadsTable`, `CloneThread_WithoutCloneFiles_CopiesTheCallingThreadsTable`, `FdHelpers_AfterUnshare_ExplicitTaskOverridesAmbientSiblingContext` |
| 5 | Keep overlay lock state independent of the changing backing inode; share it with lower/upper aliases within that overlay | `Overlay_CopyUp_PreservesLockConflictsAndUnlocks` (flock/POSIX/OFD), `Overlay_CopyUp_NewUpperOnlyAliasFindsTheOriginalLocks`, `Overlay_IndependentOverlaysDoNotShareLocksThroughTheLowerLayer` |
| 6 | Route POSIX/OFD release through the same lock-state resolver as acquisition | `Overlay_Close_ReleasesTheSameLockStateUsedForAcquisition` (POSIX close-any-fd versus OFD last-close), `CloseRange_Unshare_DoesNotReleaseLocksOnRetainedDescriptors` |
| 7 | Coordinate hostfs locks by host file identity, using a weak registry; wait queues remain scheduler-local | `Hostfs_SeparateMounts_CoordinateLocksByHostFileIdentity` (flock/POSIX/OFD), `SharedLockState_WaitersOnDifferentSchedulers_AreBothWoken` |
| 8 | Accept `CLOSE_RANGE_UNSHARE` together with `CLOSE_RANGE_CLOEXEC` | `CloseRange_Unshare_DoesNotChangeSiblingDescriptors(flags: 6)` also checks the sibling's CLOEXEC bits and subsequent thread inheritance |
| 9 | Reject `copy_file_range` to an `O_APPEND` destination with `EBADF` before modifying data or offsets | `CopyFileRange_AppendDestination_ReturnsEbadfWithoutChangingFilesOrOffsets` |
| 10 | Clip the iovec prefix at `RLIMIT_FSIZE`; allow zero-byte writes and use EOF for ordinary append writes | `FsizeLimit_ClipsWritesAcrossTheLimit` (write/pwrite/writev/pwritev), `FsizeLimit_ZeroLengthWriteAtLimit_SucceedsWithoutSignal`, `FsizeLimit_AppendUsesEof_AndRaisesSigxfszOnlyWhenNoByteFits` |
| 11 | Allow `fchdir` on an `O_PATH` directory while retaining search-permission checks | `Fchdir_OPathDirectory_ChecksSearchPermission` (allowed and `EACCES`) |

There are 35 new xUnit cases including theory variants. The existing
`SysExit_LeaderWithAliveThreads_MustNotFinalizeProcessOrCloseFds` fixture now
explicitly requests `CLONE_FILES`, as required for the shared-table behavior
its unchanged assertions exercise.

Verification: `Fiberish.Tests` **1895 PASS / 0 FAIL / 1 SKIP**; the FS/FD LTP
batch remains **106 PASS / 8 SKIP / 1 PARTIAL**, with no FAIL/BROKEN/TIMEOUT;
Core **50/50** and Signals **46/46**. The original i386 Linux/Podish review
probes also reproduce the corrected permission, iovec-count, blocking-pipe,
thread-unshare, append, file-size-limit and `fchdir` behavior. The separate
pre-existing two-reader flock upgrade deadlock is not one of these 11 fixes.
