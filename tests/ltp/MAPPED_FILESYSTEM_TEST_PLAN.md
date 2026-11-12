# hostfs / SilkFS direct-mapping test plan

Date: 2026-11-12
Status: proposed coverage and acceptance gates, not tests already implemented.
Implementation sequence: [direct-mapped memory refactor plan](../../docs/direct-mapped-memory-refactor-plan.md).

## Purpose

Test both Linux-visible behavior and the architectural requirement that ordinary
shared file mappings use host-cache pages directly. Passing content checks alone
does not prove zero-copy; matching pointers alone does not prove physical identity.

The existing 66-case memory baseline is Windows/tmpfs, not hostfs/SilkFS evidence.
Do not relabel it. Preserve `cases-memory.json`, upstream sources/assertions,
binary hashes, timeout policy and partial-coverage classification.

## Existing coverage to retain and extend

- `Fiberish.Tests/VFS/HostfsPageCacheWritebackTests.cs`: shared writeback, peer
  engines/processes, runtime isolation, asynchronous flush/handle lifetime,
  external rewrite detection, tail shrink/regrow, pressure and mmap-held files.
- `Fiberish.Tests/VFS/SilkFsAdapterTests.cs`: shared writeback/remount, write after
  mmap, reclaim, truncate, metadata persistence and orphan lifetime until munmap.
- `Fiberish.Tests/Syscalls/OpenTruncateSyscallTests.cs`,
  `InodeOrphanLifecycleSyscallTests.cs`, `CloneThreadLifecycleTests.cs`,
  `MmapSupportTests.cs`, `MemoryLtpRegressionTests.cs`: syscall and mapping lifecycle.
- `Fiberish.Tests/Memory/HostMemoryMapGeometryTests.cs`,
  `MemoryPressureCoordinatorTests.cs`: geometry and reclaim foundations.
- `Fiberish.Tests/Podish/SilkFsLifetimeTests.cs`, `Fiberish.SilkFS.Tests`: storage
  lifetime and repository regressions; not by themselves an mmap compatibility suite.
- `Podish.PjdFs` demonstrates backend selection, not an existing memory runner.

Reuse these fixtures and assertions where practical. Do not first move/rewrite
all tests and obscure the behavioral diff. Suggested new shared fixtures/classes
below are proposals; they do not exist yet.

## Execution matrix and applicability

Mandatory target matrix for the completed implementation:

| Host | File backend under test | Purpose |
| --- | --- | --- |
| Windows, local NTFS | hostfs direct | primary mapped host-file behavior |
| Windows, local NTFS | SilkFS direct live files | primary storage adapter behavior |
| Linux, local filesystem (record type) | hostfs direct | Linux host provider and ABI comparison |
| Linux, local filesystem (record type) | SilkFS direct live files | adapter parity, metadata/remount |
| Windows and Linux | tmpfs | existing semantic control, not proof of direct file mapping |

Add overlay-silkfs separately after direct SilkFS: clean lower reads, copy-up,
shared writable faults and inode identity after copy-up. Do not count an overlay
run as direct SilkFS coverage when the tested page still comes from a lower layer.

Also test forced buffered fallback in C# with unsupported mapping geometry. It
is a negative/control configuration, never evidence that the direct path passed.
Network filesystems, WSL Windows-backed paths and unusual host page sizes are
additional declared configurations, not implied by local NTFS/ext4 coverage.

For every run record host OS/kernel, volume/filesystem, page/window geometry,
backend/capabilities, guest binary hashes and build revision plus dirty-tree
state or a source diff hash. An unavailable matrix cell is **not run**, not PASS.

### Verify where the data actually lives

The fixture must inspect the opened data inode, mount and backing provider.
Capture guest path/mount/fs type and backend diagnostics for representative
file faults. Rootfs selection or guest `statfs` alone is insufficient proof.

- Check the case's effective TMPDIR/workdir, `/tmp` and `/dev/shm` independently.
- A file created on automatically mounted tmpfs is tmpfs coverage even when the
  container root is SilkFS or hostfs. Anonymous/memfd cases are not reclassified
  as direct-host-file cases.
- Use a dedicated backend data directory for custom guest probes. Native Linux
  probes use an isolated local Linux scratch filesystem, not `/mnt/c`.
- Wrong routing in a targeted contract test is a harness failure, not a skip.

## Three complementary test tiers

### A. Managed structural and provider tests

Add a shared fixture such as `MappedFileBackendTestEnvironment` with hostfs and
SilkFS parameters, real backing paths, engine/mm construction, safe barriers,
scoped provider hooks and before/after reference snapshots.

Candidate classes: `MappedFileBackendContractTests`, `MappedFileMutationTests`,
`MappedFileLockingTests`, `MappedFileFailureTests` under existing VFS/Memory test
directories. Keep backend-specific persistence/metadata tests in their current
classes. Use actual native providers and deterministic fake-provider error tests.

### B. Static-i386 native/guest differential probes

Add an independently built probe suite under a proposed `tests/memory-mapping/`
directory, not inside the upstream LTP submodule. Reuse static-i386 toolchain
settings and record source/compiler/hash metadata.

Run each probe natively on Linux first and then on Podish with the same binary.
Use pipes/checkpoints and subprocess signal handlers, not sleeps, to order
fork, writes, remap and truncate. Output structured observations: bytes/hashes,
errno, child signal/status, mapping ranges and semantic counters where exposed.

Native Linux is the behavioral oracle for version-sensitive edge cases. Do not
assert behavior POSIX/Linux leaves unspecified, especially visibility of later
external writes through an unmodified MAP_PRIVATE mapping. Compare post-COW
isolation and well-defined shared synchronization separately.

### C. Unmodified LTP

Keep the full 66-case batch and add backend runs. Existing skips/failures remain
raw outcomes. Map each relevant failure to a contract ID, not a backend-wide
"expected failure" rule. Do not claim that all 66 cases exercise the selected FS.

## Contract cases and expected evidence

Each ID is a test family, expanded across backends and meaningful variants.

| ID | Scenario | Required observations |
| --- | --- | --- |
| T01 | direct shared fault | provider is native mapped; guest binding lies in a live leased view; no page-sized content allocation/copy for this fault |
| T02 | shared aliases and mmap vs I/O | two mappings/fds, threads and forked processes see synchronized writes; scalar/vector/pread/pwrite paths agree; visibility before msync is not confused with durability |
| T03 | geometry and tail | offsets around 4 KiB and window boundaries, unaligned byte I/O and partial EOF; correct page coverage and no hidden fallback |
| T04 | private COW and fork | private write does not modify file/shared alias/parent; source read remains direct where applicable; only modified private pages materialize except required prefault/COW work |
| T05 | mremap | equal/shrink/grow/fixed, occupied target, contained slices, lazy holes and restrictive protection; preserve references/data/offset and Linux failure effects |
| T06 | protection and executable aliases | PROT_NONE faults once, restore permissions, retain maximum rights/COW/seals; all engines synchronize and shared-code writers invalidate stale translated code |
| T07 | nonsequential file offsets | remap_file_pages ordering, repeated remaps and duplicate file-offset aliases; preserve shared visibility and correct rmap accounting |
| T08 | truncate/shrink/regrow | multiple active views, peer engine/process, partial tail and COW variants; native-matched zeroing/SIGBUS, no stale bytes or host crash |
| T09 | hole punching | aligned/partial-page ranges, dirty shared aliases and private COW, KEEP_SIZE and invalid/sealed modes; native-matched data and allocation semantics |
| T10 | flush and persistence | msync/fsync/fdatasync, peer writers and ranges; observe completed host calls/errors; fresh SilkFS mount checks data/size/metadata |
| T11 | identity and orphan lifetime | close fd while mapped, rename, hard links, unlink/recreate and overwrite rename; old mappings retain old object; new opens get correct object; final refs clean up |
| T12 | residency and population | untouched anonymous vs faulted pages, private source vs COW, child-populated file cache, MAP_POPULATE and guest pagemap present; query itself does not prefault |
| T13 | real lock lifecycle | overlap, aliases, limits/capabilities, future maps, fork non-inheritance, COW/remap/resize/exit; actual OS lock coverage and balanced releases |
| T14 | reclaim and advice | trim is not RAM eviction; locked pages protected; clean file refault correct; DONTNEED/REMOVE/WILLNEED/FREE obey distinct semantics |
| T15 | VMA/procfs/inheritance | max_map_count on split/merge, maps/status/smaps/pagemap projections, guard/stack growth, WIPEONFORK/KEEPONFORK descendants |
| T16 | external host operations | synchronized mapped writes, supported rewrite/reopen and path replacement; external truncate limits reported by platform; no repository-internal edit promise |
| T17 | overlay-silkfs | lower file identity, copy-up, old/new mappings and writable shared fault; record actual provider before/after copy-up |
| T18 | injected failures and retirement | acquire/map/lock/flush/resize/restore failure, cancellation/exit and retained I/O leases; no lost refs, dirty-state loss, stale binding or retry loop |
| T19 | bounded stress | repeated map/fork/split/remap/shrink/grow/unmap; reproducible seeds, barriers, checksums, quiescent resource accounting |
| T20 | allocation and performance | shared no-copy invariant; proportional COW; lazy remap allocation; window/metadata overhead, throughput and latency versus baseline |

### T01/T02: prove direct mapping without overclaiming

Take scoped allocation/copy/provider counters after fixture/file creation and
before the operation being asserted. Distinguish file data pages, private COW,
temporary I/O buffers and metadata allocations. A direct shared page fault must
not allocate a shadow file data page or copy its contents into one.

Verify the installed pointer is covered by the leased backend view and belongs
to its live generation. Use a second independently mapped host view to observe
synchronized shared writes before guest flush, not a potentially buffered reader.
Pointers can differ across aliases; equality is not a general assertion.
Combining lease provenance, content-copy accounting and coherent observations
is the architectural gate; a throughput number is not proof.

### T03/T08/T09: edge-size set

Cover file sizes 0, 1, 4095, 4096, 4097, window-size minus/at/plus one page;
offsets 0, one guest page, and both sides of a host/window boundary. Cover
multi-window files and nonzero mmap offsets. Use unique page patterns.

Specify partial EOF-page access separately from a whole page beyond EOF. Test
guest truncation with live shared/private/locked mappings and in-flight buffer
leases. Resolve native Linux expectations for private pages beyond EOF and
tail-cache quirks before encoding assertions; do not invent a stronger contract.

For hole punching, content zeroing alone is not proof of sparse deallocation.
Test logical bytes and supported allocation reporting separately; if the host
filesystem cannot provide the promised operation, record its actual error.

### T10: coherence, sync completion and durability are separate

- Coherence is checked before msync; a correct direct mapping need not wait for
  msync to expose bytes to another shared view.
- Record native view-flush and file-flush operations according to each syscall's
  documented completion contract. A stubbed success is not a durability test.
- Inject EIO/space errors and verify propagation, retryable dirty state and no
  use-after-close during asynchronous flush.
- After successful sync, fully dispose engines/mounts/repository sessions and
  reopen using a fresh environment; verify SilkFS bytes, exact size and metadata.
- This is restart/remount persistence testing, not a power-loss guarantee.
  Crash/power-failure storage testing requires a separate fault model.

### T12/T13: deterministic residency and lock evidence

- Reproduce mincore03 (untouched anonymous vs locked) and mincore04 (parent
  observes child-populated file cache) directly, with checkpoints.
- Query helpers must not fault the pages they are about to measure.
- Use native successful-lock coverage and controlled host lock failures, not
  only internal PinCount or host RSS; sample residency after successful locks.
- Test guest limit exhaustion independently of host privilege/working-set
  failure. Report prerequisite failure explicitly, never turn a failed lock
  into a success because a guest counter increased.
- No mandatory "file must be cold after fadvise/trim" assertion: these are
  advisory and releasing a view does not evict the OS cache.
- Forced OS working-set pressure is a separately isolated robustness test,
  not a timing-sensitive prerequisite for the ordinary deterministic suite.

### T18/T19: safe failure and concurrency testing

Hooks/barriers cover before/after view acquisition, pointer installation,
invalidation, backing mutation, restoration and writeback completion. Scope
hooks per runtime/provider; serialize unavoidable existing static test hooks
and restore them in finally blocks.

Inject partial native-lock acquisition, allocation/map failure, resize failure
before mutation, failure after backing mutation and flush/restore failure.
After irreversible changes, compare actual backing and metadata, not an assumed
rollback. Ensure suspended engines are restored or fail in a defined manner.

Run fixed seeds and bounded iteration counts. After quiescence, compare live
lease/window/owner/map/pin/native-lock counts with a warmed baseline, allowing
explicitly bounded reusable caches. Check retired as well as active windows.
Expected guest SIGBUS/SIGSEGV belongs in a child probe; a host test-process crash
is a failure. Keep stress subprocesses isolated from the developer's own files.

## LTP runner work required in P0

`run.py` currently accepts only `hostfs`/`tmpfs`, imports an image only for tmpfs,
and hardcodes `--fs-backend tmpfs` when launching image cases. CLI support for
SilkFS is not sufficient to claim runner support.

Plan the following changes without modifying upstream:

1. Accept direct `silkfs` explicitly and route it through isolated imported-image
   execution with the selected CLI backend. Preserve current defaults.
2. Retain fresh per-case containers and isolated run-local stores; ensure cleanup
   cannot delete still-mapped live files. Preserve failure logs and diagnostics.
3. Use distinct backend-specific output names/run IDs so hostfs/SilkFS/tmpfs
   reports do not overwrite one another. Share the immutable binary build and
   native oracle, not writable case data/repositories.
4. Record capability/mount/provenance evidence through a preflight helper or
   diagnostics channel, never by editing the upstream binary/assertions.
5. Extend `test_runner.py` with argument, image selection, command construction,
   artifact identity and partial/skip/error preservation tests. A routing test
   must prove the requested backend reaches the CLI.

Current supported hostfs entry point, after the existing assets/CLI are built:

```sh
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-memory --manifest tests/ltp/cases-memory.json --fs-backend hostfs --output artifacts/ltp-memory/results/podish-hostfs-baseline.json
```

The SilkFS equivalent is intentionally not presented as a currently working
command. Add it only after runner support is implemented and verified.

## LTP traceability and compatibility boundaries

| Cases | Contract IDs | Interpretation |
| --- | --- | --- |
| mmap file cases; mprotect; mremap | T01–T06, T08, T11 | ordinary ABI; preserve existing successes |
| mmap12 | T12, T15 | populate and guest present reporting; no real host PFN required |
| mmap18; munmap04 | T15 | existing stack-growth audit and actual VMA limits |
| mincore01–04 | T12 | argument rules, anon vs file cache, cross-process observations |
| eleven locking-family cases | T13, T15 | actual lock lifecycle and guest accounting/procfs |
| madvise02; locked portion of msync03 | T13–T14 | locked-range errors, not fake flags |
| remap_file_pages01 | T07 | nonlinear visible ordering can initially use VMA remapping |
| memfd_create01 | T09, T13 | memfd holes/seals; not evidence of hostfs data-path coverage |
| madvise09/10 | T14–T15 | anonymous lazy-free/inheritance; full 09 additionally needs memcg pressure |
| msync04 | T10, separately gated physical introspection | requires block-device fixture, PFN/kpageflags and O_DIRECT; no fake PFNs |
| madvise06 | separate Linux swap/memcg facility | not a direct-file-cache incompatibility |

Other known environment gaps (core_pattern, huge pages, block devices) retain
their raw status. A full-batch run still includes these cases. Scoped diagnostic
filters may accelerate development but do not replace the final unchanged batch.
Keep msync03's RLIMIT_DATA-derived argument difference explicit. Unsupported
Windows external truncate/global-cache observation is a platform boundary, not
permission to weaken a supported ordinary guest syscall test.

`munmap04` currently has corrupt harness assertion counters: retain raw evidence
and BROKEN status, do not aggregate its printed totals. Before a real VMA bound
exists, isolate runs and preserve watchdog/resource-limit termination as failure;
do not increase timeouts or fabricate max_map_count merely to get a baseline.

## Acceptance and reporting

For each phase, first run the relevant contract IDs, then affected C# tests,
the complete memory LTP batch per available target backend and earlier batches.
Keep permissions tests' Windows-hostfs mode limitations separate from memory
failures. The existing baseline has C# 1869 pass / 1 pre-existing skip and tmpfs
LTP 36 pass / 11 fail / 14 broken / 5 skip; these are historical, not fresh runs.

The final report includes:

- per-host/backend/case before/after status and assertion coverage;
- actual data mount and direct-provider evidence, not only rootfs settings;
- first failing observation/log, native result and capability/prerequisite notes;
- view/lease/owner/map/lock deltas and scoped file-copy/COW allocation counts;
- measured latency/throughput and metadata/view overhead using identical inputs;
- unrun cells, unsupported facilities and remaining policy decisions.

Mandatory structural target: zero shadow file-data copies for ordinary direct
shared faults; remap cost does not scale with copied contents; COW allocations
are explained by modified/prefaulted private pages; all resources settle within
explicit cache bounds. Establish performance baselines before choosing numeric
regression budgets; do not invent an unmeasured percentage threshold.

No milestone is complete merely because tests are added, skips rise or a case
is reclassified. Baseline capture, implementation and passing acceptance evidence
are separate deliverables; no new test results are claimed by this document.
