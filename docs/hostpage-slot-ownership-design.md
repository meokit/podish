# HostPage Slot Ownership Design

This document refines the earlier HostPage redesign into a more Linux-like ownership model:

- `HostPageManager` is the sole owner of host-page metadata storage
- `HostPage` becomes a mutable value type (`struct`)
- external subsystems do not store `HostPage`
- external subsystems store a stable key (`IntPtr` host page pointer) and resolve metadata through `HostPageManager`

This is the preferred implementation direction.

For earlier exploration of the "stable object + pooled side state" approach, see [HostPage Linux-Style Redesign](./hostpage-linux-style-design.md).

## Goals

- Make the host page pointer the canonical identity.
- Make `HostPageManager` the only owner of page metadata storage.
- Keep hot metadata contiguous and cheap to touch.
- Prevent object-identity leaks by not exposing `HostPage` as a heap object.
- Require lifetime discipline through pin/refcount/mapcount ownership.
- Treat stale access as a bug in lifetime management, not a supported runtime state.

## Design Summary

The model should become:

- `IntPtr` host page pointer is the page identity key.
- `HostPageManager.State.Pages` stores only key-to-slot indexing metadata.
- Actual page metadata lives in a stable slot array / arena.
- `HostPage` is a mutable `struct` stored in those stable slots.
- Callers may temporarily obtain `ref HostPage`, but only by resolving from the key through `HostPageManager`.
- Callers may not retain `HostPage` as an object reference because there is no object reference anymore.

This is much closer to Linux:

- PFN is the identity.
- `struct page` is metadata stored in a managed table.
- Callers compute or resolve the descriptor from the identity rather than owning the descriptor object.

## Why `IntPtr` Should Be the Identity

Today the manager already canonicalizes by pointer:

- [`HostPageManager.GetOrCreate(...)`](../Fiberish.Core/Memory/HostPage.cs#L1035)

That means the true identity is already the host page pointer.
The heap `HostPage` object is only an implementation carrier around that identity.

If we want stronger ownership and fewer stale-reference hazards, the right move is:

- make `IntPtr` the externally visible identity
- make `HostPage` an internal metadata record

## Why Not Expose `HostPage` as a Long-Lived Object

Several places currently store `HostPage` for longer than one stack frame:

- [`VmPage.HostPage`](../Fiberish.Core/Memory/VmPageSlots.cs#L543)
- [`MappedPageBinding.HostPage`](../Fiberish.Core/Memory/PageManager.cs#L30)
- [`InodePageRecord.HostPage`](../Fiberish.Core/VFS/FileBackedPageCache.cs#L20)
- [`ZeroInode._sharedZeroHostPage`](../Fiberish.Core/Memory/MemoryRuntimeContext.cs#L144)
- scratch sets such as [`TbCoh.ScratchState.HostPages`](../Fiberish.Core/Memory/TbCoh.cs#L8)

As long as these are ordinary managed references, the compiler cannot prove that no stale references survive retirement.

Switching to:

- long-lived `IntPtr` key
- short-lived `ref HostPage`

shrinks the unsafe surface dramatically.

## Core Storage Model

### Manager-owned slot table

`HostPageManager.State` should become:

```csharp
private sealed class State
{
    public readonly Lock Gate = new();
    public readonly Dictionary<IntPtr, int> SlotByPtr = [];
    public readonly List<HostPageSlot> Slots = [];
    public readonly Stack<int> FreeSlots = [];
    public readonly HostPageGraphStatePool GraphPool = new();
}
```

Where:

- `SlotByPtr` maps identity key to slot index
- `Slots` is stable metadata storage
- `FreeSlots` allows slot reuse without reallocating one metadata object per page
- graph-side metadata is pooled separately

### Stable slot, not `Dictionary<IntPtr, HostPage>`

We should **not** store `HostPage` directly as the dictionary value if callers need `ref HostPage`.

Reason:

- `Dictionary` may rehash and relocate values
- `CollectionsMarshal.GetValueRefOrAddDefault(...)` is only safe for tightly bounded internal usage
- it is not a good foundation for a general "resolve key to stable ref" API

So the manager must resolve:

- `IntPtr -> slot index`
- `slot index -> ref HostPage`

## HostPage Layout

`HostPage` should be a compact mutable `struct` containing only hot, universal state.

Proposed shape:

```csharp
internal struct HostPage
{
    public IntPtr Ptr;
    public HostPageKind Kind;

    public int RefCount;
    public int MapCount;
    public int PinCount;

    public bool Dirty;
    public bool Uptodate;
    public bool Writeback;

    public long LastAccessTimestamp;

    // 0 means absent
    public int GraphStateSlot;

    // Debug-only or always-on if we want slot lifetime validation
    public uint Generation;
    public HostPageFlags Flags;
}
```

Hot fields stay here because they are:

- read frequently
- small
- needed regardless of whether rmap/TB-coherence is active

## Slot Layout

The manager should not expose raw slot internals, but internally a slot should contain:

```csharp
internal struct HostPageSlot
{
    public bool InUse;
    public uint SlotGeneration;
    public HostPage Page;
}
```

Meaning:

- `InUse`: live metadata currently bound to some `Ptr`
- `SlotGeneration`: increments on reuse
- `Page.Generation`: copied from `SlotGeneration` when activated

Even if stale is "a bug", keeping generation in debug builds is still worth it:

- it catches use-after-retire much closer to the fault
- it makes slot reuse diagnosable
- it does not force the public model to support stale references as a feature

## Side Metadata

Heavy metadata should still be split out.

### Graph state

```csharp
internal sealed class HostPageGraphState
{
    public List<HostPageOwnerRef> OwnerRefs = [];
    public List<HostPageRmapRef> RmapRefs = [];
    public Dictionary<HostPageRmapKey, int> RmapRefIndices = [];
    public HostPageTbCohIndex TbCohIndex = new();
}
```

The hot `HostPage` struct should only hold:

- `GraphStateSlot`

This gives us:

- compact main record
- pooled heavy graph structures
- no extra indirection on hot counters/flags

### Optional cold metadata

Possible future side structures:

- page-owner diagnostics
- reclaim/debug tracing
- allocation provenance

These should remain explicitly opt-in.

## Ownership Rules

This is the core semantic rule:

`HostPageManager` owns metadata storage.
Everyone else owns claims, not metadata.

### HostPageManager owns

- creation of new metadata slots
- key-to-slot resolution
- retirement and slot reuse
- side-state pool ownership

### AddressSpace / AnonVma / VmPageSlots own

- semantic membership of a page in a mapping object
- owner-claim bookkeeping

They do **not** own the `HostPage` record itself.

### PageManager owns

- installed guest-page mappings
- `MapCount` participation

It also does not own metadata storage.

### Lifecycle criterion

A page may retire only when all claims are gone:

- `RefCount == 0`
- `MapCount == 0`
- `PinCount == 0`
- no owner refs in graph state

Once that happens:

1. remove `Ptr` from `SlotByPtr`
2. release graph state to pool
3. clear the slot
4. increment slot generation
5. return slot index to `FreeSlots`

## Access Model

### Long-lived identity

Long-lived storage sites should keep:

- `IntPtr Ptr`

not `HostPage`.

### Short-lived metadata access

All metadata access goes through the manager:

```csharp
ref HostPage page = ref HostPageManager.GetPageRef(ptr);
```

or a more disciplined lease:

```csharp
using var lease = HostPageManager.Pin(ptr);
ref HostPage page = ref lease.Page;
```

### Recommendation: use leases for mutation-heavy paths

Provide two styles:

- `TryGetPageRef(ptr, out HostPageRef lease)` for stack-bounded use
- specialized helpers for simple operations like `Touch`, `AddMapRef`, `ReleaseMapRef`

This keeps call sites honest and makes pinning visible in APIs.

## Proposed Public Internal API

### Resolution

```csharp
internal static bool TryLookupSlot(IntPtr ptr, out int slot);
internal static ref HostPage GetPageRef(int slot);
internal static ref HostPage GetPageRefByPtr(IntPtr ptr);
```

### Lifetime

```csharp
internal static int GetOrCreateSlot(IntPtr ptr, HostPageKind kind);
internal static void AddRef(IntPtr ptr);
internal static void ReleaseRef(IntPtr ptr);
internal static void AddMapRef(IntPtr ptr);
internal static void ReleaseMapRef(IntPtr ptr);
internal static void AddPin(IntPtr ptr);
internal static void ReleasePin(IntPtr ptr);
```

### Graph-side access

```csharp
internal static ref HostPageGraphState GetOrCreateGraphStateRef(ref HostPage page);
internal static bool TryGetGraphStateRef(ref HostPage page, out HostPageGraphStateLease lease);
```

The manager should own graph-state slot allocation too.

## Where Current Callers Must Change

These are the main places still storing `HostPage` directly today.

### `VmPage`

Current:

- [`VmPage.HostPage`](../Fiberish.Core/Memory/VmPageSlots.cs#L543)

Target:

- replace with `IntPtr Ptr`
- resolve metadata when hot fields or graph state are needed

Why this is acceptable:

- `VmPage.Ptr` already mostly forwards to `HostPage.Ptr`
- for many operations the pointer is the real payload

### `MappedPageBinding`

Current:

- [`MappedPageBinding.HostPage`](../Fiberish.Core/Memory/PageManager.cs#L30)

Target:

- store `IntPtr Ptr`
- optionally keep `MappedPageOwnerKind`, `Mapping`, `AnonVma`, `VmPage`, `PageIndex`
- resolve `ref HostPage` only when touching mapcount or TB coherence

### `InodePageRecord`

Current:

- [`InodePageRecord.HostPage`](../Fiberish.Core/VFS/FileBackedPageCache.cs#L20)

Target:

- store `IntPtr Ptr`
- resolve through `HostPageManager` when metadata is needed

### `ZeroInode`

Current:

- [`_sharedZeroHostPage`](../Fiberish.Core/Memory/MemoryRuntimeContext.cs#L144)

Target:

- store only `_sharedZeroPagePtr`
- use manager lookup when needed

### TB coherence scratch sets

Current:

- [`HashSet<HostPage>` scratch](../Fiberish.Core/Memory/TbCoh.cs#L8)

Target:

- prefer `HashSet<IntPtr>`

This will remove another source of long-lived object identity assumptions.

## Why `ref` Alone Is Not Enough

Using `ref` is useful, but only as the access mechanism, not as the ownership model.

`ref` cannot be the whole design because:

- it cannot be stored in heap objects
- it cannot cross async boundaries
- it cannot be used as the long-lived identity

So the right split is:

- `IntPtr` is the identity
- `ref HostPage` is the temporary access path

## Stale Access Policy

This design explicitly treats stale access as a bug.

That means:

- code must not retain `ref HostPage` beyond the manager-controlled access window
- code must not assume a previously valid `IntPtr` can always be resolved later unless it still holds a claim
- pin/refcount discipline is the correctness boundary

However, debug diagnostics are still recommended:

- slot generation checks in debug builds
- assertions on resolving retired slots
- optional poison values on retired slots

These do not change semantics.
They only make bugs easier to catch.

## Recommended Migration Plan

### Phase 1: storage split

- introduce `Dictionary<IntPtr, int> SlotByPtr`
- introduce `List<HostPageSlot> Slots`
- keep existing semantics, but move storage behind slot indirection

### Phase 2: external type migration

- change `VmPage`, `MappedPageBinding`, `InodePageRecord`, and zero-page caches from `HostPage` to `IntPtr`

### Phase 3: helper APIs

- add manager helpers for hot counter updates
- add manager helpers for `Touch`, map refs, pins, and graph access

### Phase 4: graph-state split

- move owner refs, rmap refs, and TB coherence index to pooled side storage

### Phase 5: debug hardening

- add slot generation assertions
- add stale-resolution assertions
- optionally add lease helpers for mutation-heavy internals

## Preferred End State

The preferred end state is:

- `HostPage` is a manager-owned `struct`
- `IntPtr` is the externally stored page identity
- all long-lived objects store pointer keys, not page metadata objects
- graph metadata is pooled separately
- lifetime is entirely governed by explicit claim counters

This gives us the strongest ownership story and the closest match to Linux's `PFN -> struct page` mental model without forcing unsafe raw-pointer programming throughout the codebase.
