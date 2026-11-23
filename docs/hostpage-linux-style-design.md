# Host-page design alternative with stable objects

This proposal explored one heap-allocated `HostPage` descriptor per live
host pointer and a pool for optional graph metadata. The implementation
uses manager-owned metadata slots with generation-checked `HostPageRef`
handles. See [host-page metadata slots](hostpage-slot-ownership-design.md)
and [current memory management](memory-management.md).

## Proposed descriptor and graph state

Linux page descriptors motivated the split between compact page metadata
and side structures for reverse mapping and optional diagnostics. In this
proposal `HostPageManager` would keep a canonical descriptor for each
live pointer. The manager would retire the descriptor when all claims
dropped. Callers holding the old descriptor would still see a retired
object. Recycling that object for another pointer would allow an old
reference to mutate the new page, so the proposal reserved pooling for
side state.

The descriptor would hold the pointer, kind, owner/map/pin counts, status
bits and timestamp. It could omit graph state until a caller needed
owner refs, reverse refs or a coherence index. Allocation provenance and
reclaim diagnostics could use a separate optional object.

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

The proposal would advance the side-state generation on reuse.
A caller would capture the side object and generation, acquire its lock
and check that the descriptor still pointed to that generation before
mutating it. This guards a borrower against a stale reference when a
different descriptor rents the same graph object.

## Claims and retirement

The proposal assigned descriptor creation and retirement to
`HostPageManager`. `AddressSpace` and `AnonVma` would retain resident
page claims. The installed-mapping table would contribute map references.
Pinned borrowers would retain the page for the duration of their operation.

A page would enter the retirement path after its owner, reference, map
and pin claims dropped. The manager would remove the descriptor from the
registry, detach graph state and return it to its pool. A retired
descriptor could retain diagnostic fields. Graph updates would check
liveness before writing.

## Access alternatives

| Option | Access model | Tradeoff |
|---|---|---|
| Stable descriptors and generation-checked side state | Callers keep `HostPage` references and validate graph access at runtime. | Direct reads of the hot fields. Existing managed references can outlast retirement. |
| Pointer handles | Callers resolve graph operations through `HostPageManager`. | Fewer long-lived descriptor references and an extra registry lookup for access through a pointer. |
| Stack-only graph leases | Give a narrow mutation API a `ref struct` carrying the graph object and generation. | The compiler bounds where callers store the lease. C# still permits other descriptor references to outlast retirement. |

The proposed rollout started with stable descriptors, split out graph
state and added generation checks when recycling that state. Scoped graph
leases were an option for mutation-heavy paths. Pointer handles could
reduce long-lived object references if those paths still needed cleanup.

## Implementation outcome

Fiberish uses the slot design. The growable table keeps `HostPageData` in
value slots and callers keep table, index and generation in `HostPageRef`.
Mapping objects own resident slots and hold reverse refs through
`OwnerRmapTracker`. The page metadata keeps a compact coherence summary.
See [slot and handle implementation](../Fiberish.Core/Memory/HostPageRef.cs)
and [mapping owners](../Fiberish.Core/Memory/LinuxMemoryObjects.cs).
