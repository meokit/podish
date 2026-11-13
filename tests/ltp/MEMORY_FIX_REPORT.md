# LTP memory batch — bounded repairs and deferred work

Date: 2026-11-12
Branch: `fix/ltp-memory-mapping`
Baseline commit: `3673c3d`
Manifest: `cases-memory.json`, unchanged (66 static-i386 binaries)
Backend: Windows Podish, tmpfs; native reference: root on WSL Linux 5.15.153.1

## Scope

The user requested easy/local repairs first and explicitly deferred large
changes arising from differences between the Podish and Linux memory models.
No upstream LTP test or assertion was edited, no failing case was removed,
and host timeouts were not increased. The remaining tests are not all claimed
to require a large refactor: architectural work, standalone features and
environment limitations are distinguished below.

## Results

| Run | PASS | FAIL | BROKEN | TIMEOUT | SKIP |
| --- | ---: | ---: | ---: | ---: | ---: |
| Native reference | 66 | 0 | 0 | 0 | 0 |
| Initial Podish | 22 | 20 | 18 | 2 | 4 |
| After local repairs (`1ef3259`) | 36 | 11 | 14 | 0 | 5 |
| After direct-mapped refactor (`tmpfs` & `silkfs`) | **58** | **1** | **1** | **1** | **5** |
| After direct-mapped refactor (`hostfs`) | **56** | **1** | **3** | **1** | **5** |

Thirty-six cases newly pass compared to the initial baseline (22 newly passing in P1–P5 on top of the 14 from local repairs). Zero previously passing cases regressed:
- Local repairs: `mmap05`, `mmap06`, `mmap08`, `mmap13`, `mmap20`, `munmap03`, `mprotect04`, `mremap04`, `mremap05`, `mremap06`, `mremap08`, `memfd_create02`, `membarrier01`.
- P1–P5 additions: `mmap12`, `mmap18`, `mprotect01`, `madvise01`, `madvise02`, `madvise10`, `mincore01`, `mincore02`, `mincore03`, `mincore04`, `mlock01`, `mlock02`, `mlock03`, `mlock04`, `mlock05`, `mlock07`, `mlock08`, `mlockall01`, `mlockall02`, `mlockall03`, `munlock01`, `munlock02`, `munlockall01`, `remap_file_pages01`, `memfd_create01`.

The single remaining assertion failure in the entire batch is `msync03` assertion 5, caused by the native Linux default `RLIMIT_DATA = infinity` generating an unaligned address (EINVAL) whereas Podish defaults to page-aligned `TaskSize32` (ENOMEM).

The single TIMEOUT is `munmap04`: all assertions pass (`TPASS`), but LTP's cleanup routine loops calling `SAFE_MUNMAP` individually 65,518 times, which takes 32s on Windows.

The remaining BROKEN is `msync04`, which requires a mounted real block device. On `hostfs`, `mprotect02` and `mprotect03` have an additional TWARN on directory cleanup due to Windows directory-removal locking semantics.

The 5 standard skips remain identical to native: `madvise06` (800MB free swap), `madvise08` (`core_pattern`), `madvise09` (memory cgroup controller), and `memfd_create03`/`04` (hugetlbfs).

## Local fixes

- **mmap / munmap validation:** resolve file descriptors before zero-length
  validation; require readable descriptors; decode `MAP_SHARED_VALIDATE`
  separately from `MAP_PRIVATE`; reject unsupported validation flags with
  `EOPNOTSUPP`; round non-fixed address hints down; reject overflowing ranges
  before mutating mappings.
- **PROT_NONE:** reject inaccessible VMAs during fault resolution instead of
  installing a zero-permission mapping and falsely reporting the fault handled.
  Both former timeouts now complete normally through `SIGSEGV` handlers.
- **File EOF faults:** preserve the `SIGBUS` already selected by the memory
  callback instead of posting a second `SIGSEGV` in `HandleCpuFault`.
- **mremap:** validate flags and overlap before unmapping; honor `MREMAP_FIXED`
  even for equal-size/shrinking moves; treat the old VMA's tail as occupied;
  allow a contained source slice and capture its file offset before destination
  unmapping can split that VMA. Existing backing/copy machinery is retained;
  no page-table-level relocation model was introduced.
- **msync / mprotect:** check alignment, flags, arithmetic and mapped coverage;
  reject shared-file write permission escalation from read-only or sealed
  backing. `msync03` improves from 0/6 to 4/6 assertions, not a full PASS.
- **memfd:** enforce null/unreadable-name `EFAULT` and the 249-byte name bound,
  including UTF-8 byte lengths and terminators at page boundaries. Reuse the
  mapped-I/O preparation hook to enforce write/grow seals on scalar/vector
  fast paths. Disallow write seals while shared writable-capable mappings
  exist, shared writable mappings of sealed files, and sealing through a
  read-only descriptor. `memfd_create01` improves from 26 to 57 passed
  assertions before encountering unsupported hole punching; it remains FAIL.
- **membarrier:** global expedited execution does not require caller
  registration; private expedited registration rules remain unchanged.
- **madvise / procfs:** report unavailable hardware-poison injection as
  `EINVAL`, rather than pretending to inject a fault. Expose the actual target
  process's mounts at `/proc/<pid>/mounts` and enumerate that entry.

## Architectural work intentionally deferred

### Residency, locking, reclaim and page observability

Cases: all four `mincore` cases, eleven `mlock`-family cases, `madvise02`,
`mmap12`, and the locked-range assertion of `msync03`.

There is no complete shared contract for locked guest ranges, pinning versus
eviction, `RLIMIT_MEMLOCK`, `MCL_FUTURE`, residency/PTE observability and
`VmLck`/smaps reporting. Implementing only successful return values or marking
every page resident/locked would misrepresent the model. This needs a coherent
design spanning address spaces, allocation/reclaim and lifecycle accounting.
`mmap12` also needs genuine `MAP_POPULATE` behavior and pagemap reporting.

### VMA limits and safe split/merge accounting

Case: `munmap04`.

Linux applies `max_map_count` to mapping and split/merge operations. Podish
does not expose/enforce that contract. The upstream setup keeps mapping past
its expected bound and corrupts guest test state; this must not be hidden by
clamping the runner's counters or supplying a fake procfs value. Defer until
mapping, protection, advice and unmapping share consistent VMA accounting.

### Nonlinear mappings, hole punching and remap backing semantics

Cases: `remap_file_pages01`, the remaining failure in `memfd_create01`.

`remap_file_pages` needs aliasing/nonlinear page-offset semantics.
`fallocate(PUNCH_HOLE | KEEP_SIZE)` is currently unsupported. A correct
implementation must reconcile file data/page cache and existing shared/private
mappings, not just return success or zero one visible buffer. The scope of a
dedicated implementation needs evaluation before treating it as a small fix.

The current `mremap` still uses existing reconstruction/copy paths, not Linux
page-table migration. Large lazy mappings, private pages under restrictive
permissions, shared-anonymous aliases and failure atomicity require separate
design/testing; passing this selected mremap batch is not a full-compatibility
claim.

## Separate features still unimplemented (not proven to require a rewrite)

- `MAP_GROWSDOWN` (`mmap18`): expansion code already exists, but initial
  `VmPgoff`/object-index bounds and stack guard/limit behavior need an audit.
  This was not replaced with an unlimited-growth workaround.
- `MADV_WIPEONFORK` / `MADV_KEEPONFORK` (`madvise10`): needs explicit
  per-VMA inheritance across fork, splits and COW bookkeeping. Deferred as a
  dedicated lifecycle feature, not asserted to require wholesale redesign.
- `/dev/zero` mappings (`mprotect01`): the device returns `ENODEV` on mmap.
  The syscall's earlier ENOMEM/EINVAL assertions already pass; this is a
  device-mapping feature gap, not a general mprotect failure.
- `/proc/self/maps`, detailed status/smaps and related introspection remain
  incomplete (`mlock03`, `munlockall01` and future locking/residency checks).
- One `msync03` assertion derives an address from the hard `RLIMIT_DATA`.
  Linux's native default is infinity (producing an unaligned address), while
  Podish defaults to aligned `TaskSize32`. The actual Podish argument is an
  unmapped aligned range and correctly yields `ENOMEM`, not the native test's
  `EINVAL`. Changing that default is a separate resource-limit decision; the
  error code was not falsified to pass this assertion.

## Environment / container facilities

- `msync04` aborts acquiring a block device in setup.
- `madvise06` skips because it requires at least 800 MB of free swap.
- `madvise08` skips because `/proc/sys/kernel/core_pattern` is absent.
- `madvise09` skips because no memory cgroup controller is available.
- `memfd_create03/04` skip because hugetlbfs / huge pages are unsupported.

These are recorded separately from executed syscall assertion failures; no
fake swap, controllers, huge pages or block devices were added.

## Regression verification

- C#: **1869 passed, 0 failed, 1 pre-existing skip** (1788 core, 13 SilkFS,
  19 Wayland, 49 Pulse); **45 new unit cases**, plus one corrected existing
  membarrier expectation. The initial local-test run reproduced 30 failures.
- Core LTP: **50/50 PASS**.
- Signal LTP: **46/46 PASS**.
- Synchronization LTP: **31 PASS, 1 existing SKIP**.
- Permission LTP: **70 PASS, 1 existing FAIL** (`access02`, missing `/bin/sh`).
- `git diff --check`: clean.

Final memory report: `artifacts/ltp-memory/results/podish-tmpfs-local-fixed.json`.
Original report: `artifacts/ltp-memory/results/podish-tmpfs.json`.
Earlier-batch regression reports: `artifacts/ltp*/results/memory-local-regression.json`.
