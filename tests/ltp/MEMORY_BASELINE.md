# LTP memory management and mapping batch — first baseline

Date: 2026-11-12
Manifest: `tests/ltp/cases-memory.json` (66 cases)
Backend: Windows Podish, tmpfs image, guest root
Native reference: WSL Linux 5.15.153.1, root, i386 static executables
LTP: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`
Branch: `main` (baseline only; fixes go on `fix/ltp-memory-mapping`)

## Results

| Backend | PASS | FAIL | BROKEN | TIMEOUT | SKIP |
| --- | ---: | ---: | ---: | ---: | ---: |
| Native Linux | 66 | 0 | 0 | 0 | 0 |
| Windows Podish, tmpfs | 22 | 20 | 18 | 2 | 4 |

## Excluded before the comparison

- `madvise07` (needs `CONFIG_MEMORY_FAILURE`), `madvise12` (needs kernel 6.13):
  the reference kernel reports `TCONF`, so there is no comparable baseline.
  Both were removed from the manifest rather than counted as skips.

## Coverage

Groups: `mmap` (15), `munmap` (3), `mprotect` (5), `madvise` (9), `mremap` (8),
`brk` (2), `msync` (4), `mincore` (4), `memfd` (4), `mlock` (11), `remap` (1),
`barrier` (1).

## What the batch exposes

### 1. `mmap(2)` argument validation is incomplete

- `mmap06`: six cases expect `EACCES` for a file opened `O_WRONLY` when the
  mapping requests read access, and `EINVAL` for `MAP_FILE` without
  `MAP_SHARED`/`MAP_PRIVATE`. Podish accepts most of these mappings.
- `mmap08`: expects `EBADF` for a closed descriptor; Podish returns `EINVAL`
  because it checks the `fd == -1` sentinel before validating that the
  descriptor is still open.
- `mmap20`: expects `EOPNOTSUPP` for `MAP_SHARED_VALIDATE` combined with an
  invalid flag; Podish returns `EINVAL`.
- `mmap12`: fails because `/proc/self/pagemap` is missing, so the test cannot
  confirm that `MAP_POPULATE` populated the pages.

### 2. `mmap(2)` with `PROT_NONE` does not fault

`mmap05` and `mprotect04` map or protect a region with no access and then
touch it, expecting `SIGSEGV`. Podish never faults, so both cases hang until
the host timeout. The engine log shows the guest repeatedly re-entering the
same instruction, confirming the access silently succeeds.

### 3. `mmap(2)` of a file larger than the mapping

`mmap13` maps twice the file size and writes past the end of the file,
expecting `SIGBUS`. Podish raises `SIGSEGV` instead and the case is reported
as `TBROK`.

### 4. `MAP_GROWSDOWN` is not honored

`mmap18` expects a stack-like mapping to grow on demand. Podish kills the
child with `SIGSEGV` where the test expects success.

### 5. `munmap(2)` does not validate its arguments

- `munmap03`: expects `EINVAL` when the length is `SIZE_MAX` or the address is
  unaligned. Podish accepts the oversized range.
- `munmap04`: the test reserves the maximum number of VMAs and then expects
  `munmap` to fail with `ENOMEM`. Podish has no `max_map_count` accounting,
  and `/proc/sys/vm/max_map_count` is missing, so the case cannot even
  establish its premise.

### 6. `mprotect(2)` error paths

`mprotect01` expects `ENOMEM` for an unmapped address and `EACCES` when
adding write access to a read-only file mapping. Podish's `mprotect` on an
unmapped range returns success, and mapping `/dev/zero` fails with `ENODEV`
because the character device does not support `mmap`.

### 7. `madvise(2)` advice coverage and semantics

- `madvise01`: the case passes on Linux but is reported as `BROKEN` because
  Podish's assertion count differs from the native count (the native run
  skips `MADV_HWPOISON`, Podish does not).
- `madvise02`, `mincore02`, `mincore04`: need `mlock(2)`, which Podish does
  not implement.
- `madvise09`: needs `/proc/self/mounts`, which Podish does not expose.
- `madvise10`: `MADV_WIPEONFORK` and `MADV_KEEPONFORK` are not implemented;
  the child observes stale contents where the test expects zeros.

### 8. `mremap(2)` semantics

- `mremap04`, `mremap09`: expect `ENOMEM` when the region cannot grow in
  place. Podish succeeds.
- `mremap05`: expects `EINVAL` for `MREMAP_FIXED` without `MREMAP_MAYMOVE`
  and for overlapping source and destination ranges. Podish accepts both.
- `mremap08`: expects `MREMAP_FIXED` to move the mapping and to return the new
  address. Podish returns the old address and leaves the old mapping in place.
- `mremap06`: aborts because the first `mremap` call itself fails.

### 9. `msync(2)` accepts invalid arguments

`msync03` expects `EBUSY` for `MS_INVALIDATE` on a locked range, `EINVAL` for
an unaligned address, conflicting flags, or unknown flag bits, and `ENOMEM`
for an unmapped range. Podish's `msync` always returns success.
`msync04` additionally needs a block device and aborts in setup.

### 10. `mincore(2)` is not implemented

All four `mincore` cases fail with `ENOSYS`. The case expects `EINVAL` for an
unaligned address, `EFAULT` for an invalid vector pointer, and `ENOMEM` for an
unmapped range.

### 11. The `mlock` family is not implemented

`mlock`, `munlock`, `mlockall` and `munlockall` all return `ENOSYS`. This
also breaks `mincore02`, `mincore04` and `madvise02`, which lock memory first.
`mlock02` additionally expects `ENOMEM` and `EPERM` based on
`RLIMIT_MEMLOCK`, and `munlockall01` expects `VmLck` in `/proc/self/status`.

### 12. `memfd_create(2)` validation

- `memfd_create01`: expects `write(2)` on a sealed descriptor to fail. Podish
  allows the write.
- `memfd_create02`: expects `EFAULT` for a null name. Podish accepts it.

### 13. `remap_file_pages(2)` is not implemented

`remap_file_pages01` expects `ENOSYS` on a kernel without the fix but a
working remap otherwise; Podish returns `ENOSYS` and then the case crashes
with `SIGSEGV`.

### 14. `membarrier(2)` is only partially implemented

`membarrier01` expects `membarrier(MEMBARRIER_CMD_GLOBAL)` to succeed.
Podish returns `EPERM` for the global expedited command.

### 15. Missing procfs files

Several cases need files Podish does not provide:
`/proc/self/pagemap` (`mmap12`), `/proc/self/mounts` (`madvise09`),
`/proc/self/maps` (`mlock03`), `/proc/sys/vm/max_map_count` (`munmap04`),
`/proc/sys/kernel/core_pattern` (`madvise08`), and `VmLck` in
`/proc/self/status` (`munlockall01`).

## Next work items

1. Validate `mmap(2)` arguments: `EACCES` for read access to a write-only
   descriptor, `EINVAL` for `MAP_FILE` without a sharing mode, `EBADF` for a
   closed descriptor, `EOPNOTSUPP` for `MAP_SHARED_VALIDATE`.
2. Enforce `PROT_NONE` on access so `mmap05` and `mprotect04` fault.
3. Raise `SIGBUS` rather than `SIGSEGV` for access past the end of a file
   mapping.
4. Implement `MAP_GROWSDOWN` growth.
5. Validate `munmap(2)` length and alignment, and implement `max_map_count`.
6. Return `ENOMEM`/`EACCES` from `mprotect(2)` where Linux does.
7. Implement `mincore(2)`.
8. Implement `mlock`/`munlock`/`mlockall`/`munlockall` with `RLIMIT_MEMLOCK`.
9. Complete `madvise(2)`: `MADV_WIPEONFORK`, `MADV_KEEPONFORK`, and the
   remaining advice values.
10. Correct `mremap(2)`: `MREMAP_FIXED` handling, overlap checks, and
    `ENOMEM` when the region cannot grow in place.
11. Validate `msync(2)` arguments and flags.
12. Validate `memfd_create(2)` names and flags, and honor `F_SEAL_WRITE`.
13. Add the missing procfs files listed above.

Do not remove LTP assertions, convert expected failures to skips, or widen the
host timeout to hide hangs.

## Reproduce

```powershell
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-memory `
  --manifest tests/ltp/cases-memory.json
```

Report: `artifacts/ltp-memory/results/podish-tmpfs.json`.
