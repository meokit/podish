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
        var rootInode = sb.GetInode(devName);
        if (rootInode == null) throw new FileNotFoundException("Root path not found", devName);
        
        sb.Root = new Dentry("/", rootInode, null, sb);
        sb.Root.Parent = sb.Root;
        return sb;
    }
}

public class HostSuperBlock : SuperBlock
{
    private ulong _nextIno = 1;
    public string HostRoot { get; }
    private Dictionary<string, HostInode> _inodeCache = new();

    public HostSuperBlock(FileSystemType type, string hostRoot)
    {
        Type = type;
        HostRoot = hostRoot;
    }

    public override Inode AllocInode()
    {
        throw new NotImplementedException();
    }
    
    public HostInode? GetInode(string hostPath)
    {
        if (_inodeCache.TryGetValue(hostPath, out var inode)) return inode;
        
        // Check existence
        bool isDir = Directory.Exists(hostPath);
        bool isFile = System.IO.File.Exists(hostPath);
        if (!isDir && !isFile) return null;
        
        var newInode = new HostInode(_nextIno++, this, hostPath, isDir);
        _inodeCache[hostPath] = newInode;
        return newInode;
    }

    public override void WriteInode(Inode inode) { }
}

public class HostInode : Inode
{
    public string HostPath { get; }
    
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

    public override Inode? Lookup(string name)
    {
        if (Type != InodeType.Directory) return null;
        string subPath = Path.Combine(HostPath, name);
        return ((HostSuperBlock)SuperBlock).GetInode(subPath);
    }

    public override Inode Create(string name, int mode, int uid, int gid)
    {
        if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");
        string subPath = Path.Combine(HostPath, name);
        if (System.IO.File.Exists(subPath) || Directory.Exists(subPath)) throw new InvalidOperationException("Exists");
        
        using (System.IO.File.Create(subPath)) { } // Create empty file
        
        // Note: hostfs doesn't set uid/gid - relies on host filesystem
        return ((HostSuperBlock)SuperBlock).GetInode(subPath) ?? throw new IOException("Failed to create host file");
    }

    public override Inode Mkdir(string name, int mode, int uid, int gid)
    {
        if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");
        string subPath = Path.Combine(HostPath, name);
        Directory.CreateDirectory(subPath);
        // Note: hostfs doesn't set uid/gid - relies on host filesystem
        return ((HostSuperBlock)SuperBlock).GetInode(subPath) ?? throw new IOException("Failed to create host directory");
    }

    public override void Unlink(string name)
    {
        string subPath = Path.Combine(HostPath, name);
        if (System.IO.File.Exists(subPath)) System.IO.File.Delete(subPath);
    }

    public override void Rmdir(string name)
    {
        string subPath = Path.Combine(HostPath, name);
        if (Directory.Exists(subPath)) Directory.Delete(subPath);
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
        foreach (var entryPath in entries)
        {
            string name = Path.GetFileName(entryPath);
            var inode = ((HostSuperBlock)SuperBlock).GetInode(entryPath);
            if (inode != null)
            {
                list.Add(new DirectoryEntry { Name = name, Ino = inode.Ino, Type = inode.Type });
            }
        }
        return list;
    }
}
