using System;
using System.Collections.Generic;
using System.Linq;

namespace Bifrost.VFS;

public class Tmpfs : FileSystem
{
    public Tmpfs() { Name = "tmpfs"; }

    public override SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data)
    {
        var sb = new TmpfsSuperBlock(fsType);
        var rootInode = sb.AllocInode();
        rootInode.Type = InodeType.Directory;
        rootInode.Mode = 0x1FF; // 777
        
        sb.Root = new Dentry("/", rootInode, null, sb);
        sb.Root.Parent = sb.Root; 
        
        if (rootInode is TmpfsInode tmpfsRoot)
        {
            tmpfsRoot.SetPrimaryDentry(sb.Root);
        }
        
        return sb;
    }
}

public class TmpfsSuperBlock : SuperBlock
{
    private ulong _nextIno = 1;
    
    // The "Hash Table" requested by user: (ParentDentryID, Name) -> ChildDentry
    public Dictionary<DCacheKey, Dentry> Dentries { get; } = new();

    public TmpfsSuperBlock(FileSystemType type)
    {
        Type = type;
    }

    public override Inode AllocInode()
    {
        var inode = new TmpfsInode(_nextIno++, this);
        Inodes.Add(inode);
        AllInodes.Add(inode);
        // Don't call Get() - inodes start with RefCount=0
        // Only File objects should increment RefCount
        return inode;
    }

    public override void WriteInode(Inode inode) { }
    
    protected override void Shutdown()
    {
        base.Shutdown();
        Dentries.Clear();
    }
}

public class TmpfsInode : Inode
{
    private byte[]? _data = Array.Empty<byte>();
    private HashSet<string> _childNames = new();
    private Dentry? _primaryDentry;

    public TmpfsInode(ulong ino, SuperBlock sb)
    {
        Ino = ino;
        SuperBlock = sb;
        MTime = ATime = CTime = DateTime.Now;
    }

    public override Inode? Lookup(string name)
    {
        if (_primaryDentry == null) return null;
        
        var sb = (TmpfsSuperBlock)SuperBlock;
        var key = new DCacheKey(_primaryDentry.Id, name);
        if (sb.Dentries.TryGetValue(key, out var dentry))
        {
            if (_primaryDentry.Children != null) _primaryDentry.Children[name] = dentry;
            return dentry.Inode;
        }
        return null;
    }

    public override Inode Create(string name, int mode, int uid, int gid)
    {
        if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");
        if (_primaryDentry == null) throw new InvalidOperationException("Dentry detached");
        
        var sb = (TmpfsSuperBlock)SuperBlock;
        if (sb.Dentries.ContainsKey(new DCacheKey(_primaryDentry.Id, name))) throw new InvalidOperationException("Exists");

        var inode = (TmpfsInode)sb.AllocInode();
        inode.Type = InodeType.File;
        inode.Mode = mode;
        inode.Uid = uid;
        inode.Gid = gid;
        
        var dentry = new Dentry(name, inode, _primaryDentry, sb);
        inode._primaryDentry = dentry; 
        
        sb.Dentries[new DCacheKey(_primaryDentry.Id, name)] = dentry;
        _primaryDentry.Children[name] = dentry;
        _childNames.Add(name);
        
        return inode;
    }

    public override Inode Mkdir(string name, int mode, int uid, int gid)
    {
        if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");
        if (_primaryDentry == null) throw new InvalidOperationException("Dentry detached");

        var sb = (TmpfsSuperBlock)SuperBlock;
        if (sb.Dentries.ContainsKey(new DCacheKey(_primaryDentry.Id, name))) throw new InvalidOperationException("Exists");

        var inode = (TmpfsInode)sb.AllocInode();
        inode.Type = InodeType.Directory;
        inode.Mode = mode;
        inode.Uid = uid;
        inode.Gid = gid;
        
        var dentry = new Dentry(name, inode, _primaryDentry, sb);
        inode._primaryDentry = dentry;
        
        sb.Dentries[new DCacheKey(_primaryDentry.Id, name)] = dentry;
        _primaryDentry.Children[name] = dentry;
        _childNames.Add(name);
        
        return inode;
    }

    public override void Unlink(string name)
    {
        if (_primaryDentry == null) return;
        var sb = (TmpfsSuperBlock)SuperBlock;
        var key = new DCacheKey(_primaryDentry.Id, name);
        if (sb.Dentries.Remove(key))
        {
            _childNames.Remove(name);
        }
    }

    public override void Rmdir(string name)
    {
        Unlink(name);
    }

    public override int Read(File file, Span<byte> buffer, long offset)
    {
        if (Type == InodeType.Directory) return 0;
        if (_data == null || offset >= _data.Length) return 0;
        
        int count = Math.Min(buffer.Length, _data.Length - (int)offset);
        _data.AsSpan((int)offset, count).CopyTo(buffer);
        return count;
    }

    public override int Write(File file, ReadOnlySpan<byte> buffer, long offset)
    {
        if (Type == InodeType.Directory) return 0;
        
        long end = offset + buffer.Length;
        if (_data == null || end > _data.Length)
        {
            Array.Resize(ref _data, (int)end);
        }
        
        buffer.CopyTo(_data.AsSpan((int)offset));
        Size = (ulong)_data.Length;
        MTime = DateTime.Now;
        return buffer.Length;
    }

    public override void Truncate(long size)
    {
        if (Type == InodeType.Directory) return;
        Array.Resize(ref _data, (int)size);
        Size = (ulong)size;
        MTime = DateTime.Now;
    }

    public override List<DirectoryEntry> GetEntries()
    {
        var list = new List<DirectoryEntry>();
        if (Type != InodeType.Directory) return list;
        if (_primaryDentry == null) return list;

        list.Add(new DirectoryEntry { Name = ".", Ino = Ino, Type = InodeType.Directory });
        list.Add(new DirectoryEntry { Name = "..", Ino = Ino, Type = InodeType.Directory }); 

        var sb = (TmpfsSuperBlock)SuperBlock;
        foreach (var name in _childNames)
        {
            if (sb.Dentries.TryGetValue(new DCacheKey(_primaryDentry.Id, name), out var dentry))
            {
                 list.Add(new DirectoryEntry { Name = name, Ino = dentry.Inode.Ino, Type = dentry.Inode.Type });
            }
        }
        return list;
    }
    
    public void SetPrimaryDentry(Dentry d) { _primaryDentry = d; }
    
    protected override void Release()
    {
        // Clean up tmpfs inode resources
        _data = null;
        _childNames.Clear();
    }
}
