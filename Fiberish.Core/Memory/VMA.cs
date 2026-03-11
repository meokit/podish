using Fiberish.VFS;
using System.Threading;

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

public class VMA
{
    public uint Start { get; set; }
    public uint End { get; set; } // Exclusive
    public Protection Perms { get; set; }
    public MapFlags Flags { get; set; }
    public VmaFileMapping? FileMapping { get; set; }
    public LinuxFile? File => FileMapping?.File;
    public long Offset { get; set; }

    // Max bytes of valid file data relative to the Start of this VMA. Used for zero-filling BSS and partial pages.
    public long FileBackingLength { get; set; } // Max bytes to read from file relative to Start
    public string Name { get; set; } = string.Empty;
    public MemoryObject SharedObject { get; set; } = null!;

    /// <summary>
    ///     Holds per-process private pages for MAP_PRIVATE mappings.
    ///     Null for MAP_SHARED and other non-private mappings.
    /// </summary>
    public MemoryObject? PrivateObject { get; set; }

    public uint ViewPageOffset { get; set; }

    public uint Length => End - Start;

    public VMA Clone()
    {
        var shared = (Flags & MapFlags.Shared) != 0;
        SharedObject.AddRef();
        // Private pages are shared page-for-page across fork and split lazily on the next write.
        MemoryObject? privateObj = PrivateObject?.ForkCloneSharingPages();
        var clonedFileMapping = FileMapping?.AddRef();

        return new VMA
        {
            Start = Start,
            End = End,
            Perms = Perms,
            Flags = Flags,
            FileMapping = clonedFileMapping,
            Offset = Offset,
            FileBackingLength = FileBackingLength,
            Name = Name,
            SharedObject = SharedObject,
            PrivateObject = privateObj,
            ViewPageOffset = ViewPageOffset
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
