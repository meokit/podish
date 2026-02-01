using System.Collections.Generic;
using Bifrost.Core;

namespace Bifrost.Memory;

public class VMAManager
{
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

    public uint Mmap(uint addr, uint len, Protection perms, MapFlags flags, FileStream? file, long offset, long filesz, string name)
    {
        // Align to 4k
        if ((addr & 0xFFF) != 0)
            throw new ArgumentException("Address not aligned");

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
                Munmap(addr, len);
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

    public void Munmap(uint addr, uint length)
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

    private uint FindFreeRegion(uint size)
    {
        uint baseAddr = 0x10000;
        while (true)
        {
            uint end = baseAddr + size;
            if (!CheckOverlap(baseAddr, end))
                return baseAddr;
            baseAddr += 0x1000;
            if (baseAddr >= 0xC0000000)
                return 0;
        }
    }

    public bool HandleFault(uint addr, bool isWrite, Engine engine)
    {
        var vma = FindVMA(addr);
        if (vma == null) return false;

        if (isWrite && (vma.Perms & Protection.Write) == 0)
            return false;

        uint pageStart = addr & ~0xFFFu;
        Protection tempPerms = vma.Perms | Protection.Write;
        
        engine.MemMap(pageStart, 4096, (byte)tempPerms);

        if (vma.File != null)
        {
            long vmaOffset = pageStart - vma.Start;
            long off = vma.Offset + vmaOffset;

            int readLen = 4096;
            if (vma.FileSz > 0)
            {
                long remainingFile = vma.FileSz - vmaOffset;
                if (remainingFile <= 0)
                    readLen = 0;
                else if (remainingFile < 4096)
                    readLen = (int)remainingFile;
            }

            if (readLen > 0)
            {
                Span<byte> buf = stackalloc byte[4096];
                vma.File.Seek(off, SeekOrigin.Begin);
                int n = vma.File.Read(buf.Slice(0, readLen));
                if (n > 0)
                {
                    engine.MemWrite(pageStart, buf.Slice(0, n));
                }
            }
        }

        if (tempPerms != vma.Perms)
        {
            engine.MemMap(pageStart, 4096, (byte)vma.Perms);
        }

        return true;
    }
}
