# Memory Management Deep Dive

> This document describes the complete memory management architecture of the Fiberish x86 emulator, covering the entire chain from guest virtual addresses to host physical memory, as well as scenarios involving memory sharing such as `fork` and `mmap(MAP_SHARED)`.

---

## Table of Contents

1. [High-Level Layered Architecture](#1-high-level-layered-architecture)
2. [Native Layer: libfibercpu MMU](#2-native-layer-libfibercpu-mmu)
3. [VMA Layer: VMAManager / VMA](#3-vma-layer-vmamanager--vma)
4. [MemoryObject and ExternalPageManager](#4-memoryobject-and-externalpagemanager)
5. [Copy-On-Write (COW) and Private Mappings](#5-copy-on-write-cow-and-private-mappings)
6. [Shared Memory Deep Dive](#6-shared-memory-deep-dive)
7. [Fork and Memory Cloning](#7-fork-and-memory-cloning)
8. [Futexes and Shared Memory](#8-futexes-and-shared-memory)
9. [munmap and msync](#9-munmap-and-msync)

---

## 1. High-Level Layered Architecture

```text
┌─────────────────────────────────────────────────────┐
│  Guest User Space (x86 Program)                     │
│  Virtual Address Space 0x00010000 – 0xBFFFFFFF      │
└────────────────────────┬────────────────────────────┘
                         │  Virtual Address (VAddr)
┌────────────────────────▼────────────────────────────┐
│  VMAManager  (Fiberish.Core/Memory/)                │
│  · Maintains all VMA (Virtual Memory Area) metadata │
│  · Each VMA holds a MemoryObject reference          │
│  · MAP_PRIVATE holds an additional CowObject        │
└────────────────────────┬────────────────────────────┘
                         │  MemoryObject → host page ptr
┌────────────────────────▼────────────────────────────┐
│  MemoryObject  (Fiberish.Core/Memory/)              │
│  · Manages host physical memory ptrs by pageIndex   │
│  · Reference counting (_refCount) for lifecycle     │
│  · IsShared=true → Shared globally (e.g., Inode)    │
└────────────────────────┬────────────────────────────┘
                         │  IntPtr (host aligned page)
┌────────────────────────▼────────────────────────────┐
│  ExternalPageManager  (Fiberish.Core/Memory/)       │
│  · Global ref counting (GlobalRefs) for host memory │
│  · AlignedAlloc / AlignedFree (NativeMemory)        │
└────────────────────────┬────────────────────────────┘
                         │  MapExternalPage()
┌────────────────────────▼────────────────────────────┐
│  Native MMU  (libfibercpu / mmu.h)                  │
│  · PageDirectory → PageTableChunk[1024][1024]       │
│  · Each slot: (byte* page, Property perms)          │
│  · resolve_safe() → host pointer + offset           │
└─────────────────────────────────────────────────────┘
```

Responsibilities are strictly separated across layers:
- **VMAManager**: Manages "what this address space is" (permissions, files, sharing flags, limits).
- **MemoryObject**: Manages "where the real memory is" (host page pointers, on-demand allocation, page cache).
- **ExternalPageManager**: Manages "host memory reference counts" (safe cross-process deallocation).
- **Native MMU**: Manages "how the guest CPU accesses it fast" (TLBs, page fault callbacks).

### 1.1 Ownership Summary

The most important ownership boundaries are:

| Object | Owned By | Holds References To | Notes |
|--------|----------|---------------------|-------|
| `Engine` | `FiberTask` / runtime | native CPU state, current `MmuHandle` | One engine always has exactly one current MMU attached |
| `MmuHandle` | managed owner (`Engine` or detached handle) | native MMU core | Native lifetime is ref-counted; managed code controls attachment semantics |
| `VMAManager` | `Process` | `VMA[]`, `ExternalPageManager`, `MemoryObjectManager` | Authoritative address-space metadata |
| `VMA` | `VMAManager` | `MemoryObject`, optional `CowObject`, optional `VmaFileMapping` | Pure metadata plus backing-object refs |
| `MemoryObject` | `VMA`, inode page cache manager, SysV shm manager, etc. | host page pointers | Ref-counted shared backing object |
| `Inode.PageCache` | inode + `MemoryObjectManager` named-object table | `MemoryObject` | Analogous to Linux `address_space`; shared by all mappings of that live inode |
| `ExternalPageManager` | `VMAManager` | per-address-space mapped host-page refs | Releases native external-page refs when MMU mappings are torn down |

Two rules matter in practice:

- **Managed objects own semantics.** `VMAManager`, `VMA`, `MemoryObject`, and inode page cache decide what memory should exist.
- **Native MMU owns only acceleration state.** Its mappings are disposable caches and can be invalidated and rebuilt from managed state at any time.

---

## 2. Native Layer: libfibercpu MMU

Located in `libfibercpu/mem/mmu.h`, this is the lowest-level software MMU facing the x86 JIT execution engine.

### 2.1 Data Structures

```cpp
PageDirectory
└── l1_directory[1024]          // Each entry covers 4MB
    └── PageTableChunk          // Sparse, on-demand unique_ptr
        ├── pages[1024]         // byte* pointing to actual memory (can be nullptr)
        └── permissions[1024]   // Property: Read | Write | Exec | Dirty | External
```

- **Two-level page table**: L1 index = `addr >> 22`, L2 index = `(addr >> 12) & 0x3FF`, offset = `addr & 0xFFF`
- Page pointers come from two sources:
  - **Internal Allocation** (`allocate_page`): `new std::byte[PAGE_SIZE]`, owned by MMU, deleted on destruction.
  - **External Mapping** (`map_external_page`): Host passes the pointer. The MMU **does not** own it, marked with `Property::External`.

### 2.2 The Key Attribute: External

`Property::External` is the core flag separating "owned pages" from "external pages".
Shared anonymous memory and file mappings are injected via `map_external_page`, and their lifecycles are managed by the C# `ExternalPageManager`.

### 2.3 MMU Handles and Fork Behavior

The native MMU is no longer treated as an anonymous blob hidden behind `X86_Clone`. The managed `Engine` owns an explicit `MmuHandle`, and MMU lifetime is tracked through:

- `Engine.CurrentMmu`
- `Engine.ReplaceMmu(...)`
- `Engine.ShareMmuFrom(...)`
- `Engine.DetachMmu()`
- `MmuHandle.CloneSkipExternal()`

Current behavior:

- `CLONE_VM` / shared-address-space cases share the same native MMU core.
- Non-`CLONE_VM` clone still starts from `X86_Clone(...)`, but the child does **not** keep inherited native mappings as authoritative memory state.
- `FiberTask.Clone()` tears down inherited native mappings across the cloned address space and forces later accesses to refault through `VMAManager`.
- For writable `MAP_PRIVATE` file mappings, clone-time private snapshot pages are imported into the child COW object before teardown so the child preserves the correct fork snapshot.

The practical rule is:

- **Managed VMA / MemoryObject state is authoritative.**
- The native MMU is a fast cache of currently installed mappings and may be torn down and rebuilt from managed state after fork, `mprotect`, `truncate`, or other address-space changes.

---

## 3. VMA Layer: VMAManager / VMA

The `VMAManager` (`Fiberish.Core/Memory/VMAManager.cs`) is the C# layer memory map, analogous to the Linux kernel's `mm_struct`.

### 3.1 The VMA Structure

```csharp
public class VMA {
    public uint Start { get; set; }         // Start address (inclusive)
    public uint End { get; set; }           // End address (exclusive)
    public Protection Perms { get; set; }   // R/W/X
    public MapFlags Flags { get; set; }     // Shared | Private | Anonymous | ...
    public LinuxFile? File { get; set; }    // File handle if file-backed
    public long Offset { get; set; }        // File offset
    public long FileBackingLength { get; set; } // Max bytes of valid file data relative to Start
    public MemoryObject MemoryObject { get; set; }  // Points to the physical memory object
    public MemoryObject? CowObject { get; set; }    // Holds private COW'd pages (for MAP_PRIVATE file)
    public uint ViewPageOffset { get; set; }        // The starting page index within MemoryObject
}
```

- **FileBackingLength vs Offset**: When mapping files, partial pages or `.bss` boundaries require precise bounds checking. `FileBackingLength` determines how many bytes starting from `Start` are actually file-backed. Any memory past this within the VMA is implicitly zero-filled.

### 3.2 VMA.Clone(): Fork Behavior

```csharp
public VMA Clone() {
    var shared = (Flags & MapFlags.Shared) != 0;
    MemoryObject obj;

    if (shared || CowObject != null) {
        // MAP_SHARED or MAP_PRIVATE file: The base MemoryObject remains shared.
        MemoryObject.AddRef();
        obj = MemoryObject;
    } else {
        // MAP_PRIVATE anonymous: Strictly isolated, perform a deep copy.
        obj = MemoryObject.ForkCloneForPrivate();
    }

    // Existing COW pages are initially shared between parent/child and split lazily on write.
    MemoryObject? cowObj = CowObject?.ForkCloneSharingPages();
    var clonedFileMapping = FileMapping?.AddRef();

    return new VMA { ..., FileMapping = clonedFileMapping, MemoryObject = obj, CowObject = cowObj };
}
```

### 3.3 Address-Space Synchronization Model

Address-space mutations are not applied by eagerly walking every registered engine and rewriting all native mappings immediately.

Current model:

- `VMAManager` owns a monotonically increasing `CurrentMapSequence`.
- Each mapping mutation records one or more invalidation ranges in `_pendingInvalidations`.
- Each `Engine` remembers `AddressSpaceMapSequenceSeen`.
- `ProcessAddressSpaceSync.SyncEngineBeforeRun(...)` reconciles a specific engine just before it runs:
  - flushes MMU TLB state for that engine
  - invalidates translated code for the recorded ranges
  - advances `AddressSpaceMapSequenceSeen`

Pseudocode:

```csharp
var currentSeq = vmaManager.CollectCodeCacheResetRangesSince(engine.AddressSpaceMapSequenceSeen, ranges);
engine.FlushMmuTlbOnly();
foreach (var range in ranges)
    engine.ResetCodeCacheByRange(range.Start, range.Length);
engine.AddressSpaceMapSequenceSeen = currentSeq;
```

This is why operations such as `munmap`, `mprotect`, `truncate`, or inode-size updates are described as changing **managed state first** and reconciling each engine on its next run boundary.

---

## 4. MemoryObject and ExternalPageManager

### 4.1 MemoryObject

```csharp
public sealed class MemoryObject {
    private readonly Dictionary<uint, IntPtr> _pages = new();  // pageIndex → host ptr
    private int _refCount = 1;
    public bool IsShared { get; }
}
```

Key operations:
- **`GetOrCreatePage`**: On-demand allocation. Calls `ExternalPageManager.AllocateExternalPage()` and invokes an optional `onFirstCreate(ptr)` callback (e.g., to read from a file).
- **`ForkCloneForPrivate`**: For anonymous private memory, iterates through all pages and `memcpy`'s them into freshly allocated `ExternalPageManager` pages.

### 4.2 ExternalPageManager

Each `VMAManager` owns an `ExternalPageManager` instance tracking which host pages are currently installed into that address space's native MMU. A static `GlobalRefs` dictionary (`nint → int`) maintains the global reference count of each host pointer across all address spaces.
A host page is only `NativeMemory.AlignedFree`'d when its `GlobalRefs` drops to 0.

`ExternalPageManager` should be read as "native mapping ref tracker", not "owner of page semantics":

- `MemoryObject` decides whether a page exists and keeps the page pointer alive.
- `ExternalPageManager` tracks how many native MMU mappings currently point at that page inside one address space.
- `ReleaseRange` is therefore paired with native `MemUnmap`, not with inode/page-cache eviction itself.

---

## 5. Copy-On-Write (COW) and Private Mappings

The emulator supports full Copy-On-Write semantics for `MAP_PRIVATE` file mappings (like loading `ELF` libraries or `libc.so`).

1. **Setup**: A `MAP_PRIVATE` file mapping creates a VMA where `MemoryObject` is the shared Inode Page Cache (Read-Only source), and `CowObject` is a new empty `MemoryObject`.
2. **Read**: A read fault maps the page from the `MemoryObject` cache as **Read-Only**.
3. **Write**: A write fault triggers COW:
   - The page is copied from the Inode cache (or existing COW page) into a new page in the `CowObject`.
   - The VMA permissions are upgraded to `Writable`.
4. **Ownership Checking**: If a `CowObject` page has a global ref-count of exactly 2 (1 for `CowObject`, 1 for `ExternalPages`), the emulator detects it has exclusive ownership and skips re-copying, merely upgrading the mapping to `Writable`.

*Note: Anonymous `MAP_PRIVATE` mappings use eager deep-copying during `fork` because they don't have an underlying backing cache object.*

---

## 6. Shared Memory Deep Dive

### 6.1 MAP_SHARED File Mappings and Page Caching

File mappings use an Inode-level global page cache to ensure that all processes mapping the same file see the same memory:

```csharp
public MemoryObject GetOrCreateInodePageCache(Inode inode) {
    var key = $"pagecache:inode:{RuntimeHelpers.GetHashCode(inode)}";
    var obj = CreateOrOpenNamed(key, ...);
    inode.PageCache ??= obj;
    return obj;
}
```

Any `mmap(MAP_SHARED)` on the same live `Inode` object receives this identical `MemoryObject`. Writes from one process immediately affect the physical pages, becoming instantly visible to all other processes mapping the same inode instance.

This sharing is by **inode object identity**, not by path string. Hard links or other aliases only share page cache if they resolve to the same live inode object.

### 6.1.1 HostFS / SilkFS Host-Mapped Page Cache Policy

For `HostFS` and `SilkFS`, file-backed pages may be backed directly by a host file mapping instead of an emulator-owned anonymous page. This is an optimization, but it must not change guest-visible file semantics.

Current policy:

- Full guest 4K file pages:
  - Use the regular host-mapped page cache backend.
  - On all supported hosts, the default implementation uses a host-granularity window cache.
- Partial EOF tail pages:
  - On Darwin/Linux, direct mapping is allowed.
  - On Windows/WASI, direct mapping is disabled and the tail page falls back to the buffered path.

The backend is intentionally split:

- Normal full-page windows still use `.NET MemoryMappedFile`.
- Unix tail-extension windows use native `mmap/msync/munmap`.

Reason:

- `.NET MemoryMappedFile` with `capacity=0` cannot create a view that extends past the file length for the final partial page.
- Setting `capacity` to the rounded page/window size causes the host file itself to be extended, which is incorrect for guest `mmap` semantics because mapping must not mutate `i_size`.
- Native Unix `mmap` does support the required behavior: the final page can be mapped without extending the file, with EOF tail bytes zero-filled and whole pages beyond EOF delivering `SIGBUS`.

Observed Darwin/Linux behavior validated by probe programs:

- Partial EOF tail bytes are initially zero-filled.
- `MAP_SHARED` writes to the tail area are visible to other shared mappings of the same page.
- Those tail writes do not become file contents via `msync`.
- Later `ftruncate` growth does not resurrect prior tail writes as persisted file data.
- Access to a page fully beyond EOF triggers `SIGBUS`.

Important design choice:

- If a host-mapped tail page remains cached and a later guest shared mapping reuses it, EOF-tail bytes may be observed again.
- This is accepted for Darwin/Linux because it matches the validated host behavior and Linux permits this style of page-cache-visible tail state.
- We do not sanitize or zero that state on reuse.

Pseudocode:

```csharp
if (!isTailPage)
    return MapWithMemoryMappedFileWindow();

if (!geometry.SupportsDirectMappedTailPage)
    return false; // buffered fallback

return MapWithNativeUnixMmapWindow();
```

### 6.2 SysV Shared Memory (shmget/shmat)

SysV `shmget` creates a long-lived `MemoryObject` held by the `SysVShmManager`.
`shmat` simply injects a `VMA` into the caller's address space pointing to this `MemoryObject`. Detaching (`shmdt`) removes the VMA, but the memory persists until `shmctl(IPC_RMID)` is called.

---

## 7. Fork and Memory Cloning

| Type | Clone Action | Native Action |
|------|--------------|---------------|
| `MAP_PRIVATE` (Anon) | `MemoryObject.ForkCloneForPrivate()` → Eager deep copy host pages. | Child inherited native mappings are torn down and rebuilt on demand from the cloned managed memory objects |
| `MAP_PRIVATE` (File) | Copy `CowObject` pages. `MemoryObject` (Inode Cache) remains shared. | Child inherited native mappings are torn down and rebuilt on demand; writable private snapshot pages are imported into the child COW object before teardown |
| `MAP_SHARED` | `MemoryObject.AddRef()`. | Child inherited native mappings are torn down and rebuilt on demand from managed VMA / MemoryObject state |

---

## 8. Futexes and Shared Memory

A Futex (Fast Userspace muTEX) involves waiting on a specific memory address. For shared futexes across processes, the wait queue must be keyed by the **Host Physical Pointer**, not the guest virtual address.

**Shared Futex Resolution (WAIT path):**
```csharp
// 1. Read the current futex value; this also faults the page in
sm.Engine.CopyFromUser(uaddr, valueBytes);

// 2. Fetch the Host Physical Pointer to use as a global cross-process key
var hostPtr = sm.Engine.GetPhysicalAddressSafe(uaddr, false);
nint sharedKey = (nint)hostPtr;

// 3. Queue the waiter globally
sm.Futex.PrepareWaitShared(sharedKey);
```

For `FUTEX_WAKE`, the implementation first tries to fault/touch the page so a shared host key can be resolved in the current engine. If that still fails, wake falls back to the private virtual-address key as a best-effort path instead of crashing.

This guarantees that two processes with different virtual addresses mapping the same `MAP_SHARED` file wake each other up correctly once the shared page is actually mapped.

---

## 9. munmap and msync

### munmap

`VMAManager.Munmap` does four things:
1. **`engine.ResetCodeCacheByRange`**: Resets per-engine translated code for the affected range.
2. **`engine.MemUnmap`**: Removes mappings from the native MMU.
3. **`ExternalPages.ReleaseRange`**: Decrements `GlobalRefs`, potentially freeing memory.
4. Truncates or deletes the `VMA`, releasing `MemoryObject` / `CowObject` references as needed.

### Syncing to Disk (`msync` / `munmap`)

When a `MAP_SHARED` file mapping is unmapped or `msync`'d, `SyncVMA` checks the native MMU's `Dirty` bit for every page.
Dirty pages are synchronized in two tiers:

- If the filesystem exposes a direct host-mapped flush path, `SyncVMA` asks the inode to flush that mapped page directly.
- Otherwise, `SyncVMA` copies page contents back from the guest mapping and writes them to the inode.

`SyncVMA` strictly respects `FileBackingLength` using exact absolute and relative offsets to prevent out-of-bounds file I/O. Pages fully beyond the current backing range are not written back.

For host-mapped pages, `SyncVMA` first tries filesystem-specific direct flush:

- `HostFS` / `SilkFS` call `TryFlushMappedPage(pageIndex)`.
- If the page is backed by a host mapping window, the backend flushes the host view directly.
- If no direct-flush path exists, `SyncVMA` falls back to the normal page-copy writeback path.
