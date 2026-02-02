using System;
using System.Collections.Generic;

namespace Bifrost.VFS;

public enum InodeType
{
    Unknown = 0,
    Fifo = 1,
    CharDev = 2,
    Directory = 4,
    BlockDev = 6,
    File = 8,
    Symlink = 10,
    Socket = 12
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
    
    public abstract Inode AllocInode();
    public abstract void WriteInode(Inode inode);
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
    
    // Operations
    public abstract Inode? Lookup(string name);
    public abstract Inode Create(string name, int mode, int uid, int gid);
    public abstract Inode Mkdir(string name, int mode, int uid, int gid);
    public abstract void Unlink(string name);
    public abstract void Rmdir(string name);
    
    public abstract int Read(File file, Span<byte> buffer, long offset);
    public abstract int Write(File file, ReadOnlySpan<byte> buffer, long offset);
    public abstract void Truncate(long size);
    
    // File operations hooks
    public virtual void Open(File file) { }
    public virtual void Release(File file) { }
    
    // For directories, we need iteration. 
    public abstract List<DirectoryEntry> GetEntries();
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
    public Inode Inode { get; set; }
    public Dentry? Parent { get; set; }
    public SuperBlock SuperBlock { get; set; }
    public Dictionary<string, Dentry> Children { get; } = new();

    // Mount point support
    public bool IsMounted { get; set; }
    public Dentry? MountRoot { get; set; } 
    
    // Back pointer for mount traversal (points to the Dentry in parent FS where this root is mounted)
    public Dentry? MountedAt { get; set; }
    
    public Dentry(string name, Inode inode, Dentry? parent, SuperBlock sb)
    {
        Name = name;
        Inode = inode;
        Parent = parent;
        SuperBlock = sb;
    }
}

public class File
{
    public Dentry Dentry { get; set; }
    public long Position { get; set; }
    public FileFlags Flags { get; set; }
    public object? PrivateData { get; set; }
    
    public File(Dentry dentry, FileFlags flags)
    {
        Dentry = dentry;
        Flags = flags;
        dentry.Inode?.Open(this);
    }
    
    public virtual void Close()
    {
        Dentry.Inode?.Release(this);
    }
    
    public virtual int Read(Span<byte> buffer)
    {
        int n = Dentry.Inode.Read(this, buffer, Position);
        if (n > 0) Position += n;
        return n;
    }
    
    public virtual int Write(ReadOnlySpan<byte> buffer)
    {
        int n = Dentry.Inode.Write(this, buffer, Position);
        if (n > 0) Position += n;
        return n;
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