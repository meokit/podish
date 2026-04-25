# HostPage Linux-Style Redesign

This document proposes a Linux-inspired redesign for host-page metadata in Fiberish.
It is intentionally forward-looking. Unlike `docs/memory-management.md`, which describes the current implementation, this document describes the target model we want to migrate toward.

For the preferred slot-owned `HostPage struct` direction, see [HostPage Slot Ownership Design](./hostpage-slot-ownership-design.md).

## Goals

- Keep `HostPage` identity stable while a host page is live.
- Move heavy and optional metadata out of the hot object shape.
- Make ownership and lifetime rules explicit.
- Reduce the amount of state that must be touched on common map/fault paths.
- Avoid ABA-style corruption if recyclable side metadata is reused.
- Stay compatible with the current `AddressSpace` / `AnonVma` / `PageManager` architecture.

## Non-Goals

- Perfect compile-time proof that no stale `HostPage` references exist.
- A literal Linux `struct page` clone.
- Replacing the current `VmPageSlots` ownership model in one step.

## Linux Analogy

Linux does not dynamically allocate and free one metadata object per physical page in the hot path.
Instead, it has:

- stable per-page identity metadata (`struct page`, moving toward `folio`)
- compact hot fields in the main page descriptor
- optional or heavier metadata in side structures (`page_ext`, rmap, LRU state, mapping-specific structures)

The Fiberish equivalent should be:

- `HostPage`: stable identity object for one host-backed page pointer
- hot, always-needed counters/flags kept directly on `HostPage`
- side metadata objects for owner graphs, reverse mapping, TB coherence indexing, and debug/tracing data

## Current Problems

The current `HostPage` shape mixes several very different concerns:

- stable identity: `Ptr`, `Kind`
- hot lifecycle counters: `RefCount`, `MapCount`, `PinCount`
- frequently touched status bits: `Dirty`, `Uptodate`, `Writeback`, `LastAccessTimestamp`
- heavy graph state:
  - owner refs
  - rmap refs
  - TB coherence index

This causes a few issues:

- the object is heavier than the hot path needs
- any attempt to pool the whole object risks stale-reference ABA bugs
- ownership is implicit and distributed across several subsystems
- "does this page still exist?" and "does this page still have side metadata?" are currently coupled

## Core Design

### 1. Stable `HostPage` identity

`HostPage` remains a stable heap object keyed by host page pointer.

Rules:

- one live `HostPage` per host page pointer in `HostPageManager`
- `HostPage` object identity is never pooled or recycled for a different pointer
- once removed from the manager, old references may still exist, but they refer to a retired identity object, not a newly repurposed page

This mirrors the Linux preference for stable per-page descriptors.

### 2. Split metadata into hot and side state

`HostPage` should keep only hot, universal state:

- `Ptr`
- `Kind`
- `RefCount`
- `MapCount`
- `PinCount`
- `Dirty`
- `Uptodate`
- `Writeback`
- `LastAccessTimestamp`
- `StateFlags`

Everything graph-like moves out:

- owner refs
- rmap refs
- TB coherence index
- optional debug metadata

Proposed side objects:

- `HostPageGraphState`
  - owner refs
  - rmap refs
  - TB coherence index
- `HostPageDebugState`
  - optional tracing / leak diagnostics / allocation provenance

### 3. Side metadata is lazily materialized

Most pages do not need all side metadata all the time.

Examples:

- a fresh anonymous page may need hot counters only
- a page participating in reverse mapping needs `HostPageGraphState`
- a debug build may attach `HostPageDebugState`

So the target shape is:

```csharp
internal sealed class HostPage
{
    public IntPtr Ptr { get; }
    public HostPageKind Kind { get; }

    public int RefCount;
    public int MapCount;
    public int PinCount;

    public bool Dirty;
    public bool Uptodate;
    public bool Writeback;
    public long LastAccessTimestamp;

    private HostPageGraphState? _graph;
    private HostPageDebugState? _debug;
}
```

### 4. Recyclable side state uses generation

If a side object is pooled, it must not be usable through a stale pointer path after retirement.

That means the generation belongs on the side state lease boundary, not on `HostPage` identity itself.

Proposed shape:

```csharp
internal sealed class HostPageGraphState
{
    public int Generation;
    public Lock Gate;
    public List<HostPageOwnerRef> OwnerRefs;
    public List<HostPageRmapRef> RmapRefs;
    public Dictionary<HostPageRmapKey, int> RmapRefIndices;
    public HostPageTbCohIndex TbCohIndex;
}
```

Access pattern:

- capture `HostPageGraphState` reference plus generation
- lock it
- validate that the `HostPage` still points at that same state and generation
- mutate

This prevents a recycled graph state object from being mutated through a stale captured reference.

## Ownership Model

This is the most important semantic change to make explicit.

`HostPage` is not owned by one subsystem in the ordinary OO sense.
It is a shared identity object managed by `HostPageManager`.

Ownership responsibilities should be treated as follows:

### `HostPageManager`

Owns:

- the canonical `Ptr -> HostPage` registry
- creation of new `HostPage` identities
- retirement of `HostPage` identities from the registry
- side-state pools

Does not own:

- guest mappings
- page-cache membership
- anon ownership

### `AddressSpace` / `AnonVma` / `VmPageSlots`

Own:

- semantic residency of a page inside a mapping object
- `VmPage` slot membership
- owner refs attached to a page

They do not own the `HostPage` object itself.
They own claims on it.

### `PageManager`

Owns:

- currently installed guest-page bindings
- map-count participation
- native mapping references

It does not own the `HostPage` identity.

### Runtime summary

`HostPage` lifetime is the intersection of claims from:

- semantic owners (`AddressSpace` / `AnonVma`)
- installed mappings (`MapCount`)
- external/global refs (`RefCount`)
- pins (`PinCount`)

When all claims drop to zero, `HostPageManager` may retire the page identity from the registry.

## Can We Get Compile-Time Guarantees?

Not fully, not with ordinary shared managed references.

As long as code can store a `HostPage` reference in heap objects, the C# compiler cannot prove that no stale references exist at retirement time.

What we *can* do is narrow the unsafe surface:

### Option A: stable identity + best-effort runtime retirement checks

This is the practical baseline.

- `HostPage` identity stays stable
- side metadata is validated with generation
- retirement is based on explicit counters and owner claims

This is the simplest design and already much safer than pooling whole `HostPage` objects.

### Option B: handle-based access for side metadata

Replace most external `HostPage` usage with a handle:

```csharp
internal readonly record struct HostPageHandle(IntPtr Ptr);
```

Then all graph access goes through `HostPageManager`.

Pros:

- fewer places can accidentally hold long-lived object references
- ownership becomes more explicit

Cons:

- more call-site churn
- more registry lookups

### Option C: scoped leases for graph mutation

Introduce an explicit mutation lease:

```csharp
internal ref struct HostPageGraphLease
{
    public HostPage HostPage;
    public HostPageGraphState State;
    public int Generation;
}
```

Pros:

- narrows mutation lifetime
- prevents storing the lease in heap fields, async state machines, or closures

Cons:

- only works for stack-bounded access
- does not remove stale `HostPage` references themselves

### Recommendation

Use:

- Option A as the base architecture
- selectively add Option C for mutation-heavy internals
- consider Option B only if `HostPage` references continue to leak broadly

## Target Data Split

### Hot fields on `HostPage`

Keep on-object:

- `Ptr`
- `Kind`
- `RefCount`
- `MapCount`
- `PinCount`
- `Dirty`
- `Uptodate`
- `Writeback`
- `LastAccessTimestamp`

Why:

- accessed frequently
- compact
- identity-adjacent
- no graph traversal needed

### Warm graph state in `HostPageGraphState`

Move out:

- `OwnerRefs`
- `RmapRefs`
- `RmapRefIndices`
- `TbCohIndex`

Why:

- only needed for some pages / operations
- bulkier
- naturally guarded by one graph lock

### Cold debug state

Possible future side object:

- page-owner provenance
- allocation site tracking
- reclaim diagnostics
- leak detection breadcrumbs

This should not live on the hot path by default.

## Lifecycle State Machine

### Live

- registered in `HostPageManager`
- zero or more claims active
- side metadata may or may not exist

### Unclaimed

- `RefCount == 0`
- `MapCount == 0`
- `PinCount == 0`
- no owner refs

At this point:

- remove from `HostPageManager`
- release graph side state back to its pool
- keep old `HostPage` identity object retired, not repurposed

### Retired

- no longer discoverable by `Ptr -> HostPage`
- hot fields may remain readable for diagnostics
- graph state is detached
- all graph mutation APIs become no-ops or fail-fast

Retired `HostPage` is effectively a tombstone object.

## Migration Plan

### Phase 1: clarify ownership in docs and code

- document that `HostPageManager` owns identity registry, not semantic page ownership
- rename helpers where needed to say "claim" or "binding" instead of "owner" when referring to runtime references

### Phase 2: split hot and graph state

- introduce `HostPageGraphState`
- move owner/rmap/TB-coherence structures out of `HostPage`
- keep hot counters on `HostPage`

This is the highest-value change.

### Phase 3: add pooled graph-state recycling with generation

- lazy-create graph state
- reset and return to pool on retirement
- validate generation on mutation paths

### Phase 4: optional scoped graph leases

- add stack-only mutation helpers for rmap / TB coherence update paths
- keep direct field exposure narrow

### Phase 5: optional handle-based external APIs

- if needed, change broad call sites to `HostPageHandle`
- keep `HostPage` references mostly internal to memory-management code

## Expected Benefits

- closer to Linux page-metadata structure
- lower pressure to pool whole `HostPage`
- less risk of ABA bugs
- cleaner ownership boundaries
- clearer separation between hot path and graph bookkeeping
- better base for optional page-owner / reclaim diagnostics

## Expected Costs

- one extra pointer chase for graph-heavy operations
- more explicit lifecycle code
- some migration churn in rmap / TB coherence paths

This is acceptable because graph-heavy operations are already not the hottest part of the common fault/install path.

## Concrete Recommendation

The next implementation step should be:

1. freeze `HostPage` identity semantics
2. stop trying to recycle `HostPage` objects themselves
3. split out `HostPageGraphState`
4. pool only `HostPageGraphState`
5. keep generation validation only on pooled side metadata

That gives us the Linux-like benefits without forcing a full handle-only redesign up front.
