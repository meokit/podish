using System.Buffers.Binary;
using System.Text;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998 // Async method lacks await operators - syscall handlers require async signature
    private static async ValueTask<int> SysLink(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var oldPath = sm.ReadString(a1);
        var newPath = sm.ReadString(a2);

        var oldDentry = sm.PathWalk(oldPath);
        if (oldDentry == null || oldDentry.Inode == null) return -(int)Errno.ENOENT;
        if (oldDentry.Inode.Type == InodeType.Directory) return -(int)Errno.EPERM;

        var lastSlash = newPath.LastIndexOf('/');
        var dirPath = lastSlash == -1 ? "." : newPath[..lastSlash];
        if (dirPath == "") dirPath = "/";
        var name = lastSlash == -1 ? newPath : newPath[(lastSlash + 1)..];

        var dirDentry = sm.PathWalk(dirPath);
        if (dirDentry == null || dirDentry.Inode == null) return -(int)Errno.ENOENT;
        if (dirDentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

        try
        {
            var newDentry = new Dentry(name, null, dirDentry, dirDentry.SuperBlock);
            dirDentry.Inode.Link(newDentry, oldDentry.Inode);
            return 0;
        }
        catch (Exception)
        {
            return -(int)Errno.EIO;
        }
    }

    private static async ValueTask<int> SysChdir(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var path = sm.ReadString(a1);
        var dentry = sm.PathWalk(path);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;
        if (dentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

        var oldCwd = sm.CurrentWorkingDirectory;
        sm.CurrentWorkingDirectory = dentry;
        dentry.Inode!.Get();
        oldCwd?.Inode?.Put();
        return 0;
    }

    private static async ValueTask<int> SysFchdir(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;
        if (f.Dentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

        var oldCwd = sm.CurrentWorkingDirectory;
        sm.CurrentWorkingDirectory = f.Dentry;
        f.Dentry.Inode!.Get();
        oldCwd?.Inode?.Put();
        return 0;
    }

    private static async ValueTask<int> SysMkdir(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var path = sm.ReadString(a1);
        var mode = a2;

        var lastSlash = path.LastIndexOf('/');
        var parentPath = lastSlash == -1 ? "." : lastSlash == 0 ? "/" : path[..lastSlash];
        var name = lastSlash == -1 ? path : path[(lastSlash + 1)..];

        var parentDentry = sm.PathWalk(parentPath);
        if (parentDentry == null || parentDentry.Inode == null) return -(int)Errno.ENOENT;

        var t = sm.Engine.Owner as FiberTask;
        var uid = t?.Process.EUID ?? 0;
        var gid = t?.Process.EGID ?? 0;

        try
        {
            var dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
            parentDentry.Inode.Mkdir(dentry, (int)mode, uid, gid);
            return 0;
        }
        catch
        {
            return -(int)Errno.EACCES;
        }
    }

    private static async ValueTask<int> SysTruncate(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await DoTruncate(state, a1, a2);
    }

    private static async ValueTask<int> SysTruncate64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // 32-bit truncate64 has padding if we use 64-bit length. a2 and a3 are the split 64 bit integer
        var length = (long)(((ulong)a3 << 32) | a2);
        return await DoTruncate(state, a1, length);
    }

    private static ValueTask<int> DoTruncate(IntPtr state, uint pathPtr, long length)
    {
        var sm = Get(state);
        if (sm == null) return new ValueTask<int>(-(int)Errno.EPERM);
        var path = sm.ReadString(pathPtr);

        var dentry = sm.PathWalk(path);
        if (dentry == null || dentry.Inode == null) return new ValueTask<int>(-(int)Errno.ENOENT);
        if (dentry.Inode.Type == InodeType.Directory) return new ValueTask<int>(-(int)Errno.EISDIR);

        var t = sm.Engine.Owner as FiberTask;
        if (t != null && t.Process.EUID != 0 && t.Process.EUID != dentry.Inode.Uid)
        {
            // Usually need write permission to truncate, simplified check for now
        }

        return new ValueTask<int>(dentry.Inode.Truncate(length));
    }

    private static async ValueTask<int> SysFtruncate(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var length = (long)a2;

        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;
        if (f.Dentry.Inode.Type == InodeType.Directory) return -(int)Errno.EINVAL;

        return f.Dentry.Inode.Truncate(length);
    }

    private static async ValueTask<int> SysFtruncate64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var length = (long)(((ulong)a3 << 32) | a2);

        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;
        if (f.Dentry.Inode.Type == InodeType.Directory) return -(int)Errno.EINVAL;

        return f.Dentry.Inode.Truncate(length);
    }

    private static async ValueTask<int> SysRmdir(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var path = sm.ReadString(a1);

        var lastSlash = path.LastIndexOf('/');
        var parentPath = lastSlash == -1 ? "." : lastSlash == 0 ? "/" : path[..lastSlash];
        var name = lastSlash == -1 ? path : path[(lastSlash + 1)..];

        var parentDentry = sm.PathWalk(parentPath);
        if (parentDentry == null || parentDentry.Inode == null) return -(int)Errno.ENOENT;

        // Check if directory exists and is empty
        var targetDentry = sm.PathWalk(path);
        if (targetDentry == null || targetDentry.Inode == null) return -(int)Errno.ENOENT;
        if (targetDentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

        // Check if empty (only . and .. entries)
        var entries = targetDentry.Inode.GetEntries();
        if (entries.Count > 2) return -(int)Errno.ENOTEMPTY; // Has more than . and ..

        try
        {
            parentDentry.Inode!.Rmdir(name);
            parentDentry.Children.Remove(name);
            return 0;
        }
        catch
        {
            return -(int)Errno.EACCES;
        }
    }

    private static async ValueTask<int> SysMkdirAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var dirfd = (int)a1;
        var path = sm.ReadString(a2);
        var mode = a3;

        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        var lastSlash = path.LastIndexOf('/');
        var parentPath = lastSlash == -1 ? "" : path[..lastSlash];
        var name = lastSlash == -1 ? path : path[(lastSlash + 1)..];

        var parentDentry = sm.PathWalk(parentPath == "" ? "." : parentPath, startAt);
        if (parentDentry == null || parentDentry.Inode == null) return -(int)Errno.ENOENT;

        var t = sm.Engine.Owner as FiberTask;
        var uid = t?.Process.EUID ?? 0;
        var gid = t?.Process.EGID ?? 0;

        try
        {
            var dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
            parentDentry.Inode.Mkdir(dentry, (int)mode, uid, gid);
            return 0;
        }
        catch
        {
            return -(int)Errno.EACCES;
        }
    }

    private static async ValueTask<int> SysUnlinkAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var dirfd = (int)a1;
        var path = sm.ReadString(a2);
        var flags = a3;

        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        var lastSlash = path.LastIndexOf('/');
        var parentPath = lastSlash == -1 ? "" : path[..lastSlash];
        var name = lastSlash == -1 ? path : path[(lastSlash + 1)..];

        var parentDentry = sm.PathWalk(parentPath == "" ? "." : parentPath, startAt);
        if (parentDentry == null || parentDentry.Inode == null) return -(int)Errno.ENOENT;

        if ((flags & 0x200) != 0) // AT_REMOVEDIR
            try
            {
                parentDentry.Inode.Rmdir(name);
                parentDentry.Children.Remove(name);
                return 0;
            }
            catch
            {
                return -(int)Errno.EACCES;
            }

        try
        {
            parentDentry.Inode.Unlink(name);
            parentDentry.Children.Remove(name);
            return 0;
        }
        catch
        {
            return -(int)Errno.ENOENT;
        }
    }

    private static async ValueTask<int> SysGetdents(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var bufAddr = a2;
        var count = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;
        if (f.Dentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

        try
        {
            var entries = f.Dentry.Inode.GetEntries();
            var startIdx = (int)f.Position;
            if (startIdx >= entries.Count) return 0;

            var writeOffset = 0;
            for (var i = startIdx; i < entries.Count; i++)
            {
                var entry = entries[i];
                var nameBytes = Encoding.UTF8.GetBytes(entry.Name);
                var nameLen = nameBytes.Length + 1;
                // reclen: ino(4) + off(4) + reclen(2) + name + pad + type(1)
                // We align to 4 bytes for 32-bit
                var reclen = (4 + 4 + 2 + nameLen + 1 + 3) & ~3;

                if (writeOffset + reclen > count) break;

                var baseAddr = bufAddr + (uint)writeOffset;
                var buf = new byte[reclen];

                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), (uint)entry.Ino);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)(i + 1)); // d_off
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), (ushort)reclen);

                Array.Copy(nameBytes, 0, buf, 10, nameBytes.Length);
                buf[10 + nameBytes.Length] = 0; // null terminator

                var dType = entry.Type switch
                {
                    InodeType.Directory => (byte)4,
                    InodeType.File => (byte)8,
                    InodeType.Symlink => (byte)10,
                    InodeType.CharDev => (byte)2,
                    InodeType.BlockDev => (byte)6,
                    InodeType.Fifo => (byte)1,
                    InodeType.Socket => (byte)12,
                    _ => (byte)0
                };
                buf[reclen - 1] = dType;

                if (!sm.Engine.CopyToUser(baseAddr, buf))
                    return -(int)Errno.EFAULT;
                writeOffset += reclen;
                f.Position = i + 1;
            }

            return writeOffset;
        }
        catch
        {
            return -(int)Errno.EIO;
        }
    }

    private static async ValueTask<int> SysNewFstatAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var dirfd = (int)a1;
        var path = sm.ReadString(a2);
        var statAddr = a3;
        var flags = a4;

        if (path == "" && (flags & 0x1000) != 0) // AT_EMPTY_PATH
            return await SysFstat64(state, a1, a3, 0, 0, 0, 0);

        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        var dentry = sm.PathWalk(path, startAt);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;

        WriteStat64(sm, statAddr, dentry.Inode);
        return 0;
    }

    private static async ValueTask<int> SysUtimensAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var path = sm.ReadString(a2);
        var timesAddr = a3;
        var dentry = sm.PathWalk(path);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;

        if (timesAddr == 0)
            dentry.Inode.ATime = dentry.Inode.MTime = DateTime.Now;
        else
            // TODO: Read timespec from memory
            dentry.Inode.ATime = dentry.Inode.MTime = DateTime.Now;
        return 0;
    }

    private static async ValueTask<int> SysFchownAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var dirfd = (int)a1;
        var path = sm.ReadString(a2);
        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        var dentry = sm.PathWalk(path, startAt);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;

        var uid = (int)a3;
        var gid = (int)a4;
        
        // Simplified: permissions check would go here
        dentry.Inode.Uid = uid;
        dentry.Inode.Gid = gid;
        dentry.Inode.CTime = DateTime.Now;
        return 0;
    }

    private static async ValueTask<int> SysFchmodAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var dirfd = (int)a1;
        var path = sm.ReadString(a2);
        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        var dentry = sm.PathWalk(path, startAt);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;

        var mode = a3;
        // Simplified: permissions check would go here
        dentry.Inode.Mode = (int)(mode & 0xFFF);
        dentry.Inode.CTime = DateTime.Now;
        return 0;
    }

    private static async ValueTask<int> SysFaccessAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var dirfd = (int)a1;
        var path = sm.ReadString(a2);
        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        var dentry = sm.PathWalk(path, startAt);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;

        // Simplified: should check mode (a3) but for now existence is enough
        return 0;
    }

    private static async ValueTask<int> SysRename(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var oldPath = sm.ReadString(a1);
        var newPath = sm.ReadString(a2);

        return ImplRename(sm, -100, oldPath, -100, newPath, 0);
    }

    private static async ValueTask<int> SysRenameAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        return ImplRename(sm, (int)a1, sm.ReadString(a2), (int)a3, sm.ReadString(a4), 0);
    }

    private static async ValueTask<int> SysRenameAt2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        return ImplRename(sm, (int)a1, sm.ReadString(a2), (int)a3, sm.ReadString(a4), a5);
    }

    private static int ImplRename(SyscallManager sm, int oldDirFd, string oldPath, int newDirFd, string newPath,
        uint flags)
    {
        Dentry? oldStart = null;
        if (oldDirFd != -100 && !oldPath.StartsWith("/"))
        {
            var f = sm.GetFD(oldDirFd);
            if (f == null) return -(int)Errno.EBADF;
            oldStart = f.Dentry;
        }

        Dentry? newStart = null;
        if (newDirFd != -100 && !newPath.StartsWith("/"))
        {
            var f = sm.GetFD(newDirFd);
            if (f == null) return -(int)Errno.EBADF;
            newStart = f.Dentry;
        }

        var lastSlashOld = oldPath.LastIndexOf('/');
        var oldParentPath = lastSlashOld == -1 ? "" : oldPath[..lastSlashOld];
        var oldName = lastSlashOld == -1 ? oldPath : oldPath[(lastSlashOld + 1)..];

        var lastSlashNew = newPath.LastIndexOf('/');
        var newParentPath = lastSlashNew == -1 ? "" : newPath[..lastSlashNew];
        var newName = lastSlashNew == -1 ? newPath : newPath[(lastSlashNew + 1)..];

        var oldParentDentry = sm.PathWalk(oldParentPath == "" ? "." : oldParentPath, oldStart);
        var newParentDentry = sm.PathWalk(newParentPath == "" ? "." : newParentPath, newStart);

        if (oldParentDentry == null || newParentDentry == null) return -(int)Errno.ENOENT;

        try
        {
            oldParentDentry.Inode!.Rename(oldName, newParentDentry.Inode!, newName);
            return 0;
        }
        catch
        {
            return -(int)Errno.EACCES;
        }
    }

    private static async ValueTask<int> SysStat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var path = sm.ReadString(a1);
        var dentry = sm.PathWalk(path);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;

        WriteStat(sm, a2, dentry.Inode);
        return 0;
    }

    private static async ValueTask<int> SysLstat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var path = sm.ReadString(a1);
        var dentry = sm.PathWalk(path, followLink: false);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;

        WriteStat(sm, a2, dentry.Inode);
        return 0;
    }

    private static async ValueTask<int> SysFstat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;

        WriteStat(sm, a2, f.Dentry.Inode);
        return 0;
    }

    private static async ValueTask<int> SysStatx(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var dirfd = (int)a1;
        var path = sm.ReadString(a2);
        var flags = a3;
        var mask = a4;
        var statxAddr = a5;

        if (path == "" && (flags & LinuxConstants.AT_EMPTY_PATH) != 0)
        {
            var f = sm.GetFD(dirfd);
            if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;
            WriteStatx(sm, statxAddr, f.Dentry.Inode, mask);
            return 0;
        }

        Dentry? startAt = null;
        if (dirfd != unchecked((int)LinuxConstants.AT_FDCWD) && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        var followLink = (flags & LinuxConstants.AT_SYMLINK_NOFOLLOW) == 0;
        var dentry = sm.PathWalk(path, startAt, followLink);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;

        WriteStatx(sm, statxAddr, dentry.Inode, mask);
        return 0;
    }

    private static async ValueTask<int> SysChmod(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var path = sm.ReadString(a1);
        var mode = a2;

        var dentry = sm.PathWalk(path);
        if (dentry == null || dentry.Inode == null) return -2; // ENOENT

        // Permission check: only owner or root can chmod
        var t = sm.Engine.Owner as FiberTask;
        if (t != null && t.Process.EUID != 0 && t.Process.EUID != dentry.Inode.Uid)
            return -(int)Errno.EPERM;

        // Only modify permission bits (lower 12 bits: rwx for user/group/other + special bits)
        dentry.Inode.Mode = (int)(mode & 0xFFF);
        dentry.Inode.CTime = DateTime.Now;
        return 0;
    }

    private static async ValueTask<int> SysFchmod(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var mode = a2;

        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -9; // EBADF

        // Permission check
        var t = sm.Engine.Owner as FiberTask;
        if (t != null && t.Process.EUID != 0 && t.Process.EUID != f.Dentry.Inode.Uid)
            return -(int)Errno.EPERM;

        f.Dentry.Inode.Mode = (int)(mode & 0xFFF);
        f.Dentry.Inode.CTime = DateTime.Now;
        return 0;
    }

    private static async ValueTask<int> SysChown(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var path = sm.ReadString(a1);
        var uid = (int)a2;
        var gid = (int)a3;

        var dentry = sm.PathWalk(path);
        if (dentry == null || dentry.Inode == null) return -2; // ENOENT

        // Permission check: only root can chown
        var t = sm.Engine.Owner as FiberTask;
        if (t != null && t.Process.EUID != 0)
            return -1; // EPERM

        // -1 means "don't change"
        if (uid != -1) dentry.Inode.Uid = uid;
        if (gid != -1) dentry.Inode.Gid = gid;
        dentry.Inode.CTime = DateTime.Now;
        return 0;
    }

    private static async ValueTask<int> SysFchown(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var uid = (int)a2;
        var gid = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -9; // EBADF

        // Permission check: only root can chown
        var t = sm.Engine.Owner as FiberTask;
        if (t != null && t.Process.EUID != 0)
            return -1; // EPERM

        if (uid != -1) f.Dentry.Inode.Uid = uid;
        if (gid != -1) f.Dentry.Inode.Gid = gid;
        f.Dentry.Inode.CTime = DateTime.Now;
        return 0;
    }

    private static async ValueTask<int> SysLchown(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // Since we don't have symlinks yet, behave like chown
        return await SysChown(state, a1, a2, a3, a4, a5, a6);
    }

    private static async ValueTask<int> SysGetCwd(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var bufAddr = a1;
        var size = a2;

        var parts = new List<string>();
        var current = sm.CurrentWorkingDirectory;
        while (current != sm.ProcessRoot && current != sm.Root)
        {
            parts.Insert(0, current.Name);
            var next = current.Parent;
            if (current == current.SuperBlock.Root && current.MountedAt != null) next = current.MountedAt.Parent;
            if (next == null || next == current) break;
            current = next;
        }

        var cwd = "/" + string.Join("/", parts);
        if (cwd.Length + 1 > size) return -(int)Errno.ERANGE;

        if (!sm.Engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes(cwd + "\0"))) return -(int)Errno.EFAULT;
        return cwd.Length + 1;
    }

    private static async ValueTask<int> SysSync(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        foreach (var file in sm.FDs.Values) file?.Dentry.Inode?.Sync(file);
        return 0;
    }

    private static async ValueTask<int> SysFsync(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        var file = sm.GetFD((int)a1);
        if (file == null) return -(int)Errno.EBADF;
        file.Dentry.Inode!.Sync(file);
        return 0;
    }

    private static async ValueTask<int> SysFdatasync(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await SysFsync(state, a1, a2, a3, a4, a5, a6);
    }

    private static async ValueTask<int> SysUnlink(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var path = sm.ReadString(a1);

        var lastSlash = path.LastIndexOf('/');
        var parentPath = lastSlash == -1 ? "." : lastSlash == 0 ? "/" : path[..lastSlash];
        var name = lastSlash == -1 ? path : path[(lastSlash + 1)..];

        var parentDentry = sm.PathWalk(parentPath);
        if (parentDentry == null) return -(int)Errno.ENOENT;

        try
        {
            parentDentry.Inode!.Unlink(name);
            parentDentry.Children.Remove(name);
            return 0;
        }
        catch
        {
            return -(int)Errno.ENOENT;
        }
    }

    private static async ValueTask<int> SysAccess(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        var path = sm.ReadString(a1);
        var dentry = sm.PathWalk(path);
        if (dentry != null && dentry.Inode != null) return 0;
        return -(int)Errno.ENOENT;
    }

    private static async ValueTask<int> SysGetdents64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var bufAddr = a2;
        var count = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;

        try
        {
            var entries = f.Dentry.Inode.GetEntries();

            // Simplified logic: uses f.Position as index in entries list
            var startIdx = (int)f.Position;
            if (startIdx >= entries.Count) return 0;

            var writeOffset = 0;
            for (var i = startIdx; i < entries.Count; i++)
            {
                var entry = entries[i];
                var nameBytes = Encoding.UTF8.GetBytes(entry.Name);
                var nameLen = nameBytes.Length + 1;
                var recLen = (8 + 8 + 2 + 1 + nameLen + 7) & ~7;

                if (writeOffset + recLen > count) break;

                var baseAddr = bufAddr + (uint)writeOffset;

                var buf = new byte[recLen];
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0), entry.Ino);
                BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8), writeOffset + recLen);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(16), (ushort)recLen);

                byte dType = 8; // DT_REG
                if (entry.Type == InodeType.Directory) dType = 4;
                buf[18] = dType;

                Array.Copy(nameBytes, 0, buf, 19, nameBytes.Length);
                buf[19 + nameBytes.Length] = 0;

                if (!sm.Engine.CopyToUser(baseAddr, buf)) return -(int)Errno.EFAULT;
                writeOffset += recLen;
                f.Position = i + 1;
            }

            return writeOffset;
        }
        catch
        {
            return -(int)Errno.EPERM;
        }
    }

    private static void WriteStat64(SyscallManager sm, uint addr, Inode inode)
    {
        var buf = new byte[96];

        var mode = (uint)inode.Mode | (uint)inode.Type;
        var size = (long)inode.Size;
        var uid = (uint)inode.Uid;
        var gid = (uint)inode.Gid;

        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0), 0x800);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), (uint)inode.Ino); // __st_ino
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), mode);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), 1); // nlink

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24), uid);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28), gid);

        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(32), inode.Rdev); // st_rdev
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(44), (ulong)size);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(52), 4096);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(56), (ulong)((size + 511) / 512));

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(64),
            (uint)new DateTimeOffset(inode.ATime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(72),
            (uint)new DateTimeOffset(inode.MTime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(80),
            (uint)new DateTimeOffset(inode.CTime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(88), inode.Ino);

        if (!sm.Engine.CopyToUser(addr, buf)) return;
    }

    private static void WriteStat(SyscallManager sm, uint addr, Inode inode)
    {
        var buf = new byte[64];

        var mode = (uint)inode.Mode | (uint)inode.Type;
        var size = (long)inode.Size;
        var uid = (uint)inode.Uid;
        var gid = (uint)inode.Gid;

        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), 0x800); // st_dev
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)inode.Ino);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), (ushort)mode);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10), 1); // nlink
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12), (ushort)uid);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(14), (ushort)gid);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(16), (ushort)inode.Rdev); // st_rdev
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24), 4096); // blksize
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28), (uint)((size + 511) / 512));

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(32),
            (uint)new DateTimeOffset(inode.ATime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(40),
            (uint)new DateTimeOffset(inode.MTime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(48),
            (uint)new DateTimeOffset(inode.CTime).ToUnixTimeSeconds());

        if (!sm.Engine.CopyToUser(addr, buf)) return;
    }

    private static void WriteStatx(SyscallManager sm, uint addr, Inode inode, uint mask)
    {
        var buf = new byte[256];

        var actualMask = mask & LinuxConstants.STATX_BASIC_STATS;

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x00), actualMask);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x04), 4096); // blksize
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x08), 0); // attributes

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x10), (uint)Math.Max(1, inode.Dentries.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x14), (uint)inode.Uid);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x18), (uint)inode.Gid);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x1C), (ushort)((uint)inode.Mode | (uint)inode.Type));

        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x20), inode.Ino);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x28), inode.Size);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x30), (inode.Size + 511) / 512);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x38), 0); // attributes_mask

        void writeTime(int offset, DateTime dt)
        {
            var dto = new DateTimeOffset(dt);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(offset), dto.ToUnixTimeSeconds());
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 8), (uint)(dto.Millisecond * 1000000));
        }

        writeTime(0x40, inode.ATime);
        writeTime(0x50, DateTime.UnixEpoch); // btime (creation) - not supported yet
        writeTime(0x60, inode.CTime);
        writeTime(0x70, inode.MTime);

        // rdev is encoded as (major << 8) | minor, decode for statx
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x80), (inode.Rdev >> 8) & 0xFF); // rdev_major
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x84), inode.Rdev & 0xFF); // rdev_minor
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x88), 0x8); // dev_major (faked)
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x8C), 0x0); // dev_minor (faked)

        if (!sm.Engine.CopyToUser(addr, buf)) return;
    }

    private static int GetFsMagic(Dentry dentry)
    {
        var fsName = dentry.SuperBlock.Type.Name;
        return fsName switch
        {
            "tmpfs" => 0x01021994,
            "devtmpfs" => 0x01021994,
            "proc" => 0x00009FA0,
            "overlay" => unchecked(0x794C7630),
            "hostfs" => 0x0000EF53,
            _ => 0x0000EF53
        };
    }

    private static void WriteStatfs32(SyscallManager sm, uint addr, Dentry dentry)
    {
        const int blockSize = 4096;
        const int totalBlocks = 256 * 1024 * 1024 / blockSize; // 256 MiB synthetic capacity
        const int freeBlocks = totalBlocks / 2;
        const int totalFiles = 1_000_000;
        const int freeFiles = 900_000;
        const int fsid0 = 0x78656D75; // "xemu"
        const int fsid1 = 0x46535031; // "FSP1"

        var buf = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0), GetFsMagic(dentry));
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), blockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), totalBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), freeBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), freeBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), totalFiles);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24), freeFiles);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(28), fsid0);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(32), fsid1);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(36), 255); // f_namelen
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(40), blockSize); // f_frsize
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(44), 0); // f_flags
        // f_spare[4] at [48..63] left as zero

        sm.Engine.CopyToUser(addr, buf);
    }

    private static void WriteStatfs64(SyscallManager sm, uint addr, Dentry dentry)
    {
        const int blockSize = 4096;
        const ulong totalBlocks = 256UL * 1024UL * 1024UL / blockSize; // 256 MiB synthetic capacity
        const ulong freeBlocks = totalBlocks / 2;
        const ulong totalFiles = 1_000_000;
        const ulong freeFiles = 900_000;
        const int fsid0 = 0x78656D75; // "xemu"
        const int fsid1 = 0x46535031; // "FSP1"

        // i386 statfs64: sizeof(struct statfs64) = 84 bytes.
        var buf = new byte[84];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0), GetFsMagic(dentry));
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), blockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(8), totalBlocks);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(16), freeBlocks);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(24), freeBlocks);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(32), totalFiles);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(40), freeFiles);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(48), fsid0);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(52), fsid1);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(56), 255); // f_namelen
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(60), blockSize); // f_frsize
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(64), 0); // f_flags
        // f_spare[4] at [68..83] left as zero

        sm.Engine.CopyToUser(addr, buf);
    }

    private static async ValueTask<int> SysStatfs(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var path = sm.ReadString(a1);
        var dentry = sm.PathWalk(path);
        if (dentry?.Inode == null) return -(int)Errno.ENOENT;

        if (!sm.Engine.CopyToUser(a2, new byte[64])) return -(int)Errno.EFAULT;
        WriteStatfs32(sm, a2, dentry);
        return 0;
    }

    private static async ValueTask<int> SysFstatfs(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var file = sm.GetFD(fd);
        if (file?.Dentry.Inode == null) return -(int)Errno.EBADF;

        if (!sm.Engine.CopyToUser(a2, new byte[64])) return -(int)Errno.EFAULT;
        WriteStatfs32(sm, a2, file.Dentry);
        return 0;
    }

    private static async ValueTask<int> SysStatfs64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var size = (int)a2;
        if (size < 84) return -(int)Errno.EINVAL;

        var path = sm.ReadString(a1);
        var dentry = sm.PathWalk(path);
        if (dentry?.Inode == null) return -(int)Errno.ENOENT;

        if (!sm.Engine.CopyToUser(a3, new byte[84])) return -(int)Errno.EFAULT;
        WriteStatfs64(sm, a3, dentry);
        return 0;
    }

    private static async ValueTask<int> SysFstatfs64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var size = (int)a2;
        if (size < 84) return -(int)Errno.EINVAL;

        var file = sm.GetFD(fd);
        if (file?.Dentry.Inode == null) return -(int)Errno.EBADF;

        if (!sm.Engine.CopyToUser(a3, new byte[84])) return -(int)Errno.EFAULT;
        WriteStatfs64(sm, a3, file.Dentry);
        return 0;
    }

    private static async ValueTask<int> SysStat64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return ImplStat64(state, a1, a2);
    }

    private static async ValueTask<int> SysLstat64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return ImplStat64(state, a1, a2);
    }

    private static int ImplStat64(IntPtr state, uint ptrPath, uint ptrStat)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var path = sm.Engine.ReadStringSafe(ptrPath);
        if (path == null) return -(int)Errno.EFAULT;

        Logger.LogInformation($"[Stat64] Path='{path}'");
        var dentry = sm.PathWalk(path);
        if (dentry == null || dentry.Inode == null)
        {
            Logger.LogWarning($"[Stat64] PathWalk failed for '{path}'");
            return -(int)Errno.ENOENT;
        }

        WriteStat64(sm, ptrStat, dentry.Inode);
        return 0;
    }

    private static async ValueTask<int> SysFstat64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;

        WriteStat64(sm, a2, f.Dentry.Inode);
        return 0;
    }

    private static async ValueTask<int> SysSymlink(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var target = sm.ReadString(a1);
        var linkpath = sm.ReadString(a2);

        var lastSlash = linkpath.LastIndexOf('/');
        var parentPath = lastSlash == -1 ? "" : linkpath[..lastSlash];
        var name = lastSlash == -1 ? linkpath : linkpath[(lastSlash + 1)..];

        var parentDentry = sm.PathWalk(parentPath == "" ? "." : parentPath);
        if (parentDentry == null || parentDentry.Inode == null) return -(int)Errno.ENOENT;

        var t = sm.Engine.Owner as FiberTask;
        var uid = t?.Process.EUID ?? 0;
        var gid = t?.Process.EGID ?? 0;

        try
        {
            var dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
            parentDentry.Inode.Symlink(dentry, target, uid, gid);
            return 0;
        }
        catch
        {
            return -(int)Errno.EACCES;
        }
    }

    private static async ValueTask<int> SysReadlink(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var path = sm.ReadString(a1);
        var bufAddr = a2;
        var bufSize = (int)a3;

        var dentry = sm.PathWalk(path, followLink: false);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;
        if (dentry.Inode.Type != InodeType.Symlink) return -(int)Errno.EINVAL;

        var target = dentry.Inode.Readlink();
        var bytes = Encoding.UTF8.GetBytes(target);
        var len = Math.Min(bytes.Length, bufSize);
        if (!sm.Engine.CopyToUser(bufAddr, bytes.AsSpan(0, len))) return -(int)Errno.EFAULT;
        return len;
    }

    private static async ValueTask<int> SysReadlinkAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var dirfd = (int)a1;
        var path = sm.ReadString(a2);
        var bufAddr = a3;
        var bufSize = (int)a4;

        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        var dentry = sm.PathWalk(path, startAt, false);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;
        if (dentry.Inode.Type != InodeType.Symlink) return -(int)Errno.EINVAL;

        var target = dentry.Inode.Readlink();
        var bytes = Encoding.UTF8.GetBytes(target);
        var len = Math.Min(bytes.Length, bufSize);
        if (!sm.Engine.CopyToUser(bufAddr, bytes.AsSpan(0, len))) return -(int)Errno.EFAULT;
        return len;
    }

    private static async ValueTask<int> SysSymlinkAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var target = sm.ReadString(a1);
        var dirfd = (int)a2;
        var linkpath = sm.ReadString(a3);

        Dentry? startAt = null;
        if (dirfd != -100 && !linkpath.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        var lastSlash = linkpath.LastIndexOf('/');
        var parentPath = lastSlash == -1 ? "" : linkpath[..lastSlash];
        var name = lastSlash == -1 ? linkpath : linkpath[(lastSlash + 1)..];

        var parentDentry = sm.PathWalk(parentPath == "" ? "." : parentPath, startAt);
        if (parentDentry == null || parentDentry.Inode == null) return -(int)Errno.ENOENT;

        var t = sm.Engine.Owner as FiberTask;
        var uid = t?.Process.EUID ?? 0;
        var gid = t?.Process.EGID ?? 0;

        try
        {
            var dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
            parentDentry.Inode.Symlink(dentry, target, uid, gid);
            return 0;
        }
        catch
        {
            return -(int)Errno.EACCES;
        }
    }

    private static async ValueTask<int> SysMount(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var source = a1 == 0 ? "" : sm.ReadString(a1);
        var target = sm.ReadString(a2);
        var fstype = a3 == 0 ? "" : sm.ReadString(a3);
        var flags = a4;
        var dataAddr = a5;

        var targetDentry = sm.PathWalk(target);
        if (targetDentry == null) return -(int)Errno.ENOENT;

        // mount(2) operates on the mountpoint itself, not on a followed mount root.
        if (targetDentry.MountedAt != null)
            targetDentry = targetDentry.MountedAt;

        SuperBlock? newSb = null;

        // Handle MS_BIND (Bind Mount)
        if ((flags & LinuxConstants.MS_BIND) != 0)
        {
            var srcDentry = sm.PathWalk(source);

            // If binding a host path, we might need a Hostfs SB?
            // Bind mount usually just attaching the existing dentry tree to a new location.
            // But our VFS 'mount' logic expects a SuperBlock root.
            // If we bind a directory, we effectively mount the SB of that directory?
            // Or we treat it as a new "BindFS"?
            // Existing logic created a new Hostfs SB rooted at source.
            // We'll preserve that behavior for HostFS compatibility.

            if (srcDentry != null && srcDentry.Inode is HostInode hi)
            {
                var hostRoot = hi.HostPath;
                var fsType = FileSystemRegistry.Get("hostfs");
                if (fsType != null)
                    try
                    {
                        newSb = fsType.FileSystem.ReadSuper(fsType, (int)flags, hostRoot, null);
                    }
                    catch
                    {
                    }
            }
            // For internal bind mounts (Tmpfs -> Tmpfs), we might need a different approach (BindDentry?), 
            // strictly following VFS struct, MountRoot points to a SB Root. 
            // We can't easily "bind" a subdirectory as a Root of a new SB without a "BindFileSystem" wrapper.
            // Fallback: only support Hostfs bind for now as before.
        }
        else
        {
            var fsType = FileSystemRegistry.Get(fstype);
            if (fsType != null)
            {
                // Special handling for Overlay Options parsing could go here if we support dynamic overlay mounts
                object? dataObj = null;

                // If passing string data options
                if (dataAddr != 0)
                {
                    // We could read string and pass it?
                    // Tmpfs ignores it. Hostfs uses it as root path? No, Hostfs uses 'source'.
                }

                try
                {
                    newSb = fsType.FileSystem.ReadSuper(fsType, (int)flags, source, dataObj);
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"Mount failed for {fstype}: {e.Message}");
                    return -(int)Errno.EINVAL;
                }
            }
            else
            {
                return -(int)Errno.ENODEV;
            }
        }


        if (newSb != null)
        {
            var targetPath = BuildPath(targetDentry);
            targetDentry.IsMounted = true;
            targetDentry.MountRoot = newSb.Root;
            newSb.Root.MountedAt = targetDentry;

            var src = string.IsNullOrEmpty(source) ? fstype : source;
            var opts = (flags & LinuxConstants.MS_RDONLY) != 0 ? "ro,relatime" : "rw,relatime";
            sm.AddMountInfo(src, targetPath, fstype, opts);
            return 0;
        }

        return -22; // EINVAL
    }

    private static async ValueTask<int> SysUmount(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var target = sm.ReadString(a1);
        var targetDentry = sm.PathWalk(target);

        // PathWalk follows mounts, so we need to check MountedAt to find the actual mount point
        if (targetDentry != null && targetDentry.MountedAt != null)
        {
            // This dentry is a mount root, find the mount point
            var mountPoint = targetDentry.MountedAt;
            var mountRoot = mountPoint.MountRoot;
            var targetPath = BuildPath(mountPoint);

            if (mountRoot == null) return -(int)Errno.EINVAL;

            // Check if filesystem is busy (has active inodes)
            if (mountRoot.SuperBlock.HasActiveInodes()) return -16; // EBUSY

            // Detach mount
            mountPoint.IsMounted = false;
            mountPoint.MountRoot = null;
            targetDentry.MountedAt = null;
            sm.RemoveMountInfo(targetPath);

            // Release SuperBlock reference
            mountRoot.SuperBlock.Put();
            return 0;
        }

        if (targetDentry != null && targetDentry.IsMounted)
        {
            // This is the mount point itself
            var mountRoot = targetDentry.MountRoot;
            var targetPath = BuildPath(targetDentry);

            if (mountRoot != null && mountRoot.SuperBlock.HasActiveInodes()) return -16; // EBUSY

            targetDentry.IsMounted = false;
            if (targetDentry.MountRoot != null)
            {
                targetDentry.MountRoot.MountedAt = null;
                targetDentry.MountRoot.SuperBlock.Put();
            }

            targetDentry.MountRoot = null;
            sm.RemoveMountInfo(targetPath);
            return 0;
        }

        return -22; // EINVAL
    }

    private static async ValueTask<int> SysUmount2(IntPtr state, uint a1, uint flags, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var target = sm.ReadString(a1);
        var targetDentry = sm.PathWalk(target);

        const uint MNT_FORCE = 1;
        const uint MNT_DETACH = 2;
        Dentry? mountPoint;
        Dentry? mountRoot;
        if (targetDentry != null && targetDentry.MountedAt != null)
        {
            mountPoint = targetDentry.MountedAt;
            mountRoot = mountPoint.MountRoot;
        }
        else if (targetDentry != null && targetDentry.IsMounted)
        {
            mountPoint = targetDentry;
            mountRoot = targetDentry.MountRoot;
        }
        else
        {
            return -22; // EINVAL
        }

        if (mountRoot == null) return -22; // EINVAL

        var targetPath = BuildPath(mountPoint);

        if ((flags & MNT_DETACH) != 0)
        {
            // Lazy unmount: detach immediately but allow active references to continue
            mountPoint.IsMounted = false;
            mountPoint.MountRoot = null;
            if (targetDentry?.MountedAt != null) targetDentry.MountedAt = null;
            else if (mountRoot.MountedAt != null) mountRoot.MountedAt = null;
            sm.RemoveMountInfo(targetPath);
            // Don't call sb.Put() - let reference counting naturally decrease when files close
            return 0;
        }

        // Normal umount with optional force
        if (mountRoot.SuperBlock.HasActiveInodes() && (flags & MNT_FORCE) == 0) return -16; // EBUSY

        // Force unmount or no active inodes
        mountPoint.IsMounted = false;
        mountPoint.MountRoot = null;
        if (targetDentry?.MountedAt != null) targetDentry.MountedAt = null;
        else if (mountRoot.MountedAt != null) mountRoot.MountedAt = null;
        sm.RemoveMountInfo(targetPath);
        mountRoot.SuperBlock.Put();

        return 0;
    }

    private static async ValueTask<int> SysChroot(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var path = sm.ReadString(a1);
        var dentry = sm.PathWalk(path);

        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;
        if (dentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR; // ENOTDIR

        sm.ProcessRoot = dentry;
        return 0;
    }

    private static async ValueTask<int> SysFlock(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var op = (int)a2;

        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;

        return f.Dentry.Inode.Flock(f, op);
    }
}