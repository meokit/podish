# Static BusyBox guest image for LTP

Date: 2026-11-15
BusyBox: 1.35.0, static i386 musl
Source: `https://busybox.net/downloads/binaries/1.35.0-i686-linux-musl/busybox`
SHA-256: `27e409705b29937b9a6105038d9c2e3eb8dc3e2c8d606d22872dbde7f47c000f`
LTP: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`

## Purpose

The LTP images previously contained only the test binary and BusyBox was absent.
Cases that shell out to `cp`, `sh`, `true`, `ip` or `ifconfig` therefore failed
before reaching their syscall assertions. This image installs one static i386
BusyBox and creates applet links for its full command set, so those cases can
execute their real test logic.

The binary is not committed. The pinned asset is downloaded and verified by
`tests/ltp/build_busybox_image.py`; the checked-in `busybox-applets.txt` records
the applet names for deterministic symlink creation.

## Image layout

- `/bin/busybox`: static i386 BusyBox 1.35.0 (musl), mode `0755`.
- `/bin/<applet>`: symlink to `busybox` for all 402 applets, including `sh`,
  `cp`, `true`, `false`, `ifconfig`, `ip`, `route`, `mount`, `umount`, `mknod`,
  `chattr`, `setfattr`, `dd`, `nc`, `ping`, `ps`, `kill`, `sleep`, `find`,
  `grep`, `sed`, `awk`, `tar`, `gzip` and `wget`.
- `/sbin/<applet>`: links for the 33 system-oriented applets, including
  `ip`, `ifconfig`, `route`, `mount`, `umount`, `mknod`, `chattr`, `setfattr`,
  `sysctl`, `mdev`, `init` and the module/mount helpers.
- `/etc/passwd`, `/etc/group`, `/etc/nsswitch.conf`: the same LTP test users
  used by the runner.
- `/tmp`, `/dev`, `/proc`, `/run`, `/opt/ltp/testcases/bin`: normal guest
  directories.
- Guest `PATH`:
  `/opt/ltp/testcases/bin:/bin:/sbin:/usr/bin:/usr/sbin`.

`build_image_tar` writes the symlinks directly into the tar, so Windows hosts do
not need symlink privileges. The runner also supports per-case `resources`
(such as `pipe2_02_child`) and places them in `/opt/ltp/testcases/bin`.

## Build and use

```powershell
# Download/verify BusyBox and build artifacts/ltp-busybox/rootfs.tar
python tests/ltp/build_busybox_image.py

# Optional: import the standalone base image
python tests/ltp/build_busybox_image.py --import --reference localhost/podish-ltp-base:latest
```

The LTP runner automatically uses `artifacts/ltp-busybox/busybox` when it exists.
Use `--busybox <path>` or `LTP_BUSYBOX=<path>` to override it. The image is
embedded in every `tmpfs`/`silkfs` run; `hostfs` runs use the older local-rootfs
mode and do not include BusyBox.

## All-batch status with BusyBox

All selected manifests were run on Windows Podish with the BusyBox image,
60-second host timeout, and reports in `artifacts/ltp-verification/`.

| Batch | Native | Podish before | Podish with BusyBox | Notes |
| --- | ---: | --- | --- | --- |
| basic (`tmpfs`) | 50 PASS | 50 PASS (`hostfs`) | 49 PASS / 1 PARTIAL | `read02` skips its two `O_DIRECT` variants on tmpfs; hostfs is 50/50 after the `wait402` fix. |
| permissions | 71 PASS | 70 PASS / 1 FAIL | **71 PASS** | `access02` now passes with `/bin/sh`. |
| sync | 32 PASS | 31 PASS / 1 SKIP | 31 PASS / 1 SKIP | `futex_wake04` skips on the `hugetlbfs` requirement. |
| signals | 46 PASS | 46 PASS | 46 PASS | No command dependency. |
| memory | 66 PASS | 59 PASS / 1 FAIL / 1 BROKEN / 5 SKIP | 59 PASS / 1 FAIL / 2 BROKEN / 4 SKIP | `msync03` remains the known `RLIMIT_DATA` difference; one `madvise` skip/broken classification moved. |
| net-io | 73 PASS | 63 PASS / 5 PARTIAL / 4 BROKEN / 1 SKIP | **64 PASS** / 5 PARTIAL / 3 BROKEN / 1 SKIP | `sendmsg01` now passes. |
| fs-fd | 115 PASS | 106 PASS / 1 PARTIAL / 8 SKIP | 106 PASS / 1 PARTIAL / 8 SKIP | No change. |
| proc-time | 98 PASS | 89 PASS / 2 PARTIAL / 1 FAIL / 4 BROKEN / 2 SKIP | **90 PASS** / 2 PARTIAL / 1 FAIL / 3 BROKEN / 2 SKIP | `setrlimit04` now passes with `/bin/true`. |
| pipe | 18 PASS | 17 cases: 14 PASS / 1 FAIL / 2 BROKEN | **18 PASS** | `pipe2_02` passes after the staged exec/register-state reset. |
| ipc (deferred) | 39 PASS | 4 PASS / 6 FAIL / 26 BROKEN / 3 SKIP | 4 PASS / 6 FAIL / 26 BROKEN / 3 SKIP | SysV message queues remain unimplemented. |

## Newly reached assertions

### Fixed by the image

- `access02`: previously failed because `/bin/sh` was missing; now 16 assertions
  pass.
- `setrlimit04`: previously `TBROK` because `/bin/true` was missing; now passes.
- `pipe2_02`: the resource copy now succeeds through BusyBox `cp` and `sh`;
  the later exec/exit crash is fixed by the register-state change below.

### Exposed and then fixed in the review round

- `sendmsg01`: now passes after validating `MSG_OOB` for datagram/Unix sockets
  (`EOPNOTSUPP`) and mapping an unreadable control-message pointer to `EFAULT`.
- `wait402`: now passes after reporting a realistic `pid_max` (4194304) and
  keeping `wait4(INT_MIN)` as the separate `ESRCH` case.
- `pipe12`: `FIONREAD` is implemented and nonblocking large writes fill the
  remaining pipe capacity.
- `pipe15`: `pipe-user-pages-soft` and `pipe-user-pages-hard` are exposed.
- `fsync03`: `fsync`/`fdatasync` on pipes, Unix sockets and FIFOs return
  `EINVAL`.
- `pipe2_02` and static glibc programs launched through BusyBox `sh`: `execve`
  now builds the replacement image in a private address space, clears inherited
  GPR/EFLAGS/FS/GS state, terminates sibling threads and commits only after the
  new image is fully loaded. Failed exec leaves the old image, CLOEXEC
  descriptors, signal dispositions and altstack untouched.

## Remaining non-command limitations

The image intentionally does not solve block-device, mount-namespace or
netlink requirements:

- BusyBox `ip` needs `AF_NETLINK`, which Podish does not implement. `sendmsg01`
  reaches its assertions through the BusyBox `ifconfig` fallback and now passes;
  other netlink-dependent tools remain limited.
- Tests that need a real block device, `chattr` on a non-tmpfs filesystem, or
  mount operations still report `TCONF`/`SKIP` as before.
- `getfattr` is not present in this BusyBox build; `setfattr`, `chattr` and
  `lsattr` are.

## Verification

```powershell
python tests/ltp/build_busybox_image.py
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-permissions \
  --manifest tests/ltp/cases-permissions.json --timeout 60 \
  --output artifacts/ltp-verification/busybox-permissions.json
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-proc-time \
  --manifest tests/ltp/cases-proc-time.json --timeout 60 \
  --output artifacts/ltp-verification/busybox-proc-time.json
```

Reports: `artifacts/ltp-verification/busybox-*.json`.
Base image tar: `artifacts/ltp-busybox/rootfs.tar`.
