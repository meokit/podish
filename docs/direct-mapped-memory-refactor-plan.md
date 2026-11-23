# Direct-mapped memory compatibility refactor

The November 12, 2026 plan preserved direct host-cache mappings for
hostfs and SilkFS while adding guest Linux memory semantics. The
implementation landed in `2f14ebc5`. This record keeps the phase boundaries
and acceptance criteria. For current behavior, see [guest memory management](memory-management.md)
and the [native MMU lifecycle](mmu-lifecycle-model.md).

The [backend test plan](../tests/ltp/MAPPED_FILESYSTEM_TEST_PLAN.md) defines
cases T01–T19. The [LTP fix report](../tests/ltp/MEMORY_FIX_REPORT.md) records the
original baseline, local repairs and the follow-up runs. The local repair
checkpoint was `1ef32592`.

## Mapping and lifetime requirements

- Shared regular-file faults use host-cache leases when the backend supports
  mapped windows. Diagnose and test buffered fallback as a distinct provider.
- Private writes and fork COW can allocate backing pages. Shared-file
  zero-copy coverage applies to the mapped provider.
- `AddressSpace` and `AnonVma` own page contents and claims. The native MMU
  and `ProcessPageManager` track installed bindings that can be invalidated
  and rebuilt.
- A view lease keeps a pointer valid. A managed pin protects backing lifetime.
  An OS lock covers host RAM residency. Count guest memory in 4 KiB pages and
  align native views and locks to the host geometry.
- Record actual residency, dirty state, lock operations and host failures.
  Keep upstream cases, assertions and timeouts intact so partial or missing
  coverage stays visible.

## Identity and host capabilities

| Record | Identity and state |
|---|---|
| VMA | Guest range and backing offsets, permissions, inheritance, lock policy, stack bounds and guest limits. |
| Logical page | File device, inode number and page index, or anonymous owner and index. Dirty state, COW and invalidation follow the owner. |
| Installed guest binding | Guest address, owner and index, leased pointer and the native mapping's effective permissions. |
| Host view | Backend window ID and coverage, refcount, retirement and native lock coverage. |

`FilePageIdentity` carries the logical file-page key. The pointer-keyed slot
table describes a live host page. A backend can change the pointer or use
more than one view for the same file page. View retirement and slot
generations keep later users of a reused address separate from prior holders.
See [file-page identity](../Fiberish.Core/Memory/FilePageIdentity.cs),
[slot metadata](../Fiberish.Core/Memory/HostPageRef.cs) and
[mapped windows](../Fiberish.Core/Memory/WindowedMappedFilePageBackend.cs).

`IFilePageBackend` supports lease acquire and release, prefaulting, residency
queries, native lock and unlock, flush and trim. Internally a residency query
distinguishes resident, nonresident and unknown. The guest `mincore` path
selects the page source and applies guest semantics. Inspecting a native
view should avoid faulting it in. Linux `mincore` and Windows working-set
queries have different scopes. A view's existence and the number of bytes
released by trim give no measure of reclaimed RAM. See
[backend contract](../Fiberish.Core/Memory/IFilePageBackend.cs).

## Completed phases

### P0. Baseline and backend routing

Commit `cef7b2ee` added the backend fixture, scoped diagnostics and runner
coverage. `tests/ltp/run.py` selects Podish storage with `--fs-backend hostfs`,
`tmpfs` or `silkfs`. Record the backing of the test file, `/tmp` and
`/dev/shm` before assigning results to a storage backend. T01–T03
use provider, window and copy evidence to verify direct mapping. RSS alone
cannot establish that a guest binding borrowed a host-cache page. See
[LTP runner](../tests/ltp/run.py).

### P1. File identity and view leases

Commit `ec9bbefd` separated logical file pages, backing leases and host
capabilities. T01–T03, T06, T11 and T18 cover retained file identity across
aliases and rename, orphaned mappings, permission changes, window retirement
and failure while a borrower still holds a view. A read-only and a writable
window can represent the same logical file page. Keep mapping and in-flight
I/O leases alive through view replacement.

### P2. VMA edits and reference-preserving remap

Commit `99814923` added `max_map_count` accounting, reference-preserving
`mremap` and VMA-based `remap_file_pages`. Range edits preserve backing
offsets, private COW, holes, permission ceilings and inheritance flags.
Retarget rmap and native translations while keeping the owner references
needed to recover page contents. Validate overlapping remaps before
destructive changes. Linux allows some range operations to have partial
effects. T04–T07, T14–T15 and T18 cover COW, nonsequential file offsets,
remap of lazy ranges, VMA limits and failure handling.

### P3. File mutation and writeback

Commit `dffae6d8` added the hole-punch and mapped-range mutation paths.
Capture dirty state, coordinate users of the affected range, retire views
where needed, apply the backing operation and reconcile the resulting file
size and bindings. Truncation invalidates ranges past EOF and retains private
data where the guest contract requires it. Return guest `SIGBUS` for a
fault past the backing. If a host mutation succeeds, recovery uses the
resulting file state even if a later step fails.

Track guest dirtiness, host writeback and durable completion separately.
Check error propagation for `msync`, `fsync` and `fdatasync`. Scope the
writeback operations to their requested ranges and handle dirty flags on
failure. T08–T11, T16 and T18 cover shrink, regrow, private COW, flushes,
supported external host operations and injected failures. Verify SilkFS
durability by reopening after a completed sync and checking the bytes,
size and metadata.

### P4. Prefaulting, residency and locking

Commit `3f7604a1` added `MAP_POPULATE`, residency queries and native lock
tracking. A resident file page can lack an installed translation in the
calling address space. Prefaulting writable private pages can require a
COW copy. The lock tracker aggregates references by host page coverage.
Per-address-space guest limits still apply to overlapping ranges and
future mappings. Propagate failures and roll back partially acquired host
coverage.

Review lock transfer on COW, remap, resize, unmap and exit, and lock
non-inheritance across fork. Distinguish managed I/O pins from lock claims.
T12–T14 and T18 cover provider results and alias lock release. Simulated
large-page geometry tests cover alignment arithmetic. They provide no
measurement on an actual large-page host.

### P5. Advice, inheritance and procfs

Commit `07048f9d` added memory advice and inheritance work with grow-down
stack and `/dev/zero` changes. `MADV_WIPEONFORK` resets selected private
anonymous pages in the child. `MADV_KEEPONFORK` clears that policy.
`MADV_DONTNEED` drops pages through the address-space path. `MADV_REMOVE`
uses the file mutation path on eligible tmpfs ranges. The syscall accepts
`MADV_FREE` for private anonymous ranges and returns success after validation.
The lazy-discard tracking from the plan remains pending. `MADV_WILLNEED`
also returns success after validation. Guest procfs uses managed range and
residency state for maps, status, smaps and pagemap. T12–T15 cover the
planned contracts. Full `madvise09` coverage needs a memory-pressure
controller. See [memory advice handling](../Fiberish.Core/Syscalls/SyscallHandlers.Memory.cs).

### P6. Verification and review

The [LTP fix report](../tests/ltp/MEMORY_FIX_REPORT.md) records 59 of 66
passes on Windows Podish for each of tmpfs and SilkFS, and 57 of 66 with hostfs.
The WSL native reference passed all 66. The report identifies remaining
failures, broken cases and skips. It covers the measured configurations.
The [backend matrix](../tests/ltp/MAPPED_FILESYSTEM_TEST_PLAN.md#execution-matrix-and-applicability)
lists additional host and provider combinations and the evidence needed
to claim support. Compare unchanged LTP manifests, check direct mapping
through leases and provider diagnostics, and check resource counts after
map, lock and unmap stress. Track skipped and unrun cases in the result.

## Compatibility boundaries

The refactor scope excluded guest swap, memory cgroups, hugetlbfs,
block-device mounts and hardware memory-failure injection. Linux physical
page numbers and `kpageflags` require host-specific support. `msync04` needs
a block-device fixture. `msync03` depends on the default `RLIMIT_DATA` and
the resulting test address. External Windows truncation and direct edits
to SilkFS repository internals need their own compatibility evidence.
