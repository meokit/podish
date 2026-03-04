using Fiberish.Core;
using Fiberish.Diagnostics;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Memory;

public class VMAManager
{
    private static readonly ILogger Logger = Logging.CreateLogger<VMAManager>();
    private readonly List<VMA> _vmas = [];
    public ExternalPageManager ExternalPages { get; } = new();
    public MemoryObjectManager MemoryObjects { get; }

    public VMAManager(MemoryObjectManager? memoryObjects = null)
    {
        MemoryObjects = memoryObjects ?? new MemoryObjectManager();
    }

    public IReadOnlyList<VMA> VMAs => _vmas;

    public VMA? FindVMA(uint addr)
    {
        foreach (var vma in _vmas)
            if (addr >= vma.Start && addr < vma.End)
                return vma;
        return null;
    }

    public List<VMA> FindVMAsInRange(uint start, uint end)
    {
        var result = new List<VMA>();
        foreach (var vma in _vmas)
            if (vma.Start < end && vma.End > start)
                result.Add(vma);
        return result;
    }

    public uint Mmap(uint addr, uint len, Protection perms, MapFlags flags, LinuxFile? file, long offset, long filesz,
        string name, Engine engine)
    {
        // Align to 4k
        if ((addr & LinuxConstants.PageOffsetMask) != 0)
            throw new ArgumentException("Address not aligned");

        // Round up len to 4k
        len = (len + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;

        if (addr == 0)
        {
            addr = FindFreeRegion(len);
            if (addr == 0)
                throw new OutOfMemoryException("Execution out of memory");
        }

        var end = addr + len;
        if (CheckOverlap(addr, end))
        {
            if ((flags & MapFlags.Fixed) != 0)
                Munmap(addr, len, engine);
            else
                throw new InvalidOperationException("Overlap detected");
        }

        var isShared = (flags & MapFlags.Shared) != 0;
        MemoryObject memObj;
        MemoryObject? cowObj = null;
        uint viewPageOff = 0;

        if (file == null)
        {
            // Anonymous mapping
            memObj = MemoryObjects.CreateAnonymous(isShared);
        }
        else if (isShared)
        {
            // MAP_SHARED file: share the inode's global page cache
            memObj = MemoryObjects.GetOrCreateInodePageCache(file.Dentry.Inode!);
            viewPageOff = (uint)(offset / LinuxConstants.PageSize);
        }
        else
        {
            // MAP_PRIVATE file (including ELF_LOAD): shared inode cache as read-only source + private COW object
            memObj = MemoryObjects.GetOrCreateInodePageCache(file.Dentry.Inode!);
            viewPageOff = (uint)(offset / LinuxConstants.PageSize);
            cowObj = MemoryObjects.CreateAnonymous(shared: false);
        }


        var vma = new VMA
        {
            Start = addr,
            End = end,
            Perms = perms,
            Flags = flags,
            File = file,
            Offset = offset,
            FileBackingLength = filesz,
            Name = name,
            MemoryObject = memObj,
            CowObject = cowObj,
            ViewPageOffset = viewPageOff
        };

        // Insert sorted
        var inserted = false;
        for (var i = 0; i < _vmas.Count; i++)
            if (vma.End <= _vmas[i].Start)
            {
                _vmas.Insert(i, vma);
                inserted = true;
                break;
            }

        if (!inserted) _vmas.Add(vma);

        return addr;
    }

    public VMAManager Clone()
    {
        var newMM = new VMAManager(MemoryObjects);
        foreach (var vma in _vmas) newMM._vmas.Add(vma.Clone());
        return newMM;
    }

    /// <summary>
    /// Add a pre-constructed VMA directly. Used by SysV SHM subsystem.
    /// </summary>
    internal void AddVmaInternal(VMA vma)
    {
        // Insert sorted by start address
        var inserted = false;
        for (var i = 0; i < _vmas.Count; i++)
            if (vma.End <= _vmas[i].Start)
            {
                _vmas.Insert(i, vma);
                inserted = true;
                break;
            }

        if (!inserted) _vmas.Add(vma);
    }

    public void Munmap(uint addr, uint length, Engine engine)
    {
        if (length == 0) return;
        var end = addr + length;
        for (var i = 0; i < _vmas.Count; i++)
        {
            var vma = _vmas[i];

            // No intersection
            if (end <= vma.Start || addr >= vma.End)
                continue;

            // Full removal
            if (addr <= vma.Start && end >= vma.End)
            {
                SyncVMA(vma, engine, vma.Start, vma.End);
                vma.MemoryObject.Release();
                vma.CowObject?.Release();
                _vmas.RemoveAt(i--);
                continue;
            }

            // Split (Middle removal)
            if (addr > vma.Start && end < vma.End)
            {
                SyncVMA(vma, engine, addr, end);
                var tailStart = end;
                var tailEnd = vma.End;
                long tailOffset = 0;
                long tailFileSz = 0;

                if (vma.File != null)
                {
                    long diff = tailStart - vma.Start;
                    tailOffset = vma.Offset + diff;
                    if (vma.FileBackingLength > diff)
                        tailFileSz = vma.FileBackingLength - diff;
                }

                var tailVMA = new VMA
                {
                    Start = tailStart,
                    End = tailEnd,
                    Perms = vma.Perms,
                    Flags = vma.Flags,
                    File = vma.File,
                    Offset = tailOffset,
                    FileBackingLength = tailFileSz,
                    Name = vma.Name,
                    MemoryObject = vma.MemoryObject,
                    CowObject = vma.CowObject?.ForkCloneForPrivate(),
                    ViewPageOffset = vma.ViewPageOffset + ((tailStart - vma.Start) / LinuxConstants.PageSize)
                };
                vma.MemoryObject.AddRef();

                _vmas.Insert(i + 1, tailVMA);

                // Truncate current (head)
                vma.End = addr;
                if (vma.File != null)
                {
                    long newLen = vma.End - vma.Start;
                    if (vma.FileBackingLength > newLen)
                        vma.FileBackingLength = newLen;
                }

                continue;
            }

            // Head removal
            if (addr <= vma.Start && end < vma.End)
            {
                SyncVMA(vma, engine, vma.Start, end);
                var diff = end - vma.Start;
                vma.Start = end;
                vma.ViewPageOffset += diff / LinuxConstants.PageSize;
                if (vma.File != null)
                {
                    vma.Offset += diff;
                    if (vma.FileBackingLength > diff)
                        vma.FileBackingLength -= diff;
                    else
                        vma.FileBackingLength = 0;
                }

                continue;
            }

            // Tail removal
            if (addr > vma.Start && end >= vma.End)
            {
                SyncVMA(vma, engine, addr, vma.End);
                vma.End = addr;
                if (vma.File != null)
                {
                    long newLen = vma.End - vma.Start;
                    if (vma.FileBackingLength > newLen)
                        vma.FileBackingLength = newLen;
                }
            }
        }

        engine.InvalidateRange(addr, length);
        engine.MemUnmap(addr, length);
        ExternalPages.ReleaseRange(addr, length);
    }

    public int Mprotect(uint addr, uint len, Protection prot, Engine engine)
    {
        if (len == 0) return 0;

        var end = addr + len;
        var vmas = FindVMAsInRange(addr, end);
        if (vmas.Count == 0) return -(int)Errno.ENOMEM;

        vmas.Sort((a, b) => a.Start.CompareTo(b.Start));
        var cursor = addr;
        foreach (var vma in vmas)
        {
            if (vma.Start > cursor) return -(int)Errno.ENOMEM;
            if (vma.End > cursor) cursor = vma.End;
            if (cursor >= end) break;
        }

        if (cursor < end) return -(int)Errno.ENOMEM;

        for (var i = 0; i < _vmas.Count; i++)
        {
            var vma = _vmas[i];
            var overlapStart = Math.Max(vma.Start, addr);
            var overlapEnd = Math.Min(vma.End, end);
            if (overlapStart >= overlapEnd) continue;

            var oldStart = vma.Start;
            var oldEnd = vma.End;
            var oldPerms = vma.Perms;
            var oldOffset = vma.Offset;
            var oldFileSz = vma.FileBackingLength;
            var oldViewPageOffset = vma.ViewPageOffset;

            // Fully covered: just flip perms.
            if (overlapStart == oldStart && overlapEnd == oldEnd)
            {
                vma.Perms = prot;
                continue;
            }

            // Prepare middle (the protected slice).
            var midDiff = (long)overlapStart - oldStart;
            var mid = new VMA
            {
                Start = overlapStart,
                End = overlapEnd,
                Perms = prot,
                Flags = vma.Flags,
                File = vma.File,
                Offset = vma.File != null ? oldOffset + midDiff : 0,
                FileBackingLength = 0,
                Name = vma.Name,
                MemoryObject = vma.MemoryObject,
                CowObject = vma.CowObject?.ForkCloneForPrivate(),
                ViewPageOffset = oldViewPageOffset + ((uint)midDiff / LinuxConstants.PageSize)
            };
            vma.MemoryObject.AddRef();
            if (vma.File != null)
            {
                var remain = oldFileSz - midDiff;
                if (remain < 0) remain = 0;
                var midLen = (long)(mid.End - mid.Start);
                mid.FileBackingLength = Math.Min(remain, midLen);
            }

            // Left tail stays in the existing VMA.
            var hasLeft = overlapStart > oldStart;
            var hasRight = overlapEnd < oldEnd;

            if (hasLeft)
            {
                vma.Start = oldStart;
                vma.End = overlapStart;
                vma.Perms = oldPerms;
                if (vma.File != null)
                {
                    var leftLen = (long)(vma.End - vma.Start);
                    vma.FileBackingLength = Math.Min(oldFileSz, leftLen);
                }
            }

            // Right tail (if any) keeps old perms.
            VMA? right = null;
            if (hasRight)
            {
                var rightDiff = (long)overlapEnd - oldStart;
                right = new VMA
                {
                    Start = overlapEnd,
                    End = oldEnd,
                    Perms = oldPerms,
                    Flags = vma.Flags,
                    File = vma.File,
                    Offset = vma.File != null ? oldOffset + rightDiff : 0,
                    FileBackingLength = 0,
                    Name = vma.Name,
                    MemoryObject = vma.MemoryObject,
                    CowObject = vma.CowObject?.ForkCloneForPrivate(),
                    ViewPageOffset = oldViewPageOffset + ((uint)rightDiff / LinuxConstants.PageSize)
                };
                vma.MemoryObject.AddRef();
                if (vma.File != null)
                {
                    var remain = oldFileSz - rightDiff;
                    if (remain < 0) remain = 0;
                    var rightLen = (long)(right.End - right.Start);
                    right.FileBackingLength = Math.Min(remain, rightLen);
                }
            }

            if (!hasLeft)
            {
                // Reuse current slot for middle when there is no left tail.
                vma.Start = mid.Start;
                vma.End = mid.End;
                vma.Perms = mid.Perms;
                vma.File = mid.File;
                vma.Offset = mid.Offset;
                vma.FileBackingLength = mid.FileBackingLength;
                vma.Name = mid.Name;
                vma.ViewPageOffset = mid.ViewPageOffset;
                // Drop the temporary extra ref held by mid.
                mid.MemoryObject.Release();

                if (right != null)
                {
                    _vmas.Insert(i + 1, right);
                    i++;
                }
            }
            else
            {
                // Keep left in current slot; insert middle and optional right.
                _vmas.Insert(i + 1, mid);
                if (right != null)
                    _vmas.Insert(i + 2, right);
                i += right != null ? 2 : 1;
            }
        }

        // Apply native permission changes page-wise for the requested interval.
        for (var p = addr; p < end; p += LinuxConstants.PageSize)
            if (engine.IsDirty(p))
            {
                var v = FindVMA(p);
                if (v != null) engine.MemMap(p, LinuxConstants.PageSize, (byte)v.Perms);
            }

        return 0;
    }

    public void Clear(Engine engine)
    {
        foreach (var vma in _vmas)
        {
            SyncVMA(vma, engine);
            // Clear native memory pages and JIT cache (done in C++ side)
            engine.MemUnmap(vma.Start, vma.End - vma.Start);
            vma.MemoryObject.Release();
        }

        ExternalPages.Clear();
        _vmas.Clear();
    }

    private bool CheckOverlap(uint start, uint end)
    {
        foreach (var vma in _vmas)
            if (start < vma.End && end > vma.Start)
                return true;
        return false;
    }

    public void EagerMap(uint addr, uint len, Engine engine)
    {
        var startPage = addr & LinuxConstants.PageMask;
        var endAddr = addr + len;
        for (var p = startPage; p < endAddr; p += LinuxConstants.PageSize) HandleFault(p, true, engine);
    }

    internal uint FindFreeRegion(uint size)
    {
        var baseAddr = LinuxConstants.MinMmapAddr;
        while (true)
        {
            var end = baseAddr + size;
            if (!CheckOverlap(baseAddr, end))
                return baseAddr;
            baseAddr += LinuxConstants.PageSize;
            if (baseAddr >= LinuxConstants.TaskSize32)
                return 0;
        }
    }

    public bool HandleFault(uint addr, bool isWrite, Engine engine)
    {
        var vma = FindVMA(addr);
        if (vma == null)
        {
            Logger.LogWarning("No VMA found for address 0x{Addr:x}", addr);
            return false;
        }

        var pageStart = addr & LinuxConstants.PageMask;
        var pageIndex = vma.ViewPageOffset + ((pageStart - vma.Start) / LinuxConstants.PageSize);


        // ── COW path: MAP_PRIVATE file mmap ──────────────────────────────────────
        if (vma.CowObject != null)
        {
            if (isWrite)
            {
                var existingCow = vma.CowObject.GetPage(pageIndex);
                if (existingCow != IntPtr.Zero)
                {
                    // Already in CowObject.
                    // 1. Strip Write permission immediately and Yield to flush uTLB across CPUs
                    engine.MemMap(pageStart, LinuxConstants.PageSize, (byte)(vma.Perms & ~Protection.Write));
                    engine.Yield();

                    // 2. Check reference count to see if we have exclusive ownership.
                    // An exclusively mapped COW page will have a global ref count of 2:
                    // 1 reference owned by the vma.CowObject
                    // 1 reference owned by the ExternalPages tracking (current VMA)
                    if (ExternalPageManager.GetRefCount(existingCow) == 2)
                    {
                        // We are the exclusive owner. No need to copy, just upgrade to Writable.
                        engine.MemMap(pageStart, LinuxConstants.PageSize, (byte)vma.Perms);
                        return true;
                    }
                    else
                    {
                        // Page is shared (e.g. after fork). Must Copy-On-Write.
                        var newPage = ExternalPageManager.AllocateExternalPage();
                        if (newPage == IntPtr.Zero) return false;

                        unsafe
                        {
                            Buffer.MemoryCopy((void*)existingCow, (void*)newPage,
                                LinuxConstants.PageSize, LinuxConstants.PageSize);
                        }

                        // Update CowObject and adjust reference counts.
                        // AllocateExternalPage already gives newPage RefCount=1 (owned by CowObject)
                        vma.CowObject.SetPage(pageIndex, newPage);

                        ExternalPageManager.ReleasePtr(existingCow); // Drop CowObject's old ref to existingCow

                        // Remap in Engine
                        ExternalPages.Release(pageStart); // drops old mapping: existingCow loses 1 ref
                        if (!ExternalPages.AddMapping(pageStart, newPage, out _))
                            return false; // newPage gains 1 ref -> Total 2
                        if (!engine.MapExternalPage(pageStart, newPage, (byte)vma.Perms)) return false;
                        return true;
                    }
                }
                else
                {
                    // Not in CowObject yet. We need to copy from the inode cache.
                    var srcPage = vma.MemoryObject.GetOrCreatePage(pageIndex, ptr =>
                    {
                        if (vma.File == null)
                        {
                            unsafe
                            {
                                new Span<byte>((void*)ptr, LinuxConstants.PageSize).Clear();
                            }

                            return true;
                        }

                        // 1. Relative coordinate (Relative to VMA Start)
                        long vmaRelativeOffset = (long)(pageIndex - vma.ViewPageOffset) * LinuxConstants.PageSize;
                        // 2. Absolute coordinate (Absolute within the File)
                        var absoluteFileOffset = vma.Offset + vmaRelativeOffset;

                        var readLen = LinuxConstants.PageSize;
                        if (vma.FileBackingLength > 0)
                        {
                            // Relative length - relative offset = valid file bytes remaining to read in this page
                            var remainingBackingBytes = vma.FileBackingLength - vmaRelativeOffset;

                            if (remainingBackingBytes <= 0) readLen = 0;
                            else if (remainingBackingBytes < LinuxConstants.PageSize)
                                readLen = (int)remainingBackingBytes;
                        }

                        unsafe
                        {
                            Span<byte> buf = new((void*)ptr, LinuxConstants.PageSize);
                            var req = new PageIoRequest(pageIndex, absoluteFileOffset, Math.Max(0, readLen));
                            var rc = vma.File.Dentry.Inode!.ReadPage(vma.File, req, buf);
                            if (rc < 0) return false;
                        }

                        return true;
                    }, out _);

                    if (srcPage == IntPtr.Zero) return false;

                    // Allocate private copy from inode cache
                    existingCow = ExternalPageManager.AllocateExternalPage();
                    if (existingCow == IntPtr.Zero) return false;

                    unsafe
                    {
                        Buffer.MemoryCopy((void*)srcPage, (void*)existingCow,
                            LinuxConstants.PageSize, LinuxConstants.PageSize);
                    }

                    // AllocateExternalPage() gives existingCow RefCount=1. 
                    // This first reference is implicitly owned by CowObject.
                    vma.CowObject.SetPage(pageIndex, existingCow);

                    // Replace old read-only mapping with writable COW page
                    ExternalPages.Release(pageStart); // drop old inode cache ref from VMA

                    // Add mapping gives existingCow a +1 ref (Total 2)
                    if (!ExternalPages.AddMapping(pageStart, existingCow, out _)) return false;
                    if (!engine.MapExternalPage(pageStart, existingCow, (byte)vma.Perms)) return false; // full perms
                    return true;
                }
            }
            else
            {
                // Read access: prefer COW page if it exists, otherwise inode cache (read-only map)
                var cowPage = vma.CowObject.GetPage(pageIndex);
                if (cowPage != IntPtr.Zero)
                {
                    if (!ExternalPages.AddMapping(pageStart, cowPage, out var addedRef)) return false;
                    if (!engine.MapExternalPage(pageStart, cowPage, (byte)vma.Perms))
                    {
                        if (addedRef) ExternalPages.Release(pageStart);
                        return false;
                    }

                    return true;
                }
                // Fall through to normal read path, but map READ-ONLY
                // (so a subsequent write will fault back here for COW)
            }
        }

        // ── Existing logic (unchanged) ───────────────────────────────────────────
        if (isWrite && (vma.Perms & Protection.Write) == 0 && vma.CowObject == null)
        {
            Logger.LogTrace("Write fault on read-only VMA: {VmaName} at 0x{Addr:x}", vma.Name, addr);
            return false;
        }

        var mapPerms = (vma.CowObject != null)
            ? (vma.Perms & ~Protection.Write) // read-only: write fault triggers COW
            : vma.Perms;

        var tempPerms = mapPerms | Protection.Write;

        IntPtr hostPtr;
        hostPtr = vma.MemoryObject.GetOrCreatePage(pageIndex, ptr =>
        {
            if (vma.File == null)
            {
                unsafe
                {
                    new Span<byte>((void*)ptr, LinuxConstants.PageSize).Clear();
                }

                return true;
            }

            // For COW vmaps, pageIndex is in file-coordinate space
            // (ViewPageOffset + page-within-vma). Compute file byte offset accordingly.
            // 1. Relative coordinate (Relative to VMA Start)
            long vmaRelativeOffset = (long)(pageIndex - vma.ViewPageOffset) * LinuxConstants.PageSize;
            // 2. Absolute coordinate (Absolute within the File)
            var absoluteFileOffset = vma.Offset + vmaRelativeOffset;

            var readLen = LinuxConstants.PageSize;
            if (vma.FileBackingLength > 0)
            {
                // Relative length - relative offset = valid file bytes remaining to read in this page
                var remainingBackingBytes = vma.FileBackingLength - vmaRelativeOffset;

                if (remainingBackingBytes <= 0) readLen = 0;
                else if (remainingBackingBytes < LinuxConstants.PageSize) readLen = (int)remainingBackingBytes;
            }

            unsafe
            {
                Span<byte> buf = new((void*)ptr, LinuxConstants.PageSize);
                var req = new PageIoRequest(pageIndex, absoluteFileOffset, Math.Max(0, readLen));
                var rc = vma.File.Dentry.Inode!.ReadPage(vma.File, req, buf);
                if (rc < 0) return false;
            }

            return true;
        }, out _);
        if (hostPtr == IntPtr.Zero)
        {
            Logger.LogError("HandleFault: object page allocation failed for 0x{PageStart:x}", pageStart);
            return false;
        }

        if (!ExternalPages.AddMapping(pageStart, hostPtr, out var added))
        {
            Logger.LogError("HandleFault: page mapping mismatch for 0x{PageStart:x}", pageStart);
            return false;
        }

        if (!engine.MapExternalPage(pageStart, hostPtr, (byte)tempPerms))
        {
            if (added) ExternalPages.Release(pageStart);
            Logger.LogError("HandleFault: MapExternalPage failed for 0x{PageStart:x}", pageStart);
            return false;
        }

        // Set final permissions
        if (tempPerms != mapPerms) engine.MemMap(pageStart, LinuxConstants.PageSize, (byte)mapPerms);

        return true;
    }

    public bool MapAnonymousPage(uint addr, Engine engine, Protection perms)
    {
        var pageStart = addr & LinuxConstants.PageMask;
        var hostPtr = ExternalPages.GetOrAllocate(pageStart, out var isNew);
        if (hostPtr == IntPtr.Zero) return false;
        if (!engine.MapExternalPage(pageStart, hostPtr, (byte)perms))
        {
            if (isNew) ExternalPages.Release(pageStart);
            return false;
        }

        return true;
    }

    public void ShareAnonymousSharedPagesTo(VMAManager dest, Engine destEngine)
    {
        // No-op: shared memory is represented by shared MemoryObject and materialized lazily.
    }

    public static void SyncVMA(VMA vma, Engine engine)
    {
        SyncVMA(vma, engine, vma.Start, vma.End);
    }

    public void SyncMappedFile(LinuxFile file, Engine engine)
    {
        if (file.Dentry.Inode == null) return;
        var inode = file.Dentry.Inode;
        var snapshot = _vmas.ToArray();
        foreach (var vma in snapshot)
        {
            if ((vma.Flags & MapFlags.Shared) == 0 || vma.File == null) continue;
            if (!ReferenceEquals(vma.File.Dentry.Inode, inode)) continue;
            SyncVMA(vma, engine);
        }
    }

    public void SyncAllMappedSharedFiles(Engine engine)
    {
        var snapshot = _vmas.ToArray();
        foreach (var vma in snapshot)
        {
            if ((vma.Flags & MapFlags.Shared) == 0 || vma.File == null) continue;
            SyncVMA(vma, engine);
        }
    }

    public static void SyncVMA(VMA vma, Engine engine, uint rangeStart, uint rangeEnd)
    {
        if (vma.File == null || (vma.Flags & MapFlags.Shared) == 0)
            return;

        var syncStart = Math.Max(vma.Start, rangeStart);
        var syncEnd = Math.Min(vma.End, rangeEnd);
        if (syncStart >= syncEnd) return;
        var inode = vma.File.Dentry.Inode;
        if (inode == null) return;

        var startPage = syncStart & LinuxConstants.PageMask;
        var endPage = (syncEnd + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
        if (endPage < startPage) return;
        var startPageIndex = vma.ViewPageOffset + ((startPage - vma.Start) / LinuxConstants.PageSize);
        var endPageIndex = endPage <= startPage
            ? startPageIndex
            : vma.ViewPageOffset + ((endPage - vma.Start) / LinuxConstants.PageSize) - 1;

        for (var page = startPage; page < endPage; page += LinuxConstants.PageSize)
        {
            var pageIndex = vma.ViewPageOffset + ((page - vma.Start) / LinuxConstants.PageSize);
            if (engine.IsDirty(page))
            {
                vma.MemoryObject.MarkDirty(pageIndex);
                inode.SetPageDirty(pageIndex);
            }

            if (!vma.MemoryObject.IsDirty(pageIndex)) continue;
            var pagePtr = vma.MemoryObject.GetPage(pageIndex);
            if (pagePtr == IntPtr.Zero) continue;

            // 1. Relative coordinate (Relative to VMA Start)
            long vmaRelativeOffset = page - vma.Start;
            // 2. Absolute coordinate (Absolute within the File)
            var absoluteFileOffset = vma.Offset + vmaRelativeOffset;

            var writeLen = LinuxConstants.PageSize;
            if (vma.FileBackingLength > 0)
            {
                var remainingBackingBytes = vma.FileBackingLength - vmaRelativeOffset;
                if (remainingBackingBytes <= 0)
                    writeLen = 0;
                else if (remainingBackingBytes < LinuxConstants.PageSize)
                    writeLen = (int)remainingBackingBytes;
            }

            if (writeLen <= 0)
            {
                vma.MemoryObject.ClearDirty(pageIndex);
                continue;
            }

            unsafe
            {
                ReadOnlySpan<byte> pageData = new((void*)pagePtr, LinuxConstants.PageSize);
                var rc = inode.WritePage(vma.File, new PageIoRequest(pageIndex, absoluteFileOffset, writeLen), pageData, true);
                if (rc == 0)
                    vma.MemoryObject.ClearDirty(pageIndex);
            }
        }

        inode.WritePages(vma.File, new WritePagesRequest(startPageIndex, endPageIndex, true));
    }

    public void LogVMAs()
    {
        Logger.LogInformation("Memory Map:");
        foreach (var vma in _vmas)
            Logger.LogInformation("0x{Start:x8}-0x{End:x8} {Perms} {Flags} {Name}", vma.Start, vma.End, vma.Perms,
                vma.Flags, vma.Name);
    }
}
