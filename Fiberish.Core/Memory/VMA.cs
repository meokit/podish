using Fiberish.VFS;

namespace Fiberish.Memory;

public enum Protection
{
    None = 0,
    Read = 1,
    Write = 2,
    Exec = 4
}

public enum MapFlags
{
    Shared = 0x01,
    Private = 0x02,
    Fixed = 0x10,
    Anonymous = 0x20
}

public class VMA
{
    public uint Start { get; set; }
    public uint End { get; set; } // Exclusive
    public Protection Perms { get; set; }
    public MapFlags Flags { get; set; }
    public LinuxFile? File { get; set; }
    public long Offset { get; set; }
    public long FileSz { get; set; } // Max bytes to read from file relative to Start
    public string Name { get; set; } = string.Empty;

    public uint Length => End - Start;

    public VMA Clone()
    {
        return new VMA
        {
            Start = Start,
            End = End,
            Perms = Perms,
            Flags = Flags,
            File = File, // File object is shared (like os.File in Go)
            Offset = Offset,
            FileSz = FileSz,
            Name = Name
        };
    }
}