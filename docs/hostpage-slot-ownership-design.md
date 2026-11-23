# Host-page metadata slots

`HostPageManager` owns the metadata slot table for a `MemoryRuntimeContext`.
`AddressSpace` and `AnonVma` retain resident pages. `ProcessPageManager` records
installed guest mappings. Those owners keep their claims on the backing
while the manager controls allocation and reuse of metadata slots.
See [current memory management](memory-management.md) for the fault and
invalidation paths.

The slot design followed an earlier [stable-object proposal](hostpage-linux-style-design.md).
The implementation uses value records and generation-checked handles.
It stores owner-root rmap state on the mapping objects and keeps
the translated-block coherence summary in the page record.

## Storage and identity

`HostPageTableState.SlotByPtr` maps the address of a live host page to a
slot index. `Slots` is a growable `HostPageSlot[]`. The table reuses
indices through `FreeSlots`. Its lock protects pointer lookup, slot
retirement and reuse. The slot contains `InUse`, `Generation` and a
`HostPageData` value. See [table and slot layout](../Fiberish.Core/Memory/HostPageRef.cs).

The lookup key covers one live host view. View addresses can change or be
reused. A logical file page can span more than one host view over time.
Use `FilePageIdentity` for the logical identity of a file page. A
`BackingPageHandle` owns the release token for the specific backing
allocation or file view. See [file-page identity](../Fiberish.Core/Memory/FilePageIdentity.cs)
and [direct-mapped memory refactor](direct-mapped-memory-refactor-plan.md).

The slot index lets the table grow without changing the reference
stored by callers. A dictionary can relocate its values when it rehashes.
The slot array can also move when it grows. Keep any raw `ref` to a metadata
value within the code that controls access to the current array.

## Metadata layout

`HostPageData` holds the pointer, page kind, dirty state, last access
timestamp and three counters.

| Counter | Claim |
|---|---|
| `OwnerResidentCount` | Resident slots under the bound owner root. A related private mapping can retain the same page across fork. |
| `MapCount` | Installed guest page bindings. |
| `PinCount` | Managed borrowers that need the backing to stay live during an operation. |

The record also carries the owner root and owner page index, a backing
handle where applicable, and `HostPageTbCohSummary`. The summary gives
the write-protection policy without a reverse-map walk on reads of the
summary. `AddressSpace` and `AnonVma.Root` hold `OwnerRmapTracker`, which
maps an owner-local page index and host pointer to guest VMA refs.
This layout keeps the counters close to the owner parameters and lets the
owners allocate rmap storage as they acquire refs.
See [page metadata](../Fiberish.Core/Memory/HostPageRef.cs)
and [rmap owners](../Fiberish.Core/Memory/LinuxMemoryObjects.cs).

## Access and lifetime

`HostPageRef` carries the table, slot index and generation. Operations
check the slot's live state and generation before reading or changing a
record. A copied `HostPageRef` carries no lifetime claim. Hold an owner
or map claim while it suffices for the caller. For a borrower such as
a COW copy, pin the source with `TryPin` and release it with `Unpin`.
The shared zero page has its own lifetime under `ZeroInode`.

A raw `ref` gives short-lived access to the struct. Keep a backing claim
for the page bytes and resolve the slot again after array growth. A future
mutation API could use a stack-only lease to bound reference storage.
The lease would require a live backing claim and generation validation.

`HostPageRef.TryRemoveIfUnused` rechecks owner, map and pin counts, plus
the owner-root binding under the table lock. Once these claims are gone
for an anonymous or file-cache page, it removes the pointer key, clears
the slot and returns the index to `FreeSlots`. It releases the backing handle
outside the lock. The manager advances the slot generation when it
assigns the index to a live page again. See [pin and retirement logic](../Fiberish.Core/Memory/HostPageRef.cs).

## Persistent references

| Caller | Stored data |
|---|---|
| [`ResidentPageRecord`](../Fiberish.Core/Memory/OwnerPageSlots.cs) | Host pointer and `HostPageRef`, plus file identity and any release information. |
| [`MappedPageBinding`](../Fiberish.Core/Memory/ProcessPageManager.cs) | Host pointer, owner kind and object, page index and resident record. |
| [`InodePageRecord`](../Fiberish.Core/VFS/FileBackedPageCache.cs) | The inode page index, pointer and backing handle. |
| [`ZeroInode`](../Fiberish.Core/Memory/MemoryRuntimeContext.cs) | The shared zero-page pointer and its memory reservation. |
| [`TbCoh`](../Fiberish.Core/Memory/TbCoh.cs) | Host pointers in its scratch set and guest addresses grouped by address space for invalidation. |

The current implementation uses the handle where the caller needs metadata
operations and keeps pointers in the structures that carry addresses. The
original proposal also considered a pooled `HostPageGraphState` and access
through borrowed `ref HostPage`. The current owner-root trackers and
generation-checked slot handles cover those responsibilities.
