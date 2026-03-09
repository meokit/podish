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

public class VMA
{
    public uint Start { get; set; }
    public uint End { get; set; } // Exclusive
    public Protection Perms { get; set; }
    public MapFlags Flags { get; set; }
    public LinuxFile? File { get; set; }
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

        // COW object: private per-process — deep-copy existing COW pages on fork
        MemoryObject? cowObj = CowObject?.ForkCloneForPrivate();
        File?.Get();

        return new VMA
        {
            Start = Start,
            End = End,
            Perms = Perms,
            Flags = Flags,
            File = File, // File object is shared (like os.File in Go)
            Offset = Offset,
            FileBackingLength = FileBackingLength,
            Name = Name,
            MemoryObject = obj,
            CowObject = cowObj,
            ViewPageOffset = ViewPageOffset
        };
    }
}
