using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bifrost.VFS;

public class Hostfs : FileSystem
{
    public Hostfs() { Name = "hostfs"; }

    public override SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data)
    {
        var sb = new HostSuperBlock(fsType, devName); // devName is the root path on host
        var rootDentry = sb.GetDentry(devName, "/", null);
        if (rootDentry == null) throw new FileNotFoundException("Root path not found", devName);
        
        sb.Root = rootDentry;
        sb.Root.Parent = sb.Root;
        return sb;
    }
}

public class HostSuperBlock : SuperBlock
{
    private ulong _nextIno = 1;
    public string HostRoot { get; }
    private Dictionary<string, Dentry> _dentryCache = new();

    public HostSuperBlock(FileSystemType type, string hostRoot)
    {
        Type = type;
        HostRoot = hostRoot;
    }

    public Dentry? GetDentry(string hostPath, string name, Dentry? parent)
    {
        if (_dentryCache.TryGetValue(hostPath, out var dentry)) return dentry;
        
        // Check existence
        bool isDir = Directory.Exists(hostPath);
        bool isFile = System.IO.File.Exists(hostPath);
        if (!isDir && !isFile) return null;
        
        var newInode = new HostInode(_nextIno++, this, hostPath, isDir);
        var newDentry = new Dentry(name, newInode, parent, this);
        _dentryCache[hostPath] = newDentry;
        return newDentry;
    }

    public override void WriteInode(Inode inode) { }

    public void MoveDentry(string oldPath, string newPath, Dentry dentry)
    {
        lock (Lock)
        {
            if (!string.IsNullOrEmpty(oldPath)) _dentryCache.Remove(oldPath);
            _dentryCache[newPath] = dentry;
        }
    }

    public void RemoveDentry(string hostPath)
    {
        lock (Lock)
        {
            _dentryCache.Remove(hostPath);
        }
    }

    public void InstantiateDentry(Dentry dentry, string hostPath, bool isDir)
    {
        var inode = new HostInode(_nextIno++, this, hostPath, isDir);
        dentry.Instantiate(inode);
        lock (Lock)
        {
            _dentryCache[hostPath] = dentry;
        }
        if (dentry.Parent != null)
        {
            dentry.Parent.Children[dentry.Name] = dentry;
        }
    }
}

public class HostInode : Inode
{
    public string HostPath { get; set; }
    
    public HostInode(ulong ino, SuperBlock sb, string hostPath, bool isDir)
    {
        Ino = ino;
        SuperBlock = sb;
        HostPath = hostPath;
        Type = isDir ? InodeType.Directory : InodeType.File;
        Mode = isDir ? 0x1FF : 0x1B6; // 777 or 666
        
        var info = isDir ? (FileSystemInfo)new DirectoryInfo(hostPath) : new FileInfo(hostPath);
        if (info.Exists)
        {
            // Fill stat
            if (!isDir) Size = (ulong)((FileInfo)info).Length;
            MTime = info.LastWriteTime;
            ATime = info.LastAccessTime;
            CTime = info.CreationTime;
        }
    }

    public override Dentry? Lookup(string name)
    {
        if (Type != InodeType.Directory) return null;
        string subPath = Path.Combine(HostPath, name);
        if (Dentries.Count == 0) return null;
        return ((HostSuperBlock)SuperBlock).GetDentry(subPath, name, Dentries[0]);
    }

    public override Dentry Create(Dentry dentry, int mode, int uid, int gid)
    {
        if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");
        string subPath = Path.Combine(HostPath, dentry.Name);
        if (System.IO.File.Exists(subPath) || Directory.Exists(subPath)) throw new InvalidOperationException("Exists");
        
        using (System.IO.File.Create(subPath)) { } // Create empty file
        
        var sb = (HostSuperBlock)SuperBlock;
        sb.InstantiateDentry(dentry, subPath, false);
        return dentry;
    }

    public override Dentry Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");
        string subPath = Path.Combine(HostPath, dentry.Name);
        if (System.IO.File.Exists(subPath) || Directory.Exists(subPath)) throw new InvalidOperationException("Exists");
        
        Directory.CreateDirectory(subPath);
        
        var sb = (HostSuperBlock)SuperBlock;
        sb.InstantiateDentry(dentry, subPath, true);
        return dentry;
    }

    public override void Unlink(string name)
    {
        string subPath = Path.Combine(HostPath, name);
        if (System.IO.File.Exists(subPath))
        {
             var sb = (HostSuperBlock)SuperBlock;
             var dentry = sb.GetDentry(subPath, name, null);
             System.IO.File.Delete(subPath);
             dentry?.Inode?.Put(); 
             sb.RemoveDentry(subPath);
        }
    }

    public override void Rmdir(string name)
    {
        string subPath = Path.Combine(HostPath, name);
        if (Directory.Exists(subPath))
        {
             var sb = (HostSuperBlock)SuperBlock;
             var dentry = sb.GetDentry(subPath, name, null);
             Directory.Delete(subPath, true);
             dentry?.Inode?.Put();
             sb.RemoveDentry(subPath);
        }
    }

    public override void Rename(string oldName, Inode newParent, string newName)
    {
        if (Type != InodeType.Directory || newParent.Type != InodeType.Directory)
            throw new InvalidOperationException("Not a directory");

        var targetParent = (HostInode)newParent;
        string oldFullPath = Path.Combine(HostPath, oldName);
        string newFullPath = Path.Combine(targetParent.HostPath, newName);

        var sb = (HostSuperBlock)SuperBlock;
        var dentry = sb.GetDentry(oldFullPath, oldName, null);
        if (dentry == null) throw new FileNotFoundException("Source not found", oldName);

        // Handle overwrite
        if (System.IO.File.Exists(newFullPath) || Directory.Exists(newFullPath))
        {
            var targetDentry = sb.GetDentry(newFullPath, newName, null);
            if (Directory.Exists(newFullPath)) Directory.Delete(newFullPath, true);
            else System.IO.File.Delete(newFullPath);
            targetDentry?.Inode?.Put();
            sb.RemoveDentry(newFullPath);
        }

        if (dentry.Inode!.Type == InodeType.Directory)
        {
             Directory.Move(oldFullPath, newFullPath);
        }
        else
        {
             System.IO.File.Move(oldFullPath, newFullPath);
        }

        // Update cache and internal path
        sb.MoveDentry(oldFullPath, newFullPath, dentry);
        ((HostInode)dentry.Inode).HostPath = newFullPath;
        dentry.Name = newName;
        if (targetParent.Dentries.Count > 0) dentry.Parent = targetParent.Dentries[0];
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);

    public override Dentry Link(Dentry dentry, Inode oldInode)
    {
        if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");
        if (!(oldInode is HostInode hi)) throw new InvalidOperationException("Not a host inode");
        
        string newPath = Path.Combine(HostPath, dentry.Name);
        if (link(hi.HostPath, newPath) != 0)
        {
            throw new IOException($"link failed with error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        }
        
        var sb = (HostSuperBlock)SuperBlock;
        dentry.Instantiate(oldInode);
        lock (sb.Lock)
        {
            sb.MoveDentry("", newPath, dentry); // hack: we don't have an old host path for this new link yet in the cache
        }
        return dentry;
    }

    public override Dentry Symlink(Dentry dentry, string target, int uid, int gid)
    {
        if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");
        string newPath = Path.Combine(HostPath, dentry.Name);
        
        global::System.IO.File.CreateSymbolicLink(newPath, target);
        var sb = (HostSuperBlock)SuperBlock;
        sb.InstantiateDentry(dentry, newPath, false);
        return dentry;
    }

    public override string Readlink()
    {
        var info = new global::System.IO.FileInfo(HostPath);
        return info.LinkTarget ?? throw new IOException("Not a link or target missing");
    }

    public override void Open(File file)
    {
        if (Type == InodeType.File)
        {
            FileMode mode = FileMode.Open;
            FileAccess access = FileAccess.Read;
            FileShare share = FileShare.ReadWrite;
            
            if ((file.Flags & FileFlags.O_WRONLY) != 0) access = FileAccess.Write;
            else if ((file.Flags & FileFlags.O_RDWR) != 0) access = FileAccess.ReadWrite;
            
            if ((file.Flags & FileFlags.O_CREAT) != 0) mode = FileMode.OpenOrCreate;
            if ((file.Flags & FileFlags.O_TRUNC) != 0) mode = FileMode.Truncate;
            if ((file.Flags & FileFlags.O_APPEND) != 0) mode = FileMode.Append;
            
            file.PrivateData = new FileStream(HostPath, mode, access, share);
        }
    }

    public override void Release(File file)
    {
        if (file.PrivateData is FileStream fs)
        {
            fs.Dispose();
            file.PrivateData = null;
        }
    }

    public override int Read(File file, Span<byte> buffer, long offset)
    {
        if (Type == InodeType.Directory) return 0;
        
        if (file?.PrivateData is FileStream fs)
        {
            if (fs.Position != offset) fs.Seek(offset, SeekOrigin.Begin);
            return fs.Read(buffer);
        }
        
        using var tempFs = new FileStream(HostPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        tempFs.Seek(offset, SeekOrigin.Begin);
        return tempFs.Read(buffer);
    }

    public override int Write(File file, ReadOnlySpan<byte> buffer, long offset)
    {
        if (Type == InodeType.Directory) return 0;

        if (file?.PrivateData is FileStream fs)
        {
            if (fs.Position != offset) fs.Seek(offset, SeekOrigin.Begin);
            fs.Write(buffer);
            Size = (ulong)fs.Length;
            return buffer.Length;
        }
        
        using var tempFs = new FileStream(HostPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        tempFs.Seek(offset, SeekOrigin.Begin);
        tempFs.Write(buffer);
        Size = (ulong)tempFs.Length;
        return buffer.Length;
    }

    public override void Truncate(long size)
    {
         using var fs = new FileStream(HostPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
         fs.SetLength(size);
         Size = (ulong)size;
    }

    public override List<DirectoryEntry> GetEntries()
    {
        var list = new List<DirectoryEntry>();
        if (Type != InodeType.Directory) return list;

        list.Add(new DirectoryEntry { Name = ".", Ino = Ino, Type = InodeType.Directory });
        list.Add(new DirectoryEntry { Name = "..", Ino = Ino, Type = InodeType.Directory });

        var entries = Directory.GetFileSystemEntries(HostPath);
        var sb = (HostSuperBlock)SuperBlock;
        foreach (var entryPath in entries)
        {
            string name = Path.GetFileName(entryPath);
            var dentry = sb.GetDentry(entryPath, name, Dentries.Count > 0 ? Dentries[0] : null);
            if (dentry != null)
            {
                list.Add(new DirectoryEntry { Name = name, Ino = dentry.Inode!.Ino, Type = dentry.Inode.Type });
            }
        }
        return list;
    }
}
