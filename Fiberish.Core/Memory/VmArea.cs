using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Memory;

[Flags]
public enum Protection
{
    None = 0,
    Read = 1,
    Write = 2,
    Exec = 4
}

[Flags]
public enum MapFlags
{
    Shared = 0x01,
    Private = 0x02,
    Fixed = 0x10,
    Anonymous = 0x20,
    GrowDown = 0x0100,
    Stack = 0x20000,
    FixedNoReplace = 0x100000
}

public class VmArea
{
    public uint Start { get; set; }
    public uint End { get; set; } // Exclusive
    public Protection Perms { get; set; }
    public MapFlags Flags { get; set; }
    public VmaFileMapping? FileMapping { get; set; }
    public LinuxFile? File => FileMapping?.File;
    public long Offset { get; set; }
    public ulong VmPgoff { get; set; }

    public string Name { get; set; } = string.Empty;
    public AddressSpace? VmMapping { get; set; }

    /// <summary>
    ///     Holds per-process private pages for MAP_PRIVATE mappings.
    ///     Null for MAP_SHARED and other non-private mappings.
    /// </summary>
    public AnonVma? VmAnonVma { get; set; }

    public uint Length => End - Start;

    public uint VmStart
    {
        get => Start;
        set => Start = value;
    }

    public uint VmEnd
    {
        get => End;
        set => End = value;
    }

    public MapFlags VmFlags
    {
        get => Flags;
        set => Flags = value;
    }

    public Protection VmPageProt
    {
        get => Perms;
        set => Perms = value;
    }

    public LinuxFile? VmFile => File;
    public AddressSpace? VmAddressSpace => VmMapping;

    public bool IsFileBacked => File != null;
    public bool IsPrivateMapping => (Flags & MapFlags.Private) != 0 && VmAnonVma != null;

    public uint GetPageIndex(uint guestPageStart)
    {
        return checked((uint)VmPgoff) + (guestPageStart - Start) / LinuxConstants.PageSize;
    }

    public long GetRelativeOffsetForAddress(uint guestAddr)
    {
        return (long)guestAddr - Start;
    }

    public long GetRelativeOffsetForPageIndex(uint pageIndex)
    {
        return (long)(pageIndex - checked((uint)VmPgoff)) * LinuxConstants.PageSize;
    }

    public uint GetGuestPageStart(uint pageIndex)
    {
        return Start + (uint)GetRelativeOffsetForPageIndex(pageIndex);
    }

    public long GetAbsoluteFileOffsetForPageIndex(uint pageIndex)
    {
        return Offset + GetRelativeOffsetForPageIndex(pageIndex);
    }

    public long GetFileBackingLength()
    {
        if (!IsFileBacked) return 0;

        var inodeSize = (long)(File?.OpenedInode?.Size ?? 0);
        var remaining = inodeSize - Offset;
        if (remaining <= 0) return 0;
        return Math.Min(remaining, Length);
    }

    public long GetRemainingBackingBytes(long relativeOffset)
    {
        return GetFileBackingLength() - relativeOffset;
    }

    public int GetReadLengthForRelativeOffset(long relativeOffset)
    {
        if (GetFileBackingLength() <= 0)
            return LinuxConstants.PageSize;

        var remainingBackingBytes = GetRemainingBackingBytes(relativeOffset);
        if (remainingBackingBytes <= 0)
            return 0;
        if (remainingBackingBytes < LinuxConstants.PageSize)
            return (int)remainingBackingBytes;
        return LinuxConstants.PageSize;
    }

    public VmArea Clone()
    {
        VmMapping?.AddRef();
        // Private pages are shared page-for-page across fork and split lazily on the next write.
        var privateObj = VmAnonVma?.CloneForFork();
        var clonedFileMapping = FileMapping?.AddRef();

        return new VmArea
        {
            Start = Start,
            End = End,
            Perms = Perms,
            Flags = Flags,
            FileMapping = clonedFileMapping,
            Offset = Offset,
            VmPgoff = VmPgoff,
            Name = Name,
            VmMapping = VmMapping,
            VmAnonVma = privateObj
        };
    }
}

public sealed class VmaFileMapping
{
    private int _refCount = 1;

    public VmaFileMapping(LinuxFile file)
    {
        File = file;
    }

    public LinuxFile File { get; }

    public VmaFileMapping AddRef()
    {
        Interlocked.Increment(ref _refCount);
        return this;
    }

    public void Release()
    {
        var remaining = Interlocked.Decrement(ref _refCount);
        if (remaining > 0) return;
        if (remaining == 0)
        {
            File.Close();
            return;
        }

        VfsDebugTrace.FailInvariant($"VmaFileMapping refcount underflow file={File.Dentry.Name}");
    }
}
