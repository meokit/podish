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
