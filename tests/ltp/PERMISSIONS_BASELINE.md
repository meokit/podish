# LTP permission / identity batch — tmpfs backend

Date: 2026-11-12
Manifest: `tests/ltp/cases-permissions.json` (71 cases)
LTP: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`
Branch: `fix/ltp-permission-semantics`

> BusyBox image update (2026-11-15): `access02` now passes with the static
> BusyBox `/bin/sh`; the current all-batch result is **71/71 PASS**. See
> [BUSYBOX_IMAGE.md](BUSYBOX_IMAGE.md).

## Results

| Backend | PASS | FAIL | BROKEN |
| --- | ---: | ---: | ---: |
| Native Linux, root (WSL) | 71 | 0 | 0 |
| Windows Podish, hostfs (before) | 38 | 27 | 6 |
| Windows Podish, tmpfs (before fixes) | 62 | 9 | 0 |
| **Windows Podish, tmpfs (after fixes)** | **70** | **1** | **0** |

After the fixes: 507 assertions passed, 4 failed, 0 broken/skipped. The only
failing binary is `access02`, whose four `X_OK` assertions execute a
`#!/bin/sh` script; the minimal test image has no `/bin/sh`, so they fail with
`ENOENT`. The other 12 access assertions pass. This is a test-asset dependency,
not a permission-check result. Keep it visible until a real i386 shell asset is
added or the case is moved to a separate environment-dependent manifest.

The native baseline and Podish use the same static i386 ELF hashes. The tmpfs
image is generated with explicit modes/UID/GID and imported through the CLI;
each case runs in its own container and root. Do not create this tar with
Windows `tar`, which commonly loses executable bits.

## Fixes made

### 1. chown set-ID clearing

`DacPolicy.ApplySetIdClearOnChown` now follows Linux semantics:

- setuid is cleared on chown;
- setgid is cleared for group-executable non-directory inodes;
- setgid is preserved for non-group-executable regular files;
- setuid/setgid are preserved on directories, which is required for setgid
  inheritance;
- the early return for unchanged UID/GID was removed, because an explicit
  superuser chown to the same owner still applies the set-ID clearing rules.

This fixes `chown02`, `fchown02`, and `mkdir02` (parent directory setgid is no
longer destroyed by the setup chown).

### 2. setreuid / setregid saved-ID handling

`CredentialService.SetReUid` and `SetReGid` now:

- validate both requested IDs against the original credential before mutating
  either one, so `setreuid(other, old_ruid)` works as an unprivileged swap;
- update the saved ID only when the real ID changes or the effective ID changes
  away from the old real ID, matching Linux's saved-set-ID rule.

This fixes all `setreuid05` assertions. The symmetric setregid path was corrected
as well; existing setregid tests remain green.

### 3. setgroups bounds and error precedence

`SysSetGroups` no longer caps at 1024. It accepts up to `NGROUPS_MAX` (65536),
then copies the user array. A full-size list with an invalid pointer now reaches
the copy and returns `EFAULT`, while counts above the Linux limit still return
`EINVAL`. This fixes `setgroups03` and `setgroups04`.

### 4. O_NOATIME

Added `O_NOATIME` and the ownership/capability check: an unprivileged caller
that is not the file owner gets `EPERM`. This fixes `open02`.

### 5. chroot error ordering

`SysChroot` now resolves the path and checks search/execute permission before
reporting the capability failure. An unprivileged user without search permission
now sees `EACCES`, matching Linux, instead of unconditional `EPERM`. This fixes
`chroot04`.

## Verification

- Core LTP batch: **50/50 PASS** after the changes.
- Permission LTP batch: **70/71 PASS**, only the documented `access02` shell
  dependency remains.
- C#: **1777 passed, 0 failed, 1 pre-existing skip** (Fiberish.Tests 1696,
  SilkFS 13, Wayland 19, Pulse 49).
- New/updated C# coverage:
  - chown set-ID clearing for files, directories, symlinks and non-group-
    executable files;
  - unchanged-owner chown;
  - saved-UID preservation and setreuid prevalidation;
  - setgroups Linux limit / EFAULT precedence;
  - O_NOATIME unprivileged access;
  - chroot EACCES-before-EPERM ordering.

## Reproduce

The permission manifest defaults to tmpfs (`"podishFsBackend": "tmpfs"`), so no
backend flag is required:

```powershell
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-permissions `
  --manifest tests/ltp/cases-permissions.json
```

Reports: `artifacts/ltp-permissions/results/podish-tmpfs-fixed.json`.
Historical hostfs and pre-fix tmpfs reports are retained alongside it.
