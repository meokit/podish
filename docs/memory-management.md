# Memory Management Deep Dive

This document describes the current memory-management implementation in Fiberish. It is intentionally implementation-first: names, responsibilities, and lifecycle rules match the current code, not older transition states.

For the forward-looking redesign of host-page metadata, see [HostPage Linux-Style Redesign](./hostpage-linux-style-design.md) and the preferred [HostPage Slot Ownership Design](./hostpage-slot-ownership-design.md).

## Table of Contents

1. High-Level Model
2. Native MMU Layer
3. Managed VM Layer
4. `AddressSpace`, `AnonVma`, and `VmPageSlots`
5. `MappedPageBinding` and Native Mapping Lifecycle
6. Fault Handling
7. `fork`, `mprotect`, `munmap`, and truncate
8. Shared-file writeback and page-cache maintenance
9. Host-mapped file page cache
10. Shared memory and futexes

## 1. High-Level Model

The current design is closest to a Linux-style split between:

- `VMAManager`: process address-space metadata (`mm_struct`-like)
- `VmArea`: virtual memory ranges (`vm_area_struct`-like)
- `AddressSpace`: file/shmem page cache (`address_space`-like)
- `AnonVma`: anonymous/private COW pages (`anon_vma`-like)
- `VmPageSlots`: reusable page-slot storage for `AddressSpace` and `AnonVma`
- `ExternalPageManager`: per-address-space installed native mappings

The key rule is:

- Managed objects decide what memory exists and who owns it.
- The native MMU is an acceleration cache of installed guest->host mappings.

That means native mappings can be zapped and rebuilt from managed state without changing guest-visible semantics.

## 2. Native MMU Layer

The native MMU lives in `libfibercpu`. It provides:

- page-table mappings
- external-page mappings (`map_external_page`)
- owned-page allocation
- TLB flush
- translated-block cache reset
- mapping reprotect

Relevant exported operations:

- `X86_Clone(...)`
- `X86_ReprotectMappedRange(...)`
- `X86_ResetCodeCacheByRange(...)`
- `X86_ResetAllCodeCache(...)`

Current clone behavior:

- `share_mem=1`: share one MMU core
- `share_mem=0`: clone owned pages and preserve external mappings
- external pages are never internalized into owned MMU pages
- child block cache starts empty

Current reprotect behavior:

- `X86_ReprotectMappedRange(...)` only updates permissions for already mapped native pages
- it does not create mappings
- it does not release external-page bindings

Current code-cache reset behavior:

- `ResetCodeCacheByRange` only drops translated blocks for a guest range
- it does not change page tables or page ownership

## 3. Managed VM Layer

### 3.1 `VMAManager`

`VMAManager` is the authoritative managed address-space object. It owns:

- sorted `VmArea` list
- one `ExternalPageManager`
- inode-owned `AddressSpace` refs
- mapping-change sequence / deferred invalidation state

Its job is to:

- create and split `VmArea`s
- resolve page faults
- keep native mappings synchronized with managed state
- perform zap/teardown on `munmap`, truncate, reclaim, and exit

### 3.2 `VmArea`

`VmArea` is the current range metadata object. Main fields:

- `Start`, `End`
- `Perms`
- `Flags`
- `FileMapping`
- `Offset`
- `VmPgoff`
- `VmMapping : AddressSpace?`
- `VmAnonVma : AnonVma?`

Current interpretation:

- file-backed shared/private mappings use `VmMapping`
- private COW pages live in `VmAnonVma`
- anonymous private mappings start with `VmMapping == null` and `VmAnonVma == null`
- anonymous shared mappings use a shmem-style `AddressSpace`

Helpers on `VmArea` are now the only supported way to compute:

- guest page index
- relative offset inside a mapping
- absolute file offset
- effective file-backed length

The old `ViewPageOffset` / `FileBackingLength` transition fields are gone.

### 3.3 `ProcessAddressSpaceSync`

Fiberish does not eagerly walk every engine and rewrite all native mappings on each managed change.

Current synchronization model:

- `VMAManager` owns `CurrentMapSequence`
- mapping/protection changes publish sequence advances through `ProcessAddressSpaceSync`
- each `Engine` tracks `AddressSpaceMapSequenceSeen`
- `SyncEngineBeforeRun(...)` reconciles one engine at run boundary

The current reconciliation step is:

1. collect code-cache reset ranges since the engine's last seen sequence
2. flush MMU TLB state for that engine
3. reset translated block cache for recorded ranges only
4. advance `AddressSpaceMapSequenceSeen`

This is why:

- `mprotect` can be implemented as reprotect + publish
- `munmap` / truncate can zap immediately for the current engine but still publish address-space change state for peers
- non-current engines are synchronized lazily at the next run boundary

## 4. `AddressSpace`, `AnonVma`, and `VmPageSlots`

### 4.1 `VmPageSlots`

`VmPageSlots` is now the reusable page-slot container. It is not a VM owner.

It stores:

- `pageIndex -> VmPage`

Each `VmPage` carries:

- `Ptr`
- `Dirty`
- `Uptodate`
- `Writeback`
- `MapCount`
- `PinCount`
- `LastAccessTimestamp`

`VmPageSlots` provides storage operations only:

- install / create page
- peek / get page
- mark dirty / clear dirty
- truncate by size
- evict clean page
- remove page(s)
- snapshot state

### 4.2 `AddressSpace`

`AddressSpace` is the owner for file/shmem-backed cache.

Current properties:

- `Kind`: `File` or `Shmem`
- `Pages : VmPageSlots`
- `RefCount`
- `IsRecoverableWithoutSwap => Kind == File`

Current rules:

- file page cache lives in `Inode.Mapping`
- ordinary `munmap` never deletes `AddressSpace` pages
- clean file pages may be reclaimed by `GlobalAddressSpaceCacheManager`
- shmem pages are tracked as `AddressSpace`, but are not currently reclaimed like file cache

### 4.3 `AnonVma`

`AnonVma` owns anonymous/private COW pages.

Current rules:

- anonymous private mappings do not create a fake shared object anymore
- zero-fill source is provided directly by the fault path
- the first private write lazily creates `VmAnonVma`
- file-private COW pages also live in `VmAnonVma`
- `fork()` clones `AnonVma` by sharing page pointers and splitting lazily on later writes

Unlike `AddressSpace`, `AnonVma` pages are not page-cache objects:

- they are not tracked by `GlobalAddressSpaceCacheManager`
- they are not reclaimed as file cache
- they are freed when unmapping drops `MapCount` to zero and `PinCount` is also zero

## 5. `MappedPageBinding` and Native Mapping Lifecycle

`ExternalPageManager` no longer models native mappings as `guestPage -> raw ptr` only. It stores typed bindings:

- `Ptr`
- `OwnerKind`
- `AddressSpace? Mapping`
- `AnonVma? AnonVma`
- `VmPage? Page`
- `PageIndex`

Current owner kinds:

- `RawPointer`
- `AddressSpace`
- `AnonVma`
- `ZeroPage`
- `Special`

Binding rules:

- installing a binding increments `VmPage.MapCount` when a typed `VmPage` exists
- releasing a binding decrements `MapCount`
- zero page is special-cased and does not enter normal quota / global-ref accounting
- when an `AnonVma` page loses its last map and `PinCount == 0`, it is removed from the owner
- `AddressSpace` pages keep living after unmap; unmap only removes the native mapping

This is the current basis for future DRI/device-memory style mappings as well: typed bindings plus page-level lifetime, not raw pointers alone.

## 6. Fault Handling

### 6.1 Shared/shared-source faults

Shared-source resolution is centralized in `VMAManager`:

- `ResolveSharedBackingPage(...)`
- `ResolveAndMapSharedBackingPage(...)`
- `MapResolvedBackingPage(...)`

Current behavior:

- anonymous private read miss with no `VmAnonVma` page -> map global zero page
- file/shared mapping miss -> resolve through `VmMapping`
- file-backed direct-mapped page cache is tried before buffered population where applicable

### 6.2 Private faults

Private write faults are centralized through:

- `ResolvePrivateFault(...)`
- `InstallPrivatePageAndMap(...)`

Current behavior:

- file-private read:
  - use `VmAnonVma` if page exists
  - otherwise fall back to `VmMapping`
- file-private write:
  - allocate/copy into `VmAnonVma`
- anonymous private read:
  - use `VmAnonVma` if page exists
  - otherwise map zero page
- anonymous private write:
  - lazily create `VmAnonVma`
  - copy from zero page or current source into a private page

### 6.3 Dirty capture

Private dirty capture:

- `CaptureDirtyPrivatePages(...)`

Shared-file dirty capture:

- `CaptureDirtySharedPages(...)`
- `CaptureDirtySharedFilePages(...)`

The current model is explicit:

- engine/native dirty bits are captured back into managed `VmPage` state before teardown-sensitive operations
- `fork()` captures private dirty pages before cloning
- shared-file teardown paths capture shared dirty pages before unmapping

## 7. `fork`, `mprotect`, `munmap`, and truncate

### 7.1 `fork`

Current non-`CLONE_VM` fork behavior:

1. capture parent private dirty pages
2. clone native engine/MMU preserving external mappings
3. clone managed `VMAManager`
4. rebuild child `ExternalPageManager` bindings from native mappings
5. wrprotect private ranges in parent and child
6. do not tear down parent private mappings
7. do not tear down child private mappings

Important current semantics:

- fork does not range-reset the block cache
- child block cache starts empty anyway
- parent executable mappings keep their existing code cache
- later private writes split through normal COW fault handling

### 7.2 `mprotect`

Current `mprotect` behavior:

- split/adjust `VmArea` metadata as needed
- call `ReprotectMappedRange(...)`
- publish protection change through `ProcessAddressSpaceSync`
- only reset the block cache if execute permission changed

It does not:

- `MemUnmap`
- release external bindings
- capture shared dirty pages

So current `mprotect` is reprotect, not teardown.

### 7.3 `munmap`

Current `munmap` behavior:

1. split/remove `VmArea`s
2. `SyncVmArea(...)` shared-file regions if needed
3. release unmapped `AnonVma` object pages for the removed object-page range
4. zap native mappings via `TearDownNativeMappings(...)`

`TearDownNativeMappings(...)` is still the current unified zap helper. It performs:

- optional shared dirty capture
- `ResetCodeCacheByRange(...)`
- `MemUnmap(...)`
- `ExternalPages.ReleaseRange(...)`

Current lifecycle rule:

- `AddressSpace` pages survive ordinary `munmap`
- `AnonVma` pages can die immediately after the last guest mapping is removed

### 7.4 truncate and inode-range invalidation

Current inode invalidation path is Linux-shaped:

- `Inode.UnmapMappingRange(...)`
- `Inode.TruncateInodePagesRange(...)`
- `Inode.InvalidateInodePages2Range(...)`

`ProcessAddressSpaceSync` broadcasts these operations to all mapped address spaces of an inode.

Current truncate behavior:

- file-backed mappings beyond new EOF are torn down
- later access re-faults and enforces the new EOF rules
- shared file page cache may be invalidated/truncated
- private COW pages are not written back to the file

Current `UnmapMappingRange(..., evenCows)` behavior:

- `evenCows=false`: invalidate shared file-backed mappings without forcing private COW state to die
- `evenCows=true`: used by truncate/hole-punch style operations that must also tear down affected private file mappings so later access re-faults against the new file state

## 8. Shared-file writeback and page-cache maintenance

Shared-file writeback is now explicitly expressed as `AddressSpace`-based sync, not generic object sync.

Key helpers in `VMAManager`:

- `IsSharedFileVmArea(...)`
- `TryGetSharedFileVmAreaState(...)`
- `EnumerateSharedFileVmAreas(...)`
- `TryGetVmAreaOverlap(...)`
- `SyncVmArea(...)`
- `SyncMappedFile(...)`
- `SyncAllMappedSharedFiles(...)`
- `SyncSharedFilePage(...)`

Current writeback rules:

- only `MAP_SHARED` file mappings participate
- dirty pages are captured from engines before sync
- per-page sync first tries inode direct mapped-page flush
- if direct flush is unavailable, it falls back to copying page contents back through inode writeback
- pages beyond effective file backing are not written back

Global maintenance is handled by `GlobalAddressSpaceCacheManager`:

- tracks only `AddressSpace`
- may trigger `SyncAllMappedSharedFiles(...)`
- may reclaim clean file-backed pages
- does not treat `AnonVma` as cache

## 9. Host-mapped file page cache

`HostFS` and `SilkFS` may use host file mappings as page-cache backing.

Current policy:

- full guest 4K file pages use the host-mapped backend when available
- Darwin/Linux also allow direct mapping of partial EOF tail pages
- Windows/WASI keep partial EOF tail pages on the buffered path

Current backend split:

- normal windows: `.NET MemoryMappedFile`
- Unix tail-extension windows: native `mmap/msync/munmap`

Why:

- `.NET MemoryMappedFile` cannot represent the final partial page without either failing or extending the file
- native Unix `mmap` can represent the final partial page without changing file size

Current validated Darwin/Linux behavior:

- EOF tail bytes are initially zero-filled
- `MAP_SHARED` tail writes are visible to other shared mappings of the same page
- those tail writes do not become file contents via `msync`
- later `ftruncate` growth does not resurrect those tail writes as persisted file contents
- a page fully beyond EOF faults as `SIGBUS`

Fiberish currently accepts one Linux-like consequence:

- if a cached shared tail page is reused later, EOF-tail bytes may be observed again

## 10. Shared memory and futexes

### 10.1 `MAP_SHARED|MAP_ANONYMOUS`

Current model:

- represented as shmem/tmpfs-style `AddressSpace`
- not as anonymous private zero-source

### 10.2 SysV SHM

Current model:

- SysV SHM backing is an `AddressSpace`
- attaching inserts a `VmArea`
- detaching zaps the mapping
- object lifetime is independent of one process unmapping it

### 10.3 Futexes

Shared futexes are keyed by the resolved host physical pointer once the page is mapped.

Current behavior:

- `WAIT` path faults/touches the page, then resolves a shared host key
- `WAKE` path tries to do the same
- if `WAKE` still cannot resolve a shared host key, it falls back to a best-effort private key instead of crashing

This is sufficient for shared mappings backed by the same installed host page to rendezvous across processes.

## 11. Ownership Summary

Current ownership boundaries:

| Object | Owned By | Holds | Purpose |
|---|---|---|---|
| `Engine` | runtime / `FiberTask` | native CPU state + current MMU | execution |
| `VMAManager` | `Process` | `VmArea[]`, `ExternalPageManager` | address-space semantics |
| `VmArea` | `VMAManager` | mapping metadata + refs to `AddressSpace` / `AnonVma` | virtual range description |
| `AddressSpace` | `Inode.Mapping` or shmem owner | `VmPageSlots` | file/shmem page cache |
| `AnonVma` | private mappings | `VmPageSlots` | anonymous/private COW pages |
| `ExternalPageManager` | `VMAManager` | `guestPage -> MappedPageBinding` | installed native mappings |
| `VmPage` | `VmPageSlots` owner | page state + map/pin counts | page lifecycle |

The important rule remains:

- `VMAManager`, `VmArea`, `AddressSpace`, and `AnonVma` are authoritative.
- Native MMU mappings and translated blocks are caches derived from that managed state.
