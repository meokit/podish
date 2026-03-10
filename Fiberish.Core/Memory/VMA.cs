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
    public MemoryObject MemoryObject { get; set; } = null!;

    /// <summary>
    ///     For MAP_PRIVATE + file: holds COW'd private pages.
    ///     Null for MAP_SHARED, MAP_PRIVATE anon, and threads.
    /// </summary>
    public MemoryObject? CowObject { get; set; }

    public uint ViewPageOffset { get; set; }

    public uint Length => End - Start;

    public VMA Clone()
    {
        var shared = (Flags & MapFlags.Shared) != 0;
        MemoryObject obj;

        if (shared || CowObject != null)
        {
            // Shared VMA: share MemoryObject (MAP_SHARED file, MAP_SHARED anon, SysV shm)
            // MAP_PRIVATE file mmap: MemoryObject IS the inode page cache (shared across processes);
            //   per-process writes go into CowObject, not MemoryObject, so AddRef is correct.
            MemoryObject.AddRef();
            obj = MemoryObject;
        }
        else
        {
            // MAP_PRIVATE anonymous: deep-copy so child is fully isolated from parent
            obj = MemoryObject.ForkCloneForPrivate();
        }

        // COW object: private metadata per-process, but pages are initially shared
        // and split lazily on write fault.
        MemoryObject? cowObj = CowObject?.ForkCloneSharingPages();
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
            MemoryObject = obj,
            CowObject = cowObj,
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
