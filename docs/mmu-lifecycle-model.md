# Native MMU ownership and code-cache lifetime

## MMU attachment

The native `MmuCore` owns the page directory, host-page alias index and
decoded-block cache. Attached engines and standalone `MmuHandle`s hold
intrusive references. The native runtime deletes the core when the last
reference drops. Managed `Engine` tracks attachments by MMU identity and
rejects attempts to attach a disposed handle.

| Operation | MMU behavior |
|---|---|
| `X86_MmuCreateEmpty` | Return a standalone handle for a new core with an empty page directory. |
| `X86_Clone` with shared memory (`CLONE_VM`) | Attach the child engine to the parent's core. |
| `X86_Clone` without shared memory | Create a core, copy the mappings and borrow the same backing pointers. The guest fork path applies managed copy-on-write rules. |
| `X86_EngineGetMmu` | Return a retained handle to the attached core. |
| `X86_EngineDetachMmu` | Transfer the old attachment to a handle and bind the engine to an empty core. |
| `X86_EngineAttachMmu` | Attach the target core at an idle boundary. |
| `X86_EngineAttachMmuForExec` | Replace the address space during guest `execve`. If native execution still borrows blocks from the old core, retain that core until execution exits. |

Use `X86_MmuRetain` and `X86_MmuRelease` for standalone handle lifetime and
`X86_MmuGetIdentity` to inspect the core's identity. Detach leaves a live
engine with an empty MMU. The caller owns guest page backing. Pass a 4 KiB backing span and an aligned
guest address to `X86_MemMapHostPage`. Keep the backing alive until every
mapping and clone has finished using it. `X86_MemProtect` changes R/W/X on
existing mappings. `X86_MemUnmap` removes mappings and invalidates cached
translations. The mapping enumeration reports R/W/X and the dirty flag. See [C API declarations](../libfibercpu/src/bindings.h),
[MMU core and cache](../libfibercpu/src/mem/mmu.h), and
[managed attachment code](../Fiberish.Core/Core/Engine.cs).

## Native code-cache borrowing

The guest scheduler serializes execution on its host thread. `X86_Run` and
`X86_Step` reject recursive entry on that thread, including calls for a
different engine from an execution callback. The normal APIs reject destroying
or rebinding a running engine. `ResetMemory` rejects replacement of a core
with active guest execution. The scheduler and MMU require separate
synchronization for parallel host-thread use of a shared core.

`X86_Run` borrows decoded-block and operation pointers until it returns.
During decode and concatenation, the allocator can refuse a request that would
exceed the core's block budget and mark the cache for a flush. Execution can
finish the available block or run an instruction from stack storage. If the
guest remains runnable, the run can return `CachePressure`. The scheduler
then reschedules the task. A caller running to `endEip` checks the
exit status and resumes if needed.

Before the next `X86_Run` obtains a block pointer, the MMU releases any pending
block pool and advances its generation. A callback-time reset invalidates
cached blocks and defers pool release to that boundary. An idle reset releases
the pool in the same call. The block budget counts requested block bytes.
`-XX:BlockCacheSize` sets that budget for the core. A block too large for an
empty pool can use stack execution without a flush loop.

The MMU keys cached blocks by host page base and guest EIP. It tries the
engine's small lookup cache before consulting the core's per-host-page hash
index. The core tracks which host pages each block spans so an executable
write can invalidate affected blocks. The monotonic pool allocates these
blocks at `alignof(BasicBlock)`.

AArch64 checks the current instruction bytes when entering a cached block,
including hits on a same-page successor link. Replacing a cache slot
invalidates links to its prior occupant. The same host file range can appear
at distinct host virtual addresses, and callers can write directly to the
backing memory. Fast guest stores that
continue in a block require a dirty, writable page from the runtime's
private backing pool and zero executable aliases in the current MMU.
Stores to caller-owned or host file views use the ordinary MMU write path and
refetch the next instruction.

## Translation caches

`MicroTlbAccessGuard` checks once that the operand fits in its guest page.
The MMU then probes the micro-TLB threaded through the handler ABI. A miss
there probes the matching read or write table in the engine's direct-mapped
L2 TLB. An L2 hit copies both prepacked tags and the host-pointer addend into
the micro-TLB. The L2 TLB also has a separate execute table. Write tags cover
dirty, writable pages eligible for the fast path. Clean pages and pages
that require write interception take the slow path. The core publishes
translation invalidations to other attached engines through its TLB
shootdown ring. See [TLB entries](../libfibercpu/src/mem/tlb.h) and
[data access guards](../libfibercpu/src/mem/mmu_impl.h).
