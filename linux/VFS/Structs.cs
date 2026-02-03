using System;
using System.Collections.Generic;

namespace Bifrost.VFS;

public enum InodeType : int
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
public enum FileFlags : int
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
    public FileSystemType Type { get; set; } = null!;
    public Dentry Root { get; set; } = null!;
    public int BlockSize { get; set; } = 4096;
    public List<Inode> Inodes { get; set; } = new();
    public object Lock { get; } = new();

    // Reference counting for lifecycle management
    public int RefCount { get; set; } = 0;
    protected HashSet<Inode> AllInodes = new();

    public void Get() => RefCount++;

    public void Put()
    {
        if (--RefCount <= 0)
        {
            Shutdown();
        }
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

    public virtual Inode AllocInode() => throw new NotSupportedException();
    public virtual void WriteInode(Inode inode) { }
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
    public List<Dentry> Dentries { get; } = new();
    public object Lock { get; } = new();

    // Reference counting for lifecycle management
    public int RefCount { get; set; } = 0;

    // Reference counting methods
    public void Get() => RefCount++;

    public void Put()
    {
        if (--RefCount <= 0)
        {
            Release();
        }
    }

    protected virtual void Release()
    {
        // Subclasses can override to clean up resources
    }

    // Operations
    public virtual Dentry? Lookup(string name) => null;
    public virtual Dentry Create(Dentry dentry, int mode, int uid, int gid) => throw new NotSupportedException();
    public virtual Dentry Mkdir(Dentry dentry, int mode, int uid, int gid) => throw new NotSupportedException();
    public virtual void Unlink(string name) => throw new NotSupportedException();
    public virtual void Rmdir(string name) => throw new NotSupportedException();
    public virtual Dentry Link(Dentry dentry, Inode oldInode) => throw new NotSupportedException();
    public virtual void Rename(string oldName, Inode newParent, string newName) => throw new NotSupportedException();
    public virtual Dentry Symlink(Dentry dentry, string target, int uid, int gid) => throw new NotSupportedException();
    public virtual string Readlink() => throw new NotSupportedException();

    public virtual int Read(File file, Span<byte> buffer, long offset) => 0;
    public virtual int Write(File file, ReadOnlySpan<byte> buffer, long offset) => 0;
    public virtual void Truncate(long size) => throw new NotSupportedException();

    // File operations hooks
    public virtual void Open(File file) { }
    public virtual void Release(File file) { }

    public virtual void Sync(File file) { }

    // For directories, we need iteration. 
    public virtual List<DirectoryEntry> GetEntries() => new();
}

public struct DirectoryEntry
{
    public string Name;
    public ulong Ino;
    public InodeType Type;
}

public class Dentry
{
    private static long _nextId = 0;
    public long Id { get; } = System.Threading.Interlocked.Increment(ref _nextId);

    public string Name { get; set; }
    public Inode? Inode { get; set; }
    public Dentry? Parent { get; set; }
    public SuperBlock SuperBlock { get; set; }
    public Dictionary<string, Dentry> Children { get; } = new();

    // Mount point support
    public bool IsMounted { get; set; }
    public Dentry? MountRoot { get; set; }

    // Back pointer for mount traversal (points to the Dentry in parent FS where this root is mounted)
    public Dentry? MountedAt { get; set; }

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

    public void Instantiate(Inode inode)
    {
        if (Inode != null) throw new InvalidOperationException("Dentry already instantiated");
        Inode = inode;
        Inode.Dentries.Add(this);
        Inode.Get();
    }
}

public class File
{
    public Dentry Dentry { get; set; }
    public long Position { get; set; }
    public FileFlags Flags { get; set; }
    public object? PrivateData { get; set; }
    private bool _isClosed = false;

    public File(Dentry dentry, FileFlags flags)
    {
        Dentry = dentry;
        Flags = flags;
        dentry.Inode?.Get();  // Increase reference count
        dentry.Inode?.Open(this);
    }

    public virtual void Close()
    {
        if (_isClosed) return;
        _isClosed = true;

        Dentry.Inode?.Release(this);
        Dentry.Inode?.Put();  // Decrease reference count
    }

    public virtual int Read(Span<byte> buffer)
    {
        int n = Dentry.Inode!.Read(this, buffer, Position);
        if (n > 0) Position += n;
        return n;
    }

    public virtual int Write(ReadOnlySpan<byte> buffer)
    {
        int n = Dentry.Inode!.Write(this, buffer, Position);
        if (n > 0) Position += n;
        return n;
    }

    public virtual void Sync()
    {
        Dentry.Inode?.Sync(this);
    }
}

public struct DCacheKey : IEquatable<DCacheKey>
{
    public long ParentId;
    public string Name;

    public DCacheKey(long parentId, string name)
    {
        ParentId = parentId;
        Name = name;
    }

    public bool Equals(DCacheKey other) => ParentId == other.ParentId && Name == other.Name;
    public override bool Equals(object? obj) => obj is DCacheKey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(ParentId, Name);
}