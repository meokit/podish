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

### 2.3 Fork Deep Copy Behavior

When `X86_Clone(parent, share_mem=false)` is invoked (the fork path), the `PageDirectory` performs a **deep copy**. All pages—including external ones—are memcpy'd into new private native allocations.
Because this breaks C# shared memory semantics, `FiberTask.Clone()` loops over all `MAP_SHARED` VMAs and explicitly calls `MemUnmap()` on the new native CPU instance. This clears the bogus private copies, forcing a fault upon the next access so the child maps the exact same host pointer as the parent.

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
        // MAP_SHARED or MAP_PRIVATE file: The MemoryObject is shared across processes (e.g. Inode page cache).
        MemoryObject.AddRef();
        obj = MemoryObject;
    } else {
        // MAP_PRIVATE anonymous: Strictly isolated, perform a deep copy.
        obj = MemoryObject.ForkCloneForPrivate();
    }

    // Deep copy any existing COW pages for child isolation.
    MemoryObject? cowObj = CowObject?.ForkCloneForPrivate();

    return new VMA { ..., MemoryObject = obj, CowObject = cowObj };
}
```

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

The `ExternalPageManager` per `SyscallManager` tracks which host pages have been mapped into the native MMU. A static `GlobalRefs` dictionary (`nint → int`) maintains the global reference count of each host pointer.
A host page is only `NativeMemory.AlignedFree`'d when its `GlobalRefs` drops to 0.

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
    var key = $"pagecache:{HashCode(inode.SuperBlock)}:{inode.Ino}";
    var obj = CreateOrOpenNamed(key, ...);
    inode.PageCache ??= obj;
    return obj;
}
```

Any `mmap(MAP_SHARED)` on the same file receives this identical `MemoryObject`. Writes from one process immediately affect the physical pages, becoming instantly visible to all other processes mapping the file.

### 6.1.1 HostFS / SilkFS Host-Mapped Page Cache Policy

For `HostFS` and `SilkFS`, clean file-backed pages may be backed directly by a host file mapping instead of an emulator-owned anonymous page. This is an optimization, but it must not change guest-visible file semantics.

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
| `MAP_PRIVATE` (Anon) | `MemoryObject.ForkCloneForPrivate()` → Eager deep copy host pages. | Deep copy `PageDirectory` |
| `MAP_PRIVATE` (File) | Copy `CowObject` pages. `MemoryObject` (Inode Cache) remains shared. | Deep copy `PageDirectory` |
| `MAP_SHARED` | `MemoryObject.AddRef()`. | `PageDirectory` deep copy **(Cleared by C# via `MemUnmap`)** |

---

## 8. Futexes and Shared Memory

A Futex (Fast Userspace muTEX) involves waiting on a specific memory address. For shared futexes across processes, the wait queue must be keyed by the **Host Physical Pointer**, not the guest virtual address.

**Shared Futex Resolution:**
```csharp
// 1. Force a page fault to guarantee the page is mapped in the native MMU
sm.Engine.CopyFromUser(uaddr, new byte[1]);

// 2. Fetch the Host Physical Pointer to use as a global cross-process key
var hostPtr = sm.Engine.GetPhysicalAddressSafe(uaddr, false);
nint sharedKey = (nint)hostPtr;

// 3. Queue the waiter globally
sm.Futex.PrepareWaitShared(sharedKey);
```

This guarantees that two processes with different virtual addresses mapping the same `MAP_SHARED` file will wake each other up correctly.

---

## 9. munmap and msync

### munmap

`VMAManager.Munmap` does four things:
1. **`engine.InvalidateRange`**: Clears JIT block caches to prevent SMC bugs.
2. **`engine.MemUnmap`**: Removes mappings from the native MMU.
3. **`ExternalPages.ReleaseRange`**: Decrements `GlobalRefs`, potentially freeing memory.
4. Truncates or deletes the `VMA`, calling `Release()` on the `MemoryObject` and `CowObject`.

### Syncing to Disk (`msync` / `munmap`)

When a `MAP_SHARED` file mapping is unmapped or `msync`'d, `SyncVMA` checks the Native MMU's `Dirty` bit for every page.
Dirty pages are extracted via `CopyFromUser` and written back to the `Inode`. `SyncVMA` strictly respects `FileBackingLength` using exact absolute and relative offsets to prevent out-of-bounds file I/O.

For host-mapped pages, `SyncVMA` first tries filesystem-specific direct flush:

- `HostFS` / `SilkFS` call `TryFlushMappedPage(pageIndex)`.
- If the page is backed by a host mapping window, the backend flushes the host view directly.
- If no direct-flush path exists, `SyncVMA` falls back to the normal page-copy writeback path.
