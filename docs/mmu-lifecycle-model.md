# MMU Lifecycle Model

## Summary

The runtime now uses an intrusive refcounted native MMU core with C#-managed attachment semantics.

- `CLONE_VM` shares the same MMU core.
- Non-shared clone/fork creates a new MMU core by cloning pages while skipping external pages.
- External pages are never converted into owned pages.

## Ownership Rules

- Native side owns MMU core lifetime via intrusive refcount (`retain/release`).
- C# side owns engine/MMU binding semantics (`CurrentMmu`, `ReplaceMmu`, `ShareMmuFrom`, `DetachMmu`).
- Engines always have a valid MMU; no `NoMMU` state is exposed.

## Native APIs

- `X86_MmuCreateEmpty`
- `X86_MmuCloneSkipExternal`
- `X86_MmuRetain` / `X86_MmuRelease`
- `X86_MmuGetIdentity`
- `X86_EngineGetMmu`
- `X86_EngineDetachMmu`
- `X86_EngineAttachMmu`

## Invariants

- `Engine.CurrentMmu` is always valid while engine is alive.
- `Engine.GetAttachmentCount(mmuId)` reflects the number of engines attached to that MMU identity.
- Disposed `MmuHandle` cannot be attached again (fail-fast).

## Native Code Cache Borrowing

The guest scheduler serializes native execution on one host thread. `X86_Run`
and `X86_Step` reject recursive execution from callbacks, including execution
of a different engine. An engine cannot be destroyed or rebound while it is
executing; `ResetMemory` rejects replacement of a core whose guest code is
active. These restrictions also apply when callbacks invoke native APIs.

`X86_Run` may borrow raw decoded-block and operation pointers for the entire
call, including speculative decoding and concatenation. No budget allocation
may free the code cache during that call. If an allocation would exceed the
budget, it is refused, the shared core records a pending flush, and the guest
finishes the available block or executes one instruction from stack storage.
`X86_Run` then returns `CachePressure` at a safe point. The scheduler treats
this as a normal reschedulable exit; callers with an `endEip` must run again
until they reach the requested endpoint.

The next `X86_Run` on the shared core releases the block pool **before** it
obtains any block pointer. A cache reset requested in an execution callback
immediately invalidates block reachability, but also defers storage release to
the next `Run`. A reset requested while the core is idle remains immediate.
Generation changes only when the pool is actually released. The cache budget
counts requested block bytes, not the monotonic allocator's bookkeeping.

If a single block cannot fit an empty cache, guest execution proceeds from
stack storage without repeatedly flushing the empty pool. The budget and
native cache controls do not make parallel host-thread access to a shared
core safe; that still requires separate synchronization.
