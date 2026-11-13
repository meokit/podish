# Direct-mapped memory compatibility: implementation plan

Date: 2026-11-12
Status: implementation authorized on 2026-11-12; P0 starts first.
Working branch: `fix/ltp-memory-mapping`; prior local repairs committed as `1ef3259`.

## Goal and scope

Preserve hostfs/SilkFS mappings backed directly by the host OS file cache while
implementing Linux application-visible memory semantics. Do not introduce a
mandatory second buffer cache for ordinary shared file pages.

Companion: [hostfs/SilkFS test plan](../tests/ltp/MAPPED_FILESYSTEM_TEST_PLAN.md).
Current implementation: [memory-management.md](memory-management.md),
[MMU lifecycle](mmu-lifecycle-model.md).
Current tmpfs results: [memory fix report](../tests/ltp/MEMORY_FIX_REPORT.md).

The user authorized committing prior repairs and starting refactoring/testing.
Keep baseline capture and new semantic fixes separate. The prior repair set is
preserved in `1ef3259`; do not quietly fold it into a new test baseline. Work
starts on the dedicated repair branch without merging or pushing. Promotion of
the baseline to `main` and a subsequent repair branch must preserve the earlier
baseline-first workflow and requires approval for that integration. Each
milestone should be reviewable independently.

## Non-negotiable invariants

1. Shared regular-file mappings use mapped host-cache leases on supported hosts.
   A copied fallback must be explicit, diagnosed and separately tested.
2. Private writes and fork COW may allocate independent pages. Zero-copy is not
   a requirement for private modifications, anonymous memory or import buffers.
3. Existing `AddressSpace`, `AnonVma`, rmap, owned backing handles and the native
   external-page MMU model remain the starting point, not disposable scaffolding.
4. A lease keeps a view/pointer alive; an internal pin protects managed lifetime;
   an OS lock guarantees host RAM residency. None substitutes for another.
5. Native MMU entries and `ProcessPageManager` are installed-mapping caches,
   not a new authoritative owner of memory contents. Durable semantic state
   must survive native mapping invalidation.
6. Guest page size remains 4 KiB. Host page and mapping-window granularity are
   separate. Reference counts and locks use the actual host coverage required.
7. Never fabricate residency, physical page numbers, dirty flags, swap, memory
   controllers or successful locks to satisfy a test.
8. Preserve upstream tests, assertions, case selection and timeouts. Unsupported
   facilities and partial assertion coverage remain visible, not green XFAILs.

## Target state model

Use four related records, not four copies of page data:

- **VMA semantic state:** range, effective and maximum allowed permissions,
  backing offsets, lock policy, fork policy, stack bounds and resource charges.
- **Logical owner/page state:** stable file object plus page offset, or concrete
  anonymous owner plus index; dirty/writeback state, COW and invalidation state.
- **Installed guest binding:** guest page address, owner/index and currently
  leased pointer, plus the effective MMU permissions and synchronization state.
- **Host view state:** live view identity/generation, address/length, leases,
  native lock coverage and supported host operations.

A logical file-page key should be based on a retained file/inode identity and
page index, not a pathname or pointer. Use backing incarnation/version checks
where objects can be replaced or cached host information becomes stale. A view
generation changes when a view is replaced; it must not indiscriminately create
a different logical file page on every resize or protection change.

The pointer-keyed slots described by [hostpage-slot-ownership-design.md](hostpage-slot-ownership-design.md)
remain useful for one **live host view/binding**. This plan narrows their identity
scope: two pointers can refer to the same file-cache page, and a reused address
must not resurrect old logical state. Do not replace the entire slot store just
to add file-page identity. Preserve its short-lived metadata access discipline.

### Host capability contract

Extend `IFilePageBackend`, or add a narrowly scoped host-memory service alongside
it, for residency queries, prefetch, prefault, native lock/unlock and advisory
cache release. API names and placement are to be settled in P1, not preselected
as a new general-purpose abstraction layer.

- Query results distinguish known resident/nonresident from unavailable/unknown
  internally, identify their scope and do not fault pages merely to inspect them.
- Linux native `mincore` can assist file residency queries. Windows working-set
  queries are not a complete global file-cache oracle.
- `mincore` has no public unknown bit: a provider must implement the selected
  guest semantic contract, or report an explicitly documented unsupported path;
  it must not silently encode unknown as zero or mark every mapped page resident.
- Document guest modeled residency separately from host physical residency.
  Allocating a view, holding a pointer or releasing a window proves neither.
- Native pin failure, partial acquisition and release failures have explicit
  handling. Guest limits and host working-set/privilege limits are independent.
- `Trim` reports released view coverage separately from actual RAM reclamation.

## Steps

- [x] P0 — establish hostfs/SilkFS baselines and structural test observability.
- [x] P1 — separate logical page identity, view leases and host capabilities.
- [x] P2 — unify VMA edits and reference-preserving remap operations.
- [x] P3 — unify file mutation, invalidation and writeback error handling.
- [x] P4 — implement prefault, residency and real lock lifecycles.
- [x] P5 — integrate advice, inheritance and guest procfs projections.
- [ ] P6 — close the cross-backend regression and compatibility report.

### P0 — baseline and harness, before semantic refactoring

Changes:

- Inventory and retain existing Hostfs/SilkFS tests listed in the companion plan.
- Add a reusable backend fixture without rewriting existing tests in bulk.
- Add SilkFS image execution to `tests/ltp/run.py`; the CLI supports it, but the
  runner currently accepts only hostfs/tmpfs and hardcodes tmpfs image execution.
- Verify each test's data mount/backing, not merely the selected root backend.
  Record `/tmp`, `/dev/shm` and test file routing independently.
- Provide scoped diagnostic hooks for provider choice, leased view range,
  COW/copy counts, lock operations and reference snapshots. Hooks must not alter
  syscall results. Do not rely on process-wide RSS to prove zero-copy.
- Collect Windows hostfs/SilkFS/tmpfs and Linux hostfs/SilkFS baselines where
  available, keeping unavailable hosts explicit. Use identical guest binaries.

Exit: T01–T03 can prove the selected direct backend is actually exercised; runner
tests cover backend argument/image routing and unchanged classification rules;
immutable baseline reports exist before implementation fixes. New failing tests
are evidence, not a reason to edit upstream or claim a baseline is already green.

### P1 — identity, lease and capability boundaries

Depends on P0. Main areas: `HostPageRef.cs`, `BackingPageHandle.cs`,
`OwnerPageSlots.cs`, `LinuxMemoryObjects.cs`, `IFilePageBackend.cs`,
`WindowedMappedFilePageBackend.cs`, and both mapped inodes.

Changes:

- Define logical owner/index and host-view identity lifetimes, including path
  rename, hard-link aliases, replacement and orphaned-but-mapped files.
- Audit old/new read-only and writable windows, retired views and address reuse;
  shared logical state cannot split merely because a pointer changes.
- Specify capability and error contracts above. Add fake providers for controlled
  failure tests plus native provider probes; neither replaces real-host tests.
- Define lock acquisition order, scheduler safe points and in-flight user-buffer
  lease behavior before adding new invalidation or OS-lock operations.

Exit: T01–T03, T06, T11 and T18 show no stale/reused identity, premature release
or leaked retired views; shared file faulting introduces no duplicate data buffer.
Retain existing native MMU attachment and external ownership invariants.

### P2 — VMA edits, limits and binding migration

Depends on P1. Main areas: `VmArea.cs`, `VMAManager.cs`,
`ProcessPageManager.cs`, `ProcessAddressSpaceSync.cs`, memory syscalls.

Changes:

- Share range arithmetic, edit planning, split/merge attribute preservation and
  map-count reservation across mmap/munmap/mprotect/madvise/mremap.
- Implement actual `max_map_count` policy and matching procfs reporting. Count
  guest VMAs, not host windows, and test split failures near the limit.
- Move mremap's existing owner/page references without reading or copying guest
  contents; preserve COW, holes, restricted permissions and shared-anon aliases.
- Retarget rmap and native mappings across all attached engines; preserve TB
  coherence, maximum permissions, locks and inheritance flags through edits.
- Implement `remap_file_pages` initially through VMA/file-offset remapping;
  evaluate Linux's current emulation behavior before adding a nonlinear index.
- Audit existing grow-down indexing, guard gap and stack limits in this model.

Internal consistency is transactional where possible, but do not invent a public
all-or-nothing guarantee for every syscall: Linux permits partial effects for
some range operations. Native differential tests specify those effects. Reject
invalid overlapping remaps before their destructive steps.

Exit: T04–T07, T14–T15 and T18; selected mremap tests stay passing; `munmap04`
cannot escape a real VMA limit. Large lazy/PROT_NONE moves preserve content and
allocate no content copies proportional to the moved address range.

### P3 — file mutation and writeback protocol

Depends on P1/P2. Main areas: `Structs.cs`, `Hostfs.cs`,
`SilkFileSystemAdapter.cs`, mapped backend and address-space synchronization.

Changes:

- Generalize existing resize suspension into explicit coordination of affected
  ranges: quiesce users, capture dirty state, retire/release views as needed,
  mutate backing, reconcile actual size/version, invalidate and restore bindings.
- Define guest truncate, extension, partial EOF-page zeroing and hole-punch
  semantics against native Linux. Preserve private data where Linux does; do
  not assume every private page survives truncation beyond EOF.
- Never return an apparently restored old file state after an irreversible host
  mutation succeeded. Failure recovery must reconcile actual backing/metadata.
- Track guest dirty, host writeback and durable completion distinctly. Preserve
  dirty state on failure and propagate `SyncCachedPages`/flush errors to callers
  instead of discarding them.
- Audit fsync, fdatasync, msync and close separately. Do not add forced durable
  flushing on every close or shared write as a zero-copy/coherence workaround.
- Scope unsupported external-host operations honestly; SilkFS direct editing of
  repository internals is not an ordinary supported guest file mutation API.

Exit: T08–T11, T16 and T18; correct SIGBUS rather than host-process access faults;
shrink/regrow does not revive old bytes or leak views; remount verifies SilkFS
data and metadata after completed sync. Injected failures preserve recoverability.

### P4 — population, residency and locking

Depends on P1/P2/P3 (resize must understand locked views). Main areas: fault
resolution, VMA attributes, host-memory providers, pressure and owner-page code.

Changes:

- Add a common prefault engine for `MAP_POPULATE`, mlock and applicable advice;
  distinguish readahead from completed faulting, including writable private COW.
- Implement mincore's anonymous/private/file-source selection and cross-process
  file-cache observation. A missing caller binding is not proof of absence.
- Implement mlock/munlock/mlockall/munlockall, `MAP_LOCKED`, limits, privileges,
  partial failures, overlap and future allocation behavior. Match Linux's
  differences between `MAP_LOCKED` and successful mlock guarantees.
- Aggregate native locks by host coverage without making guest per-mm charges
  disappear; preserve alias locks until their last relevant reference is gone.
- Integrate fork's non-inheritance of memory locks, COW lock transfer, mremap,
  resize, exit and every reclaim/unmap path. Handle pinned I/O users separately.
- Test large host pages using simulated geometry; native validation is required
  before claiming support on an actual larger-page host.

Exit: T12–T14 and T18; mincore02–04/mlock05 supported-host assertions use real
prefault/lock state; host failure cannot be reported as successful RAM locking;
unlocking one alias does not unlock another's live coverage.

### P5 — advice, inheritance and guest introspection

Depends on P2/P3/P4; implement in small feature-specific slices.

- Add WIPEONFORK/KEEPONFORK for private anonymous ranges, preserving policy on
  splits and descendants without touching unrelated file-cache pages.
- Implement MADV_FREE lazy discard and write-after-advice cancellation using
  genuine pressure/write tracking. Immediate zeroing is not equivalent.
- Route DONTNEED/REMOVE/WILLNEED and file advice through owner/mutation/provider
  contracts; honor locked-range rules and advisory vs mandatory effects.
- Generate maps/status/smaps/pagemap present and lock information from semantic
  state, not raw host addresses or only installed native translations.
- Keep actual PFNs/kpageflags, global drop_caches, swap and memcg support separate
  from these projections. Do not manufacture a virtual PFN solely for msync04.

Exit: T12–T15 plus relevant madvise tests; no fake physical introspection or
unrelated host-cache purge; unsupported facility reports remain unchanged in
meaning. Full madvise09 still requires a real memory-pressure/controller model.

### P6 — verification and compatibility boundary

Depends on P0–P5. Run all companion-plan tiers and earlier LTP batches. Publish
before/after per host/backend/capability, assertion coverage and structural
direct-mapping evidence. Record unrun cells, skips and native differences.

Exit:

- No previously passing case regresses on its original configuration.
- All mandatory mapped-file contract tests pass on Windows/Linux hostfs/SilkFS
  configurations actually claimed as supported, plus the tmpfs control.
- Direct-mapping invariants survive stress and remain separate from throughput
  measurements; do not claim completion solely from a higher LTP PASS count.
- No unbounded view/lease/owner/lock growth after repeated quiescent cycles.
- Review remaining compatibility boundaries, particularly Windows external file
  truncation, global file-cache observation and Linux-only kernel facilities.

## Out of this implementation series unless separately approved

- Full guest swap, memory-cgroup controller, hugetlbfs, block-device mounting or
  hardware memory-failure injection.
- Exact Windows reproduction of Linux host physical PFN/kpageflags semantics.
- Globally purging or pinning unrelated host processes' caches.
- Strong guarantees for unsupervised external edits of SilkFS repository files.
- Replacing native MMU ownership, the mapped backend or all metadata storage.

These boundaries do not justify deleting their LTP cases or changing expected
results. `msync03`'s default RLIMIT_DATA difference is a separate policy decision;
do not falsify EINVAL or silently change the limit merely to match the reference.

## Review and rollout

P0 is the first implementation task after authorization, not P4's syscall stubs.
Keep baseline capture, harness support and semantic fixes in separate commits.
For each phase: failing regression first, bounded implementation, targeted tests,
full affected C# projects, unchanged LTP memory batch and earlier-batch checks.
Use measured before/after results; no estimated PASS count or schedule is a
release guarantee. Commit/merge/push requires separate user authorization.
