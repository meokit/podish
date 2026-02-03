using System;
using System.Collections.Generic;
using Bifrost.Core;
using Bifrost.VFS;
using Bifrost.Native;
using Bifrost.Loader;
using Bifrost.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Bifrost.Memory;

public class VMAManager
{
    private static readonly ILogger Logger = Logging.CreateLogger<VMAManager>();
    private readonly List<VMA> _vmas = new();

    public VMA? FindVMA(uint addr)
    {
        foreach (var vma in _vmas)
        {
            if (addr >= vma.Start && addr < vma.End)
                return vma;
        }
        return null;
    }

    public List<VMA> FindVMAsInRange(uint start, uint end)
    {
        var result = new List<VMA>();
        foreach (var vma in _vmas)
        {
            if (vma.Start < end && vma.End > start)
                result.Add(vma);
        }
        return result;
    }

    public uint Mmap(uint addr, uint len, Protection perms, MapFlags flags, Bifrost.VFS.File? file, long offset, long filesz, string name, Engine engine)
    {
        // Align to 4k
        if ((addr & LinuxConstants.PageOffsetMask) != 0)
            throw new ArgumentException("Address not aligned");

        // Round up len to 4k
        len = (len + (uint)LinuxConstants.PageOffsetMask) & (uint)LinuxConstants.PageMask;

        if (addr == 0)
        {
            addr = FindFreeRegion(len);
            if (addr == 0)
                throw new OutOfMemoryException("Execution out of memory");
        }

        uint end = addr + len;
        if (CheckOverlap(addr, end))
        {
            if ((flags & MapFlags.Fixed) != 0)
            {
                Munmap(addr, len, engine);
            }
            else
            {
                throw new InvalidOperationException("Overlap detected");
            }
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
            Name = name
        };

        // Insert sorted
        bool inserted = false;
        for (int i = 0; i < _vmas.Count; i++)
        {
            if (vma.End <= _vmas[i].Start)
            {
                _vmas.Insert(i, vma);
                inserted = true;
                break;
            }
        }
        if (!inserted)
        {
            _vmas.Add(vma);
        }

        return addr;
    }

    public VMAManager Clone()
    {
        var newMM = new VMAManager();
        foreach (var vma in _vmas)
        {
            newMM._vmas.Add(vma.Clone());
        }
        return newMM;
    }

    public void Munmap(uint addr, uint length, Engine engine)
    {
        uint end = addr + length;
        for (int i = 0; i < _vmas.Count; i++)
        {
            var vma = _vmas[i];

            // No intersection
            if (end <= vma.Start || addr >= vma.End)
                continue;

            // Full removal
            if (addr <= vma.Start && end >= vma.End)
            {
                SyncVMA(vma, engine);
                _vmas.RemoveAt(i--);
                continue;
            }

            // Split (Middle removal)
            if (addr > vma.Start && end < vma.End)
            {
                uint tailStart = end;
                uint tailEnd = vma.End;
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
                    Name = vma.Name
                };

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
                uint diff = end - vma.Start;
                vma.Start = end;
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
                continue;
            }
        }
    }

    private bool CheckOverlap(uint start, uint end)
    {
        foreach (var vma in _vmas)
        {
            if (start < vma.End && end > vma.Start)
                return true;
        }
        return false;
    }

    public void EagerMap(uint addr, uint len, Engine engine)
    {
        uint startPage = addr & LinuxConstants.PageMask;
        uint endAddr = addr + len;
        for (uint p = startPage; p < endAddr; p += (uint)LinuxConstants.PageSize)
        {
            HandleFault(p, true, engine);
        }
    }

    private uint FindFreeRegion(uint size)
    {
        uint baseAddr = LinuxConstants.MinMmapAddr;
        while (true)
        {
            uint end = baseAddr + size;
            if (!CheckOverlap(baseAddr, end))
                return baseAddr;
            baseAddr += (uint)LinuxConstants.PageSize;
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

        uint pageStart = addr & LinuxConstants.PageMask;
        Protection tempPerms = vma.Perms | Protection.Write;
        
        engine.MemMap(pageStart, (uint)LinuxConstants.PageSize, (byte)tempPerms);

        if (vma.File != null)
        {
            long vmaOffset = pageStart - vma.Start;
            long off = vma.Offset + vmaOffset;
            Console.WriteLine($"[VMAManager] HandleFault mapping 0x{pageStart:x} from file {vma.Name} off=0x{off:x}");

            int readLen = LinuxConstants.PageSize;
            if (vma.FileSz > 0)
            {
                long remainingFile = vma.FileSz - vmaOffset;
                if (remainingFile <= 0)
                    readLen = 0;
                else if (remainingFile < LinuxConstants.PageSize)
                    readLen = (int)remainingFile;
            }

            if (readLen > 0)
            {
                Span<byte> buf = stackalloc byte[LinuxConstants.PageSize];
                int n = vma.File.Dentry.Inode!.Read(vma.File, buf.Slice(0, readLen), off);
                if (n > 0)
                {
                    engine.MemWrite(pageStart, buf.Slice(0, n));
                }
            }
        }

        if (tempPerms != vma.Perms)
        {
            engine.MemMap(pageStart, (uint)LinuxConstants.PageSize, (byte)vma.Perms);
        }

        return true;
    }

    public void SyncVMA(VMA vma, Engine engine)
    {
        if (vma.File == null || (vma.Flags & MapFlags.Shared) == 0)
            return;

        for (uint page = vma.Start; page < vma.End; page += (uint)LinuxConstants.PageSize)
        {
            if (engine.IsDirty(page))
            {
                // Write back dirty page
                byte[] data = engine.MemRead(page, (uint)LinuxConstants.PageSize);
                
                long vmaOffset = page - vma.Start;
                long off = vma.Offset + vmaOffset;
                
                int writeLen = LinuxConstants.PageSize;
                if (vma.FileSz > 0)
                {
                    long remainingFile = vma.FileSz - vmaOffset;
                    if (remainingFile <= 0)
                        writeLen = 0;
                    else if (remainingFile < LinuxConstants.PageSize)
                        writeLen = (int)remainingFile;
                }

                if (writeLen > 0)
                {
                    vma.File.Dentry.Inode!.Write(vma.File, data.AsSpan(0, writeLen), off);
                }
            }
        }
    }

    public void LogVMAs()
    {
        Logger.LogInformation("Memory Map:");
        foreach (var vma in _vmas)
        {
            Logger.LogInformation("0x{Start:x8}-0x{End:x8} {Perms} {Flags} {Name}", vma.Start, vma.End, vma.Perms, vma.Flags, vma.Name);
        }
    }
}
