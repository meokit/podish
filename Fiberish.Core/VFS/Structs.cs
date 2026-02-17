namespace Fiberish.VFS;

public enum InodeType
{
    Unknown = 0,
    File = 0x8000,
    Directory = 0x4000,
    Symlink = 0xA000,
    CharDev = 0x2000,
    BlockDev = 0x6000,
    Fifo = 0x1000,
    Socket = 0xC000
}

[Flags]
public enum FileFlags
{
    O_RDONLY = 0,
    O_WRONLY = 1,
    O_RDWR = 2,
    O_CREAT = 64,
    O_EXCL = 128,
    O_NOCTTY = 256,
    O_TRUNC = 512,
    O_APPEND = 1024,
    O_NONBLOCK = 2048,
    O_DIRECTORY = 65536,
    O_CLOEXEC = 524288
}

public abstract class FileSystem
{
    public string Name { get; set; } = "";
    public abstract SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data);
}

public class FileSystemType
{
    public string Name { get; set; } = "";
    public FileSystem FileSystem { get; set; } = null!;
}

public abstract class SuperBlock
{
    protected HashSet<Inode> AllInodes = [];
    public FileSystemType Type { get; set; } = null!;
    public Dentry Root { get; set; } = null!;
    public int BlockSize { get; set; } = 4096;
    public List<Inode> Inodes { get; set; } = [];
    public object Lock { get; } = new();

    // Reference counting for lifecycle management
    public int RefCount { get; set; }

    public void Get()
    {
        RefCount++;
    }

    public void Put()
    {
        if (--RefCount <= 0) Shutdown();
    }

    public bool HasActiveInodes()
    {
        return AllInodes.Any(i => i.RefCount > i.Dentries.Count);
    }

    protected virtual void Shutdown()
    {
        // Subclasses can override to clean up resources
        AllInodes.Clear();
        Inodes.Clear();
    }

    public virtual Inode AllocInode()
    {
        throw new NotSupportedException();
    }

    public virtual void WriteInode(Inode inode)
    {
    }
}

public abstract class Inode
{
    public ulong Ino { get; set; }
    public InodeType Type { get; set; }
    public int Mode { get; set; }
    public int Uid { get; set; }
    public int Gid { get; set; }
    public ulong Size { get; set; }
    public DateTime MTime { get; set; }
    public DateTime ATime { get; set; }
    public DateTime CTime { get; set; }

    public SuperBlock SuperBlock { get; set; } = null!;

    // All dentries pointing to this inode (hard links)
    public List<Dentry> Dentries { get; } = [];
    public object Lock { get; } = new();

    // Reference counting for lifecycle management
    public int RefCount { get; set; }

    // Reference counting methods
    public void Get()
    {
        RefCount++;
    }

    public void Put()
    {
        if (--RefCount <= 0) Release();
    }

    protected virtual void Release()
    {
        // Subclasses can override to clean up resources
    }

    // Operations
    public virtual Dentry? Lookup(string name)
    {
        return null;
    }

    public virtual Dentry Create(Dentry dentry, int mode, int uid, int gid)
    {
        throw new NotSupportedException();
    }

    public virtual Dentry Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        throw new NotSupportedException();
    }

    public virtual void Unlink(string name)
    {
        throw new NotSupportedException();
    }

    public virtual void Rmdir(string name)
    {
        throw new NotSupportedException();
    }

    public virtual Dentry Link(Dentry dentry, Inode oldInode)
    {
        throw new NotSupportedException();
    }

    public virtual void Rename(string oldName, Inode newParent, string newName)
    {
        throw new NotSupportedException();
    }

    public virtual Dentry Symlink(Dentry dentry, string target, int uid, int gid)
    {
        throw new NotSupportedException();
    }

    public virtual string Readlink()
    {
        throw new NotSupportedException();
    }

    public virtual int Read(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        return 0;
    }

    public virtual int Write(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        return 0;
    }

    // Async blocking support
    public virtual ValueTask WaitForRead(LinuxFile linuxFile)
    {
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask WaitForWrite(LinuxFile linuxFile)
    {
        return ValueTask.CompletedTask;
    }

    public virtual void Truncate(long size)
    {
        throw new NotSupportedException();
    }

    // File operations hooks
    public virtual void Open(LinuxFile linuxFile)
    {
    }

    public virtual void Release(LinuxFile linuxFile)
    {
    }

    public virtual void Sync(LinuxFile linuxFile)
    {
    }

    // For directories, we need iteration. 
    public virtual List<DirectoryEntry> GetEntries()
    {
        return new List<DirectoryEntry>();
    }
}

public struct DirectoryEntry
{
    public string Name;
    public ulong Ino;
    public InodeType Type;
}

public class Dentry
{
    private static long _nextId;

    public Dentry(string name, Inode? inode, Dentry? parent, SuperBlock sb)
    {
        Name = name;
        Inode = inode;
        Parent = parent;
        SuperBlock = sb;
        if (inode != null)
        {
            inode.Dentries.Add(this);
            inode.Get();
        }
    }

    public long Id { get; } = Interlocked.Increment(ref _nextId);

    public string Name { get; set; }
    public Inode? Inode { get; set; }
    public Dentry? Parent { get; set; }
    public SuperBlock SuperBlock { get; set; }
    public Dictionary<string, Dentry> Children { get; } = [];

    // Mount point support
    public bool IsMounted { get; set; }
    public Dentry? MountRoot { get; set; }

    // Back pointer for mount traversal (points to the Dentry in parent FS where this root is mounted)
    public Dentry? MountedAt { get; set; }

    public void Instantiate(Inode inode)
    {
        if (Inode != null) throw new InvalidOperationException("Dentry already instantiated");
        Inode = inode;
        Inode.Dentries.Add(this);
        Inode.Get();
    }
}

public class LinuxFile
{
    private bool _isClosed;

    public LinuxFile(Dentry dentry, FileFlags flags)
    {
        Dentry = dentry;
        Flags = flags;
        dentry.Inode?.Get(); // Increase reference count
        dentry.Inode?.Open(this);
    }

    public Dentry Dentry { get; set; }
    public long Position { get; set; }
    public FileFlags Flags { get; set; }
    public object? PrivateData { get; set; }

    public virtual void Close()
    {
        if (_isClosed) return;
        _isClosed = true;

        Dentry.Inode?.Release(this);
        Dentry.Inode?.Put(); // Decrease reference count
    }

    public virtual int Read(Span<byte> buffer)
    {
        var n = Dentry.Inode!.Read(this, buffer, Position);
        if (n > 0) Position += n;
        return n;
    }

    public virtual int Write(ReadOnlySpan<byte> buffer)
    {
        var n = Dentry.Inode!.Write(this, buffer, Position);
        if (n > 0) Position += n;
        return n;
    }

    public virtual ValueTask WaitForRead()
    {
        return Dentry.Inode!.WaitForRead(this);
    }

    public virtual ValueTask WaitForWrite()
    {
        return Dentry.Inode!.WaitForWrite(this);
    }

    public virtual void Sync()
    {
        Dentry.Inode?.Sync(this);
    }

    /// <summary>
    ///     Check for readiness. Returns a bitmask of POLL* constants.
    ///     Default implementation for regular files: Always readable and writable.
    /// </summary>
    public virtual short Poll(short events)
    {
        // Regular files are always ready for read/write
        // See Linux: fs/select.c
        short revents = 0;
        if ((events & 0x0001) != 0) revents |= 0x0001; // POLLIN
        if ((events & 0x0004) != 0) revents |= 0x0004; // POLLOUT
        return revents;
    }
}

public struct DCacheKey(long parentId, string name) : IEquatable<DCacheKey>
{
    public long ParentId = parentId;
    public string Name = name;

    public readonly bool Equals(DCacheKey other)
    {
        return ParentId == other.ParentId && Name == other.Name;
    }

    public override bool Equals(object? obj)
    {
        return obj is DCacheKey other && Equals(other);
    }

    public readonly override int GetHashCode()
    {
        return HashCode.Combine(ParentId, Name);
    }

    public static bool operator ==(DCacheKey left, DCacheKey right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(DCacheKey left, DCacheKey right)
    {
        return !(left == right);
    }
}