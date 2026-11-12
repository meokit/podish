# LTP permission / identity batch — tmpfs backend

Date: 2026-11-12
Manifest: `tests/ltp/cases-permissions.json` (71 cases)
LTP: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`

## Results

| Backend | PASS | FAIL | BROKEN |
| --- | ---: | ---: | ---: |
| Native Linux, root (WSL) | 71 | 0 | 0 |
| Windows Podish, guest root, hostfs | 38 | 27 | 6 |
| Windows Podish, guest root, **tmpfs image** | **62** | **9** | **0** |

tmpfs run: 492 assertions passed, 25 failed, 0 broken/skipped.
The native baseline and Podish use the same static i386 ELF hashes. The tmpfs
image contains one copy of all selected binaries under `/opt/ltp/testcases/bin`;
each case runs in its own container and root. Modes and ownership are written
explicitly into the image tar, so Windows hostfs mode rewriting is no longer
part of the test.

The runner builds the image with Python `tarfile` and imports it through the
CLI. Do not generate this tar with Windows `tar`: it commonly writes binaries
as `0644`, which correctly causes `Execute access denied` at image startup.

## What changed from hostfs

The following hostfs failures disappear on tmpfs and are therefore backend
metadata limitations, not `DacPolicy` failures:

- `chmod*`, `fchmod*`, `chown*`, `fchown*`, `umask01`
- `mkdir04`, `unlink08`, `rename09`, `link04`, `open10`
- mode-dependent `access01` / `faccessat202` assertions

Windows hostfs currently forces mode `0755` and returns success for ownership
changes without applying them. That path must not be used as evidence that
permission checks are correct; `tmpfs` is the meaningful Windows reference for
the permission batch.

## Remaining tmpfs failures

### 1. `chown02` / `fchown02`: setgid not cleared on chown

After a superuser `chown`, LTP expects a group-executable regular file with
`S_ISUID|S_ISGID` to become `0100770`. Podish leaves `0106770`; the setgid bit
survives. `ApplySetIdClearOnChown` returns the old mode unchanged when the
requested UID/GID equal the current ones, but Linux still applies the set-ID
clearing rules for a superuser `chown`. The helper also lacks the Linux rule
that preserves `S_ISGID` on a non-group-executable regular file when ownership
really changes. This is a real chown semantics bug, not a backend issue.

### 2. `mkdir02`: setgid directory inheritance is lost

LTP creates a parent directory with `S_ISGID`, chowns it to a free GID, then
runs as nobody. A new child directory should inherit both the parent GID and
`S_ISGID`; Podish gets GID `65534` (nobody) and no setgid bit.

This is consistent with `ApplySetIdClearOnChown` clearing `S_ISGID` on the
parent directory during the root `chown`. For directories, Linux preserves
setgid when the caller has `CAP_FSETID` (or root); the helper currently applies
the regular-file set-ID clearing rule without a directory/type distinction.
That destroys the inheritance contract. The same helper must distinguish
regular files, directories, and the group-executable special case.

### 3. `setreuid05`: saved-set-UID transitions incomplete

Legal privileged transitions that depend on the saved UID still return
`EPERM`, and some transitions leave the expected real/effective/saved triple
wrong. Examples:

- `setreuid(-1, other)` fails with `EPERM`
- `setreuid(other, main)` fails with `EPERM`
- `setreuid(-1, main)` fails with `EPERM`
- after a permitted transition, `ruid/euid/suid` does not match the expected
  `1/1/1` or `1/2/2` state

This is isolated to saved-ID bookkeeping in `CredentialService.SetReUid`, not
the filesystem backend.

### 4. `setgroups03/04`: count limit and EFAULT precedence

`setgroups` is capped at `1024` in `SyscallHandlers.Identity.cs`, whereas Linux
accepts the full `NGROUPS_MAX` range (65536 for this ABI). `setgroups(65536,
bad_pointer)` therefore returns `EINVAL` before touching the user pointer;
the expected result is `EFAULT`. The hard-coded 1024 limit also prevents valid
large supplementary-group operations. Fix both the limit and validation order.

### 5. `open02`: `O_NOATIME` privilege check missing

An unprivileged guest process can open a file with `O_RDONLY | O_NOATIME` even
when it neither owns the file nor has `CAP_FOWNER`. Linux must return `EPERM`.
The open-flag path silently accepts the flag.

### 6. `chroot04`: EPERM before search-permission check

`chroot04` creates a directory without search permission, drops to nobody, and
expects `EACCES`. Podish checks the privilege/capability first and returns
`EPERM`. The path lookup must run first (or the capability error must be
deferred) so the more specific path error is observable.

### 7. `access02`: environment limitation, not a permission conclusion

`access02` creates a `#!/bin/sh` executable and runs it. The minimal tmpfs image
has no `/bin/sh`, so four `X_OK` execution assertions fail with `ENOENT`; the
other 12 access assertions pass. This is an image dependency, not evidence that
`access(X_OK)` is wrong. Either add a real guest shell asset to the image or
exclude this binary from the permission score in future runs. Do not replace
`/bin/sh` with a no-op fake just to make the test green.

## Hostfs versus tmpfs summary

The hostfs run was useful for discovering that Windows hostfs silently loses
POSIX metadata; it is not a valid permission-test backend. After switching to
tmpfs, the failures collapse to eight failing binaries covering six substantive
areas (chown/fchown share one), plus one missing test asset (`/bin/sh`). The
next fixes should target directory setgid preservation, chown set-ID clearing,
saved-UID transitions, setgroups bounds/order, `O_NOATIME`, and chroot error
ordering.

## Reproduce

The permission manifest sets `"podishFsBackend": "tmpfs"`, so no flag is
required. Use `--fs-backend hostfs` only to reproduce the old comparison.

```powershell
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-permissions `
  --manifest tests/ltp/cases-permissions.json
```

Reports: `artifacts/ltp-permissions/results/podish-tmpfs.json` and
`podish-hostfs.json`. Native baseline: `results/native.json`.
