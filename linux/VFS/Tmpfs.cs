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
        lock (Lock)
        {
            var inode = new TmpfsInode(_nextIno++, this);
            Inodes.Add(inode);
            AllInodes.Add(inode);
            return inode;
        }
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

    public TmpfsInode(ulong ino, SuperBlock sb)
    {
        Ino = ino;
        SuperBlock = sb;
        MTime = ATime = CTime = DateTime.Now;
    }

    public override Dentry? Lookup(string name)
    {
        lock (Lock)
        {
            if (Dentries.Count == 0) return null;
            var primaryDentry = Dentries[0];

            var sb = (TmpfsSuperBlock)SuperBlock;
            var key = new DCacheKey(primaryDentry.Id, name);
            if (sb.Dentries.TryGetValue(key, out var dentry))
            {
                if (primaryDentry.Children != null) primaryDentry.Children[name] = dentry;
                return dentry;
            }
            return null;
        }
    }

    public override Dentry Create(Dentry dentry, int mode, int uid, int gid)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");

            var sb = (TmpfsSuperBlock)SuperBlock;
            var key = new DCacheKey(dentry.Parent!.Id, dentry.Name);
            if (sb.Dentries.ContainsKey(key)) throw new InvalidOperationException("Exists");

            var inode = (TmpfsInode)sb.AllocInode();
            inode.Type = InodeType.File;
            inode.Mode = mode;
            inode.Uid = uid;
            inode.Gid = gid;

            dentry.Instantiate(inode);

            lock (sb.Lock)
            {
                sb.Dentries[key] = dentry;
            }
            dentry.Parent.Children[dentry.Name] = dentry;
            _childNames.Add(dentry.Name);

            return dentry;
        }
    }

    public override Dentry Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");

            var sb = (TmpfsSuperBlock)SuperBlock;
            var key = new DCacheKey(dentry.Parent!.Id, dentry.Name);
            if (sb.Dentries.ContainsKey(key)) throw new InvalidOperationException("Exists");

            var inode = (TmpfsInode)sb.AllocInode();
            inode.Type = InodeType.Directory;
            inode.Mode = mode;
            inode.Uid = uid;
            inode.Gid = gid;

            dentry.Instantiate(inode);

            lock (sb.Lock)
            {
                sb.Dentries[key] = dentry;
            }
            dentry.Parent.Children[dentry.Name] = dentry;
            _childNames.Add(dentry.Name);

            return dentry;
        }
    }
    public override void Unlink(string name)
    {
        lock (Lock)
        {
            if (Dentries.Count == 0) return;
            var primaryDentry = Dentries[0];
            var sb = (TmpfsSuperBlock)SuperBlock;
            var key = new DCacheKey(primaryDentry.Id, name);
            if (sb.Dentries.TryGetValue(key, out var dentry))
            {
                lock (sb.Lock)
                {
                    sb.Dentries.Remove(key);
                }
                primaryDentry.Children.Remove(name);
                var unlinkedInode = dentry.Inode;
                unlinkedInode?.Dentries.Remove(dentry);
                _childNames.Remove(name);

                // Crucial: Decrement refcount of the unlinked inode
                unlinkedInode?.Put();
            }
        }
    }

    public override void Rmdir(string name)
    {
        Unlink(name);
    }

    public override void Rename(string oldName, Inode newParent, string newName)
    {
        var targetParent = (TmpfsInode)newParent;
        // 1. Determine lock order to prevent deadlocks
        Inode first = this;
        Inode second = targetParent;
        if (first.Ino > second.Ino) { first = targetParent; second = this; }

        lock (first.Lock)
        {
            if (first != second)
            {
                lock (second.Lock)
                {
                    DoRename(oldName, targetParent, newName);
                }
            }
            else
            {
                DoRename(oldName, targetParent, newName);
            }
        }
    }

    private void DoRename(string oldName, TmpfsInode targetParent, string newName)
    {
        var sb = (TmpfsSuperBlock)SuperBlock;

        if (Dentries.Count == 0) throw new InvalidOperationException("Source parent detached");
        var oldPrimary = Dentries[0];

        if (targetParent.Dentries.Count == 0) throw new InvalidOperationException("Target parent detached");
        var newPrimary = targetParent.Dentries[0];

        var oldKey = new DCacheKey(oldPrimary.Id, oldName);
        var newKey = new DCacheKey(newPrimary.Id, newName);

        lock (sb.Lock)
        {
            if (!sb.Dentries.TryGetValue(oldKey, out var dentry))
                throw new InvalidOperationException("Source does not exist");

            // 2. Cycle Check: Ensure we aren't moving a directory into its own subdirectory
            if (dentry.Inode!.Type == InodeType.Directory)
            {
                var curr = newPrimary;
                while (curr != null)
                {
                    if (curr == dentry) throw new InvalidOperationException("Cannot move directory into its own subdirectory");
                    if (curr == curr.Parent) break;
                    curr = curr.Parent;
                }
            }

            // 3. Atomic Replacement: If destination exists, unlink it
            if (sb.Dentries.TryGetValue(newKey, out var existingDentry))
            {
                if (existingDentry.Inode!.Type == InodeType.Directory)
                {
                    // Check if empty (only . and ..)
                    if (existingDentry.Children.Count > 0)
                        throw new InvalidOperationException("Directory not empty");
                    targetParent.Rmdir(newName);
                }
                else
                {
                    targetParent.Unlink(newName);
                }
            }

            // 4. Move dentry in superblock cache
            sb.Dentries.Remove(oldKey);
            sb.Dentries[newKey] = dentry;

            // 5. Move dentry in directory structures
            oldPrimary.Children.Remove(oldName);
            _childNames.Remove(oldName);

            dentry.Parent = newPrimary;
            dentry.Name = newName;

            newPrimary.Children[newName] = dentry;
            targetParent._childNames.Add(newName);
        }
    }

    public override Dentry Link(Dentry dentry, Inode oldInode)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");

            var sb = (TmpfsSuperBlock)SuperBlock;
            var key = new DCacheKey(dentry.Parent!.Id, dentry.Name);
            if (sb.Dentries.ContainsKey(key)) throw new InvalidOperationException("Exists");

            dentry.Instantiate(oldInode);

            lock (sb.Lock)
            {
                sb.Dentries[key] = dentry;
            }
            dentry.Parent.Children[dentry.Name] = dentry;
            _childNames.Add(dentry.Name);
            return dentry;
        }
    }

    public override Dentry Symlink(Dentry dentry, string target, int uid, int gid)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");

            var sb = (TmpfsSuperBlock)SuperBlock;
            var key = new DCacheKey(dentry.Parent!.Id, dentry.Name);
            if (sb.Dentries.ContainsKey(key)) throw new InvalidOperationException("Exists");

            var inode = (TmpfsInode)sb.AllocInode();
            inode.Type = InodeType.Symlink;
            inode.Mode = 0x1FF; // 777
            inode.Uid = uid;
            inode.Gid = gid;
            inode._data = System.Text.Encoding.UTF8.GetBytes(target);
            inode.Size = (ulong)inode._data.Length;

            dentry.Instantiate(inode);

            lock (sb.Lock)
            {
                sb.Dentries[key] = dentry;
            }
            dentry.Parent.Children[dentry.Name] = dentry;
            _childNames.Add(dentry.Name);

            return dentry;
        }
    }

    public override string Readlink()
    {
        lock (Lock)
        {
            if (Type != InodeType.Symlink || _data == null) throw new InvalidOperationException("Not a symlink");
            return System.Text.Encoding.UTF8.GetString(_data);
        }
    }

    public override int Read(File file, Span<byte> buffer, long offset)
    {
        lock (Lock)
        {
            if (Type == InodeType.Directory) return 0;
            if (_data == null || offset >= _data.Length) return 0;

            int count = Math.Min(buffer.Length, _data.Length - (int)offset);
            _data.AsSpan((int)offset, count).CopyTo(buffer);
            return count;
        }
    }

    public override int Write(File file, ReadOnlySpan<byte> buffer, long offset)
    {
        lock (Lock)
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
        if (Dentries.Count == 0) return list;
        var primaryDentry = Dentries[0];

        list.Add(new DirectoryEntry { Name = ".", Ino = Ino, Type = InodeType.Directory });
        list.Add(new DirectoryEntry { Name = "..", Ino = Ino, Type = InodeType.Directory });

        var sb = (TmpfsSuperBlock)SuperBlock;
        foreach (var name in _childNames)
        {
            if (sb.Dentries.TryGetValue(new DCacheKey(primaryDentry.Id, name), out var dentry))
            {
                list.Add(new DirectoryEntry { Name = name, Ino = dentry.Inode!.Ino, Type = dentry.Inode.Type });
            }
        }
        return list;
    }

    protected override void Release()
    {
        // Clean up tmpfs inode resources
        _data = null;
        _childNames.Clear();
    }
}
