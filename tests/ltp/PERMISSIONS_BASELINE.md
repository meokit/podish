# LTP permission / identity batch — first baseline

Date: 2026-11-12
Manifest: `tests/ltp/cases-permissions.json` (71 cases)
Artifacts: `artifacts/ltp-permissions` (ignored by Git)
LTP: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`

## Results

| Backend | PASS | FAIL | BROKEN |
| --- | ---: | ---: | ---: |
| Native Linux, root (WSL) | 71 | 0 | 0 |
| Windows Podish, guest root | 38 | 27 | 6 |

Podish assertion counts: 333 passed, 664 failed, 6 broken-test aborts.
The native baseline is intentionally run as root because LTP's `.needs_root`
tests create root-owned fixtures and drop privileges themselves. The same
static i386 ELF binaries are used on both sides. No upstream assertions were
changed and no runtime permission fix is included in this batch.

The native baseline exposed two exclusions before the Podish run:

- `rename11` / `rename14` need a loop block device and `all_filesystems`; they
  were removed from the manifest.
- `chroot01` expects `EPERM` for unprivileged `chroot`, but the WSL kernel
  returns `EACCES`; it is not a stable cross-environment baseline. `chroot04`
  remains and tests the search-permission path.

## What the failures expose

### 1. Windows hostfs does not preserve POSIX mode or ownership

This is the dominant cause. `Fiberish.Core/VFS/Hostfs.cs` has an explicit
non-Unix fallback:

- `HostfsOwnershipMapper.RefreshGuestProjection` forces mode `0755`.
- `SetGuestMode` forces mode `0755` and returns success.
- `SetGuestOwnership` returns success without changing owner/group.

Consequences visible in the baseline:

- `chmod03/05/07`, `fchmod02/03/04/05`: calls succeed, but mode stays `0755`;
  sticky/setgid bits disappear.
- `chown02/03/05`, `fchown02/03/05`, `fchownat01`: calls succeed, but UID/GID
  stay unchanged or are misprojected.
- `umask01`: every created file is `0755`; 511 assertion failures.
- `mkdir02`, `open10`: setgid inheritance cannot be observed because the
  directory mode is rewritten to `0755`.
- `mkdir04`, `unlink08`, `rename09`: tests create restrictive `0700/0555/0666`
  directories, but hostfs exposes `0755`, so operations that should fail
  succeed.
- `access01` and `faccessat202` include many mode-dependent assertions that
  cannot be trusted on this backend.
- `link04`: hardlink permissions and `EEXIST`/`EIO` behavior are affected by
  the same mode-less hostfs path.

These are real defects for running permission-sensitive workloads on Windows,
but they are **backend metadata defects**, not proof that every check in
`DacPolicy` is wrong. They must be rerun on tmpfs/overlay (or a Unix hostfs)
after a metadata-capable backend is wired into this runner.

### 2. Symlink creation on Windows hostfs returns EIO

`access02`, `faccessat201`, `chmod08`, `lchown01`, `fchownat02` abort in setup
with `symlink(...) failed: EIO`. The suite therefore cannot test symlink
permission/lchown/follow behavior on Windows. The core C# tests cover symlink
objects in tmpfs, so this is specific to the Windows hostfs path rather than a
missing inode abstraction.

### 3. Saved-set-UID semantics are incomplete

`setreuid05` fails transitions that rely on the saved set-user-ID:
`setreuid(-1, other)`, `setreuid(other, main)`, and related calls return
`EPERM` even when Linux permits them for a privileged process. This is a pure
identity-state bug and does not depend on hostfs metadata.

### 4. `setgroups` error precedence is wrong

`setgroups03/04` expect `EFAULT` for the exercised invalid-list cases but get
`EINVAL`. The implementation appears to validate count/size before attempting
the user-pointer copy. Linux's observable precedence must be verified against
a native baseline; the current ordering hides the pointer fault. This is a
pure syscall argument-validation bug.

### 5. `O_NOATIME` privilege check is missing

`open02` shows an unprivileged process can open `O_RDONLY | O_NOATIME` without
owning the file or having `CAP_FOWNER`. The open path does not enforce the
`O_NOATIME` capability/ownership rule. This is independent of mode storage.

### 6. `chroot` returns EPERM before path search permission

`chroot04` expects `EACCES` when the target directory has no search permission.
Podish returns `EPERM` from the privilege check first. Linux checks the path
and its search permissions before reporting the capability failure for this
case. The chroot handler needs ordered path/permission validation.

### 7. `access`/`faccessat` trust the current metadata projection

Even after hostfs metadata is fixed, the tests point at a second risk: `access`
must use real IDs and `faccessat(AT_EACCESS)` effective IDs, and every path
component needs search/execute permission. The current `DacPolicy.CheckPathAccess`
only evaluates the final inode; path traversal checks are in `PathWalker`.
These paths must be validated on a metadata-capable backend, not inferred from
the hostfs failures above.

## Next work items

1. Add a metadata-preserving backend to the permission runner (tmpfs/overlay
   or a Unix-hostfs run) and rerun this manifest before changing DacPolicy.
2. Fix the non-Unix hostfs metadata policy: mode/owner writes cannot silently
   succeed while forcing `0755`.
3. Fix Windows hostfs symlink creation or explicitly classify it as
   unsupported and skip symlink-dependent permission tests.
4. Fix `setreuid` saved-set-UID transitions and `setgroups` argument precedence.
5. Add `O_NOATIME` ownership/capability checks.
6. Reorder chroot permission checks.
7. Continue with path-component search permission tests after the backend can
   preserve modes.

Until items 1-2 are addressed, a green permission run on Windows hostfs would
be misleading; do not weaken or skip these assertions to make the batch green.
