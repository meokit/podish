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

        var vma = new VMA
        {
            Start = addr,
            End = end,
            Perms = perms,
            Flags = flags,
            File = file,
            Offset = offset,
            FileSz = filesz,
            Name = name,
            MemoryObject = file == null
                ? MemoryObjectManager.Instance.CreateAnonymous((flags & MapFlags.Shared) != 0)
                : MemoryObjectManager.Instance.CreateFile(file, offset, filesz, (flags & MapFlags.Shared) != 0),
            ViewPageOffset = 0
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
        var newMM = new VMAManager();
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
        engine.InvalidateRange(addr, length);
        engine.MemUnmap(addr, length);
        ExternalPages.ReleaseRange(addr, length);

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
                SyncVMA(vma, engine);
                vma.MemoryObject.Release();
                _vmas.RemoveAt(i--);
                continue;
            }

            // Split (Middle removal)
            if (addr > vma.Start && end < vma.End)
            {
                var tailStart = end;
                var tailEnd = vma.End;
                long tailOffset = 0;
                long tailFileSz = 0;

                if (vma.File != null)
                {
                    long diff = tailStart - vma.Start;
                    tailOffset = vma.Offset + diff;
                    if (vma.FileSz > diff)
                        tailFileSz = vma.FileSz - diff;
                }

                var tailVMA = new VMA
                {
                    Start = tailStart,
                    End = tailEnd,
                    Perms = vma.Perms,
                    Flags = vma.Flags,
                    File = vma.File,
                    Offset = tailOffset,
                    FileSz = tailFileSz,
                    Name = vma.Name,
                    MemoryObject = vma.MemoryObject,
                    ViewPageOffset = vma.ViewPageOffset + ((tailStart - vma.Start) / LinuxConstants.PageSize)
                };
                vma.MemoryObject.AddRef();

                _vmas.Insert(i + 1, tailVMA);

                // Truncate current (head)
                vma.End = addr;
                if (vma.File != null)
                {
                    long newLen = vma.End - vma.Start;
                    if (vma.FileSz > newLen)
                        vma.FileSz = newLen;
                }

                continue;
            }

            // Head removal
            if (addr <= vma.Start && end < vma.End)
            {
                var diff = end - vma.Start;
                vma.Start = end;
                vma.ViewPageOffset += diff / LinuxConstants.PageSize;
                if (vma.File != null)
                {
                    vma.Offset += diff;
                    if (vma.FileSz > diff)
                        vma.FileSz -= diff;
                    else
                        vma.FileSz = 0;
                }

                continue;
            }

            // Tail removal
            if (addr > vma.Start && end >= vma.End)
            {
                vma.End = addr;
                if (vma.File != null)
                {
                    long newLen = vma.End - vma.Start;
                    if (vma.FileSz > newLen)
                        vma.FileSz = newLen;
                }
            }
        }
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
            Logger.LogTrace("No VMA found for address 0x{Addr:x}", addr);
            return false;
        }

        if (isWrite && (vma.Perms & Protection.Write) == 0)
        {
            Logger.LogTrace("Write fault on read-only VMA: {VmaName} at 0x{Addr:x}", vma.Name, addr);
            return false;
        }

        var pageStart = addr & LinuxConstants.PageMask;
        var tempPerms = vma.Perms | Protection.Write;
        var pageIndex = vma.ViewPageOffset + ((pageStart - vma.Start) / LinuxConstants.PageSize);

        IntPtr hostPtr;
        hostPtr = vma.MemoryObject.GetOrCreatePage(pageIndex, ptr =>
        {
            if (vma.File == null) return true;

            long vmaOffset = pageStart - vma.Start;
            var off = vma.Offset + vmaOffset;

            var readLen = LinuxConstants.PageSize;
            if (vma.FileSz > 0)
            {
                var remainingFile = vma.FileSz - vmaOffset;
                if (remainingFile <= 0)
                    readLen = 0;
                else if (remainingFile < LinuxConstants.PageSize)
                    readLen = (int)remainingFile;
            }

            if (readLen <= 0) return true;

            unsafe
            {
                Span<byte> buf = new((void*)ptr, LinuxConstants.PageSize);
                var n = vma.File.Dentry.Inode!.Read(vma.File, buf[..readLen], off);
                Logger.LogDebug("HandleFault: Read {N} bytes from file at offset {Off}", n, off);
            }
            return true;
        }, out _);
        if (hostPtr == IntPtr.Zero)
        {
            Logger.LogError("HandleFault: object page allocation failed for 0x{PageStart:x}", pageStart);
            return false;
        }

        if (!ExternalPages.AddMapping(pageStart, hostPtr, out var addedRef))
        {
            Logger.LogError("HandleFault: page mapping mismatch for 0x{PageStart:x}", pageStart);
            return false;
        }

        if (!engine.MapExternalPage(pageStart, hostPtr, (byte)tempPerms))
        {
            if (addedRef) ExternalPages.Release(pageStart);
            Logger.LogError("HandleFault: MapExternalPage failed for 0x{PageStart:x}", pageStart);
            return false;
        }

        // Set final permissions
        if (tempPerms != vma.Perms) engine.MemMap(pageStart, LinuxConstants.PageSize, (byte)vma.Perms);

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
        if (vma.File == null || (vma.Flags & MapFlags.Shared) == 0)
            return;

        for (var page = vma.Start; page < vma.End; page += LinuxConstants.PageSize)
            if (engine.IsDirty(page))
            {
                // Write back dirty page
                var data = new byte[LinuxConstants.PageSize];
                if (!engine.CopyFromUser(page, data)) continue; // Skip if fault? Or handle error?

                long vmaOffset = page - vma.Start;
                var off = vma.Offset + vmaOffset;

                var writeLen = LinuxConstants.PageSize;
                if (vma.FileSz > 0)
                {
                    var remainingFile = vma.FileSz - vmaOffset;
                    if (remainingFile <= 0)
                        writeLen = 0;
                    else if (remainingFile < LinuxConstants.PageSize)
                        writeLen = (int)remainingFile;
                }

                if (writeLen > 0) vma.File.Dentry.Inode!.Write(vma.File, data.AsSpan(0, writeLen), off);
            }
    }

    public void LogVMAs()
    {
        Logger.LogInformation("Memory Map:");
        foreach (var vma in _vmas)
            Logger.LogInformation("0x{Start:x8}-0x{End:x8} {Perms} {Flags} {Name}", vma.Start, vma.End, vma.Perms,
                vma.Flags, vma.Name);
    }
}
