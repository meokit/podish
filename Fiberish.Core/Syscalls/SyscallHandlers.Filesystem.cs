using System.Buffers.Binary;
using System.Text;
using Fiberish.Auth.Permission;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998 // Async method lacks await operators - syscall handlers require async signature
    private static int MapFsExceptionToErrno(Exception ex, Errno fallback = Errno.EIO)
    {
        return ex switch
        {
            FileNotFoundException => -(int)Errno.ENOENT,
            DirectoryNotFoundException => -(int)Errno.ENOENT,
            UnauthorizedAccessException => -(int)Errno.EACCES,
            PathTooLongException => -(int)Errno.EINVAL,
            InvalidOperationException ioe when ioe.Message.Contains("Exists", StringComparison.OrdinalIgnoreCase) =>
                -(int)Errno.EEXIST,
            InvalidOperationException ioe
                when ioe.Message.Contains("Not a directory", StringComparison.OrdinalIgnoreCase) =>
                -(int)Errno.ENOTDIR,
            InvalidOperationException ioe
                when ioe.Message.Contains("Is a directory", StringComparison.OrdinalIgnoreCase) =>
                -(int)Errno.EISDIR,
            IOException ioe when ioe.Message.Contains("Exists", StringComparison.OrdinalIgnoreCase) => -(int)Errno
                .EEXIST,
            _ => -(int)fallback
        };
    }

    private static async ValueTask<int> SysLink(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var oldPath = sm.ReadString(a1);
        var newPath = sm.ReadString(a2);

        var oldLoc = sm.PathWalkWithFlags(oldPath, LookupFlags.FollowSymlink);
        if (!oldLoc.IsValid) return -(int)Errno.ENOENT;
        if (oldLoc.Dentry!.Inode!.Type == InodeType.Directory) return -(int)Errno.EPERM;

        var (dirLoc, name, err) = sm.PathWalkForCreate(newPath);
        if (err != 0) return err;
        if (!dirLoc.IsValid || dirLoc.Dentry!.Inode!.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

        if (!ReferenceEquals(oldLoc.Mount, dirLoc.Mount))
        {
            return -(int)Errno.EXDEV;
        }

        try
        {
            var newDentry = new Dentry(name, null, dirLoc.Dentry, dirLoc.Dentry.SuperBlock);
            dirLoc.Dentry.Inode.Link(newDentry, oldLoc.Dentry.Inode);
            return 0;
        }
        catch (Exception ex)
        {
            return MapFsExceptionToErrno(ex, Errno.EIO);
        }
    }

    private static async ValueTask<int> SysLinkat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var olddirfd = (int)a1;
        var oldpath = sm.ReadString(a2);
        var newdirfd = (int)a3;
        var newpath = sm.ReadString(a4);
        var flags = (int)a5;
        if ((flags & ~LinuxConstants.AT_SYMLINK_FOLLOW) != 0) return -(int)Errno.EINVAL;

        PathLocation oldStartLoc = default;
        if (olddirfd != unchecked((int)LinuxConstants.AT_FDCWD) && !oldpath.StartsWith("/"))
        {
            var fdir = sm.GetFD(olddirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            oldStartLoc = new PathLocation(fdir.Dentry, fdir.Mount);
        }

        PathLocation newStartLoc = default;
        if (newdirfd != unchecked((int)LinuxConstants.AT_FDCWD) && !newpath.StartsWith("/"))
        {
            var fdir = sm.GetFD(newdirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            newStartLoc = new PathLocation(fdir.Dentry, fdir.Mount);
        }

        var followLink = (flags & LinuxConstants.AT_SYMLINK_FOLLOW) != 0;
        var oldLoc = sm.PathWalkWithFlags(oldpath, oldStartLoc.IsValid ? oldStartLoc : sm.CurrentWorkingDirectory, followLink ? LookupFlags.FollowSymlink : LookupFlags.None);
        if (!oldLoc.IsValid)
        {
            return -(int)Errno.ENOENT;
        }
        if (oldLoc.Dentry!.Inode!.Type == InodeType.Directory) return -(int)Errno.EPERM;

        var (dirLoc, name, err) = sm.PathWalkForCreate(newpath, newStartLoc.IsValid ? newStartLoc : null);
        if (err != 0) return err;
        if (!dirLoc.IsValid || dirLoc.Dentry!.Inode!.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

        if (!ReferenceEquals(oldLoc.Mount, dirLoc.Mount))
        {
            return -(int)Errno.EXDEV;
        }

        try
        {
            var newDentry = new Dentry(name, null, dirLoc.Dentry, dirLoc.Dentry.SuperBlock);
            dirLoc.Dentry.Inode.Link(newDentry, oldLoc.Dentry.Inode);
            return 0;
        }
        catch (Exception ex)
        {
            return MapFsExceptionToErrno(ex, Errno.EIO);
        }
    }

    private static async ValueTask<int> SysChdir(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var path = sm.ReadString(a1);
        var loc = sm.PathWalkWithFlags(path, LookupFlags.FollowSymlink);
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;
        if (loc.Dentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

        sm.UpdateCurrentWorkingDirectory(loc, "SysChdir");
        return 0;
    }

    private static async ValueTask<int> SysFchdir(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var f = sm.GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;
        if (f.OpenedInode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

        sm.UpdateCurrentWorkingDirectory(new PathLocation(f.Dentry, f.Mount), "SysFchdir");
        return 0;
    }

    private static async ValueTask<int> SysMkdir(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var path = sm.ReadString(a1);
        var mode = a2;

        var (parentLoc, name, err) = sm.PathWalkForCreate(path);
        if (err != 0) return err;

        var t = sm.Engine.Owner as FiberTask;
        var uid = t?.Process.EUID ?? 0;
        var gid = t?.Process.EGID ?? 0;

        try
        {
            var dentry = new Dentry(name, null, parentLoc.Dentry, parentLoc.Dentry!.SuperBlock);
            var finalMode = DacPolicy.ApplyUmask((int)mode, t?.Process.Umask ?? 0);
            parentLoc.Dentry.Inode!.Mkdir(dentry, finalMode, uid, gid);
            return 0;
        }
        catch (Exception ex)
        {
            return MapFsExceptionToErrno(ex, Errno.EACCES);
        }
    }

    private static async ValueTask<int> SysTruncate(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await DoTruncate(state, a1, a2);
    }

    private static async ValueTask<int> SysTruncate64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
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

        var loc = sm.PathWalkWithFlags(path, LookupFlags.FollowSymlink);
        if (!loc.IsValid || loc.Dentry!.Inode == null) return new ValueTask<int>(-(int)Errno.ENOENT);

        // Check mount read-only
        if (loc.Mount!.IsReadOnly) return new ValueTask<int>(-(int)Errno.EROFS);

        // TODO: Permission Check

        return new ValueTask<int>(loc.Dentry.Inode.Truncate(length));
    }

    private static async ValueTask<int> SysFtruncate(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var length = (long)a2;

        var f = sm.GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;
        if (f.OpenedInode.Type == InodeType.Directory) return -(int)Errno.EINVAL;

        return f.OpenedInode.Truncate(length);
    }

    private static async ValueTask<int> SysFtruncate64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var length = (long)(((ulong)a3 << 32) | a2);

        var f = sm.GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;
        if (f.OpenedInode.Type == InodeType.Directory) return -(int)Errno.EINVAL;

        return f.OpenedInode.Truncate(length);
    }

    private static async ValueTask<int> SysRmdir(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var path = sm.ReadString(a1);

        var (parentLoc, name, err) = sm.PathWalkForCreate(path);
        if (err != 0) return err;

        // Check if directory exists and is empty
        var targetLoc = sm.PathWalkWithFlags(path, LookupFlags.None);
        if (!targetLoc.IsValid || targetLoc.Dentry!.Inode == null) return -(int)Errno.ENOENT;
        if (targetLoc.Dentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
        if (!ReferenceEquals(parentLoc.Mount, targetLoc.Mount)) return -(int)Errno.EBUSY;

        // Check if empty (only . and .. entries)
        var entries = targetLoc.Dentry.Inode.GetEntries();
        if (entries.Count > 2) return -(int)Errno.ENOTEMPTY; // Has more than . and ..

        try
        {
            parentLoc.Dentry!.Inode!.Rmdir(name);
            parentLoc.Dentry.Children.Remove(name);
            return 0;
        }
        catch (Exception ex)
        {
            return MapFsExceptionToErrno(ex, Errno.EACCES);
        }
    }

    private static async ValueTask<int> SysMkdirAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var dirfd = (int)a1;
        var path = sm.ReadString(a2);
        var mode = a3;

        PathLocation startLoc = default;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startLoc = new PathLocation(fdir.Dentry, fdir.Mount);
        }

        var (parentLoc, name, err) = sm.PathWalkForCreate(path, startLoc.IsValid ? startLoc : null);
        if (err != 0) return err;

        var t = sm.Engine.Owner as FiberTask;
        var uid = t?.Process.EUID ?? 0;
        var gid = t?.Process.EGID ?? 0;

        try
        {
            var dentry = new Dentry(name, null, parentLoc.Dentry, parentLoc.Dentry!.SuperBlock);
            var finalMode = DacPolicy.ApplyUmask((int)mode, t?.Process.Umask ?? 0);
            parentLoc.Dentry.Inode!.Mkdir(dentry, finalMode, uid, gid);
            return 0;
        }
        catch (Exception ex)
        {
            return MapFsExceptionToErrno(ex, Errno.EACCES);
        }
    }

    private static async ValueTask<int> SysMknod(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await SysMknodat(state, LinuxConstants.AT_FDCWD, a1, a2, a3, 0, 0);
    }

    private static async ValueTask<int> SysMknodat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var dirfd = (int)a1;
        var path = sm.ReadString(a2);
        var mode = (int)a3;
        var dev = a4;

        var startAtErr = ResolveStartAt(sm, dirfd, path, out var startAt);
        if (startAtErr != 0) return startAtErr;

        var (parentLoc, name, err) = sm.PathWalkForCreate(path, startAt);
        if (err != 0) return err;
        if (parentLoc.Mount != null && parentLoc.Mount.IsReadOnly) return -(int)Errno.EROFS;

        const int S_IFMT = 0xF000;
        const int S_IFIFO = 0x1000;
        const int S_IFCHR = 0x2000;
        const int S_IFBLK = 0x6000;
        const int S_IFREG = 0x8000;
        const int S_IFSOCK = 0xC000;

        var fileType = mode & S_IFMT;
        var t = sm.Engine.Owner as FiberTask;
        var uid = t?.Process.EUID ?? 0;
        var gid = t?.Process.EGID ?? 0;
        var finalMode = DacPolicy.ApplyUmask(mode & 0x0FFF, t?.Process.Umask ?? 0);
        var dentry = new Dentry(name, null, parentLoc.Dentry, parentLoc.Dentry!.SuperBlock);

        try
        {
            switch (fileType)
            {
                case S_IFREG:
                    parentLoc.Dentry.Inode!.Create(dentry, finalMode, uid, gid);
                    return 0;
                case S_IFIFO:
                    parentLoc.Dentry.Inode!.Mknod(dentry, finalMode, uid, gid, InodeType.Fifo, 0);
                    return 0;
                case S_IFCHR:
                    parentLoc.Dentry.Inode!.Mknod(dentry, finalMode, uid, gid, InodeType.CharDev, dev);
                    return 0;
                case S_IFBLK:
                    parentLoc.Dentry.Inode!.Mknod(dentry, finalMode, uid, gid, InodeType.BlockDev, dev);
                    return 0;
                case S_IFSOCK:
                    parentLoc.Dentry.Inode!.Mknod(dentry, finalMode, uid, gid, InodeType.Socket, 0);
                    return 0;
                default:
                    return -(int)Errno.EINVAL;
            }
        }
        catch (Exception ex)
        {
            return MapFsExceptionToErrno(ex, Errno.EACCES);
        }
    }

    private static async ValueTask<int> SysSetXAttr(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await SetXAttrPath(state, a1, a2, a3, a4, a5, LookupFlags.FollowSymlink);
    }

    private static async ValueTask<int> SysLSetXAttr(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await SetXAttrPath(state, a1, a2, a3, a4, a5, LookupFlags.None);
    }

    private static async ValueTask<int> SysFSetXAttr(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var name = sm.ReadString(a2);
        if (string.IsNullOrEmpty(name)) return -(int)Errno.EINVAL;
        if (a4 > int.MaxValue) return -(int)Errno.EINVAL;

        var f = sm.GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;

        var readErr = ReadUserBuffer(sm, a3, (int)a4, out var valueBytes);
        if (readErr != 0) return readErr;
        return f.OpenedInode.SetXAttr(name, valueBytes, (int)a5);
    }

    private static async ValueTask<int> SysGetXAttr(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await GetXAttrPath(state, a1, a2, a3, a4, LookupFlags.FollowSymlink);
    }

    private static async ValueTask<int> SysLGetXAttr(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await GetXAttrPath(state, a1, a2, a3, a4, LookupFlags.None);
    }

    private static async ValueTask<int> SysFGetXAttr(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var name = sm.ReadString(a2);
        var valueAddr = a3;
        if (a4 > int.MaxValue) return -(int)Errno.EINVAL;
        var size = (int)a4;
        if (string.IsNullOrEmpty(name)) return -(int)Errno.EINVAL;

        var f = sm.GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;
        return CopyXAttrToUser(sm, f.OpenedInode, name, valueAddr, size);
    }

    private static async ValueTask<int> SysListXAttr(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await ListXAttrPath(state, a1, a2, a3, LookupFlags.FollowSymlink);
    }

    private static async ValueTask<int> SysLListXAttr(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await ListXAttrPath(state, a1, a2, a3, LookupFlags.None);
    }

    private static async ValueTask<int> SysFListXAttr(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var listAddr = a2;
        if (a3 > int.MaxValue) return -(int)Errno.EINVAL;
        var size = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;
        return CopyXAttrListToUser(sm, f.OpenedInode, listAddr, size);
    }

    private static async ValueTask<int> SysRemoveXAttr(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        return await RemoveXAttrPath(state, a1, a2, LookupFlags.FollowSymlink);
    }

    private static async ValueTask<int> SysLRemoveXAttr(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        return await RemoveXAttrPath(state, a1, a2, LookupFlags.None);
    }

    private static async ValueTask<int> SysFRemoveXAttr(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var name = sm.ReadString(a2);
        if (string.IsNullOrEmpty(name)) return -(int)Errno.EINVAL;

        var f = sm.GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;
        return f.OpenedInode.RemoveXAttr(name);
    }

    private static ValueTask<int> SetXAttrPath(IntPtr state, uint pathPtr, uint namePtr, uint valuePtr, uint sizeRaw,
        uint flags, LookupFlags lookupFlags)
    {
        var sm = Get(state);
        if (sm == null) return new ValueTask<int>(-(int)Errno.EPERM);

        var path = sm.ReadString(pathPtr);
        var name = sm.ReadString(namePtr);
        if (string.IsNullOrEmpty(name)) return new ValueTask<int>(-(int)Errno.EINVAL);
        if (sizeRaw > int.MaxValue) return new ValueTask<int>(-(int)Errno.EINVAL);

        var loc = sm.PathWalkWithFlags(path, lookupFlags);
        if (!loc.IsValid || loc.Dentry?.Inode == null) return new ValueTask<int>(-(int)Errno.ENOENT);
        if (loc.Mount != null && loc.Mount.IsReadOnly) return new ValueTask<int>(-(int)Errno.EROFS);

        var readErr = ReadUserBuffer(sm, valuePtr, (int)sizeRaw, out var valueBytes);
        if (readErr != 0) return new ValueTask<int>(readErr);
        return new ValueTask<int>(loc.Dentry.Inode.SetXAttr(name, valueBytes, (int)flags));
    }

    private static ValueTask<int> GetXAttrPath(IntPtr state, uint pathPtr, uint namePtr, uint valuePtr, uint sizeRaw,
        LookupFlags lookupFlags)
    {
        var sm = Get(state);
        if (sm == null) return new ValueTask<int>(-(int)Errno.EPERM);

        var path = sm.ReadString(pathPtr);
        var name = sm.ReadString(namePtr);
        if (string.IsNullOrEmpty(name)) return new ValueTask<int>(-(int)Errno.EINVAL);
        if (sizeRaw > int.MaxValue) return new ValueTask<int>(-(int)Errno.EINVAL);

        var loc = sm.PathWalkWithFlags(path, lookupFlags);
        if (!loc.IsValid || loc.Dentry?.Inode == null) return new ValueTask<int>(-(int)Errno.ENOENT);
        return new ValueTask<int>(CopyXAttrToUser(sm, loc.Dentry.Inode, name, valuePtr, (int)sizeRaw));
    }

    private static ValueTask<int> ListXAttrPath(IntPtr state, uint pathPtr, uint listPtr, uint sizeRaw,
        LookupFlags lookupFlags)
    {
        var sm = Get(state);
        if (sm == null) return new ValueTask<int>(-(int)Errno.EPERM);

        var path = sm.ReadString(pathPtr);
        if (sizeRaw > int.MaxValue) return new ValueTask<int>(-(int)Errno.EINVAL);

        var loc = sm.PathWalkWithFlags(path, lookupFlags);
        if (!loc.IsValid || loc.Dentry?.Inode == null) return new ValueTask<int>(-(int)Errno.ENOENT);
        return new ValueTask<int>(CopyXAttrListToUser(sm, loc.Dentry.Inode, listPtr, (int)sizeRaw));
    }

    private static ValueTask<int> RemoveXAttrPath(IntPtr state, uint pathPtr, uint namePtr, LookupFlags lookupFlags)
    {
        var sm = Get(state);
        if (sm == null) return new ValueTask<int>(-(int)Errno.EPERM);

        var path = sm.ReadString(pathPtr);
        var name = sm.ReadString(namePtr);
        if (string.IsNullOrEmpty(name)) return new ValueTask<int>(-(int)Errno.EINVAL);

        var loc = sm.PathWalkWithFlags(path, lookupFlags);
        if (!loc.IsValid || loc.Dentry?.Inode == null) return new ValueTask<int>(-(int)Errno.ENOENT);
        if (loc.Mount != null && loc.Mount.IsReadOnly) return new ValueTask<int>(-(int)Errno.EROFS);
        return new ValueTask<int>(loc.Dentry.Inode.RemoveXAttr(name));
    }

    private static int CopyXAttrToUser(SyscallManager sm, Inode inode, string name, uint valueAddr, int size)
    {
        Span<byte> probe = stackalloc byte[0];
        var needed = inode.GetXAttr(name, probe);
        if (needed < 0) return needed;
        if (size == 0) return needed;
        if (valueAddr == 0) return -(int)Errno.EFAULT;

        var buf = new byte[size];
        var rc = inode.GetXAttr(name, buf);
        if (rc < 0) return rc;
        if (rc > size) return -(int)Errno.ERANGE;
        if (!sm.Engine.CopyToUser(valueAddr, buf.AsSpan(0, rc))) return -(int)Errno.EFAULT;
        return rc;
    }

    private static int CopyXAttrListToUser(SyscallManager sm, Inode inode, uint listAddr, int size)
    {
        Span<byte> probe = stackalloc byte[0];
        var needed = inode.ListXAttr(probe);
        if (needed < 0) return needed;
        if (size == 0) return needed;
        if (listAddr == 0) return -(int)Errno.EFAULT;

        var buf = new byte[size];
        var rc = inode.ListXAttr(buf);
        if (rc < 0) return rc;
        if (rc > size) return -(int)Errno.ERANGE;
        if (!sm.Engine.CopyToUser(listAddr, buf.AsSpan(0, rc))) return -(int)Errno.EFAULT;
        return rc;
    }

    private static int ResolveStartAt(SyscallManager sm, int dirfd, string path, out PathLocation? startAt)
    {
        startAt = null;
        if (path.StartsWith("/", StringComparison.Ordinal)) return 0;
        if (dirfd == unchecked((int)LinuxConstants.AT_FDCWD)) return 0;

        var fdir = sm.GetFD(dirfd);
        if (fdir == null) return -(int)Errno.EBADF;
        startAt = new PathLocation(fdir.Dentry, fdir.Mount);
        return 0;
    }

    private static int ReadUserBuffer(SyscallManager sm, uint addr, int size, out byte[] valueBytes)
    {
        valueBytes = [];
        if (size < 0) return -(int)Errno.EINVAL;
        if (size == 0) return 0;
        if (addr == 0) return -(int)Errno.EFAULT;

        valueBytes = new byte[size];
        if (!sm.Engine.CopyFromUser(addr, valueBytes)) return -(int)Errno.EFAULT;
        return 0;
    }

    private static async ValueTask<int> SysUnlinkAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var dirfd = (int)a1;
        var path = sm.ReadString(a2);
        var flags = a3;
        const uint AT_REMOVEDIR = 0x200;
        if ((flags & ~AT_REMOVEDIR) != 0) return -(int)Errno.EINVAL;

        PathLocation startLoc = default;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startLoc = new PathLocation(fdir.Dentry, fdir.Mount);
        }

        var (parentLoc, name, err) = sm.PathWalkForCreate(path, startLoc.IsValid ? startLoc : null);
        if (err != 0) return err;

        var targetLoc = sm.PathWalkWithFlags(path, startLoc.IsValid ? startLoc : sm.CurrentWorkingDirectory,
            LookupFlags.None);
        if (!targetLoc.IsValid || targetLoc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        if ((flags & AT_REMOVEDIR) != 0) // AT_REMOVEDIR
        {
            if (targetLoc.Dentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
            if (!ReferenceEquals(parentLoc.Mount, targetLoc.Mount)) return -(int)Errno.EBUSY;
            try
            {
                parentLoc.Dentry!.Inode!.Rmdir(name);
                parentLoc.Dentry.Children.Remove(name);
                return 0;
            }
            catch (Exception ex)
            {
                return MapFsExceptionToErrno(ex, Errno.EACCES);
            }
        }

        if (targetLoc.Dentry.Inode.Type == InodeType.Directory) return -(int)Errno.EISDIR;

        try
        {
            parentLoc.Dentry!.Inode!.Unlink(name);
            parentLoc.Dentry.Children.Remove(name);
            return 0;
        }
        catch (Exception ex)
        {
            return MapFsExceptionToErrno(ex, Errno.ENOENT);
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
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;
        if (f.OpenedInode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

        try
        {
            var entries = f.OpenedInode.GetEntries();
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
        var knownFlags = LinuxConstants.AT_EMPTY_PATH | LinuxConstants.AT_SYMLINK_NOFOLLOW;
        if ((flags & ~knownFlags) != 0) return -(int)Errno.EINVAL;

        if (path == "" && (flags & 0x1000) != 0) // AT_EMPTY_PATH
            return await SysFstat64(state, a1, a3, 0, 0, 0, 0);

        PathLocation startLoc = default;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startLoc = new PathLocation(fdir.Dentry, fdir.Mount);
        }

        var followLink = (flags & LinuxConstants.AT_SYMLINK_NOFOLLOW) == 0;
        var loc = sm.PathWalkWithFlags(path, startLoc.IsValid ? startLoc : sm.CurrentWorkingDirectory,
            followLink ? LookupFlags.FollowSymlink : LookupFlags.None);
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        RefreshHostfsProjectionForCaller(sm, loc.Dentry.Inode);
        WriteStat64(sm, statAddr, loc.Dentry.Inode);
        return 0;
    }

    private static async ValueTask<int> SysUtimensAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var path = sm.ReadString(a2);
        var timesAddr = a3;
        var loc = sm.PathWalkWithFlags(path, LookupFlags.FollowSymlink);
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        if (timesAddr == 0)
            loc.Dentry.Inode.ATime = loc.Dentry.Inode.MTime = DateTime.Now;
        else
            // TODO: Read timespec from memory
            loc.Dentry.Inode.ATime = loc.Dentry.Inode.MTime = DateTime.Now;
        return 0;
    }

    private static async ValueTask<int> SysFchownAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var dirfd = (int)a1;
        var path = sm.ReadString(a2);
        var flags = a5;
        if ((flags & ~LinuxConstants.AT_SYMLINK_NOFOLLOW) != 0) return -(int)Errno.EINVAL;
        PathLocation startLoc = default;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startLoc = new PathLocation(fdir.Dentry, fdir.Mount);
        }

        var followLink = (flags & LinuxConstants.AT_SYMLINK_NOFOLLOW) == 0;
        var loc = sm.PathWalkWithFlags(path, startLoc.IsValid ? startLoc : sm.CurrentWorkingDirectory,
            followLink ? LookupFlags.FollowSymlink : LookupFlags.None);
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        var uid = (int)a3;
        var gid = (int)a4;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        RefreshHostfsProjectionForCaller(sm, loc.Dentry.Inode);
        var allowed = DacPolicy.CanChown(task.Process, loc.Dentry.Inode, uid, gid);
        if (allowed != 0) return allowed;
        return ApplyOwnershipChange(loc.Dentry.Inode, uid, gid);
    }

    private static async ValueTask<int> SysFchmodAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var dirfd = (int)a1;
        var path = sm.ReadString(a2);
        PathLocation startLoc = default;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startLoc = new PathLocation(fdir.Dentry, fdir.Mount);
        }

        var loc = sm.PathWalkWithFlags(path, startLoc.IsValid ? startLoc : sm.CurrentWorkingDirectory,
            LookupFlags.FollowSymlink);
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        var mode = a3;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        RefreshHostfsProjectionForCaller(sm, loc.Dentry.Inode);
        var allowed = DacPolicy.CanChmod(task.Process, loc.Dentry.Inode);
        if (allowed != 0) return allowed;
        return ApplyModeChange(loc.Dentry.Inode, (int)mode, task.Process);
    }

    private static async ValueTask<int> SysFaccessAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var dirfd = (int)a1;
        var path = sm.ReadString(a2);
        PathLocation startLoc = default;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startLoc = new PathLocation(fdir.Dentry, fdir.Mount);
        }

        const uint AT_EACCESS = 0x200;
        var knownFlags = AT_EACCESS | LinuxConstants.AT_SYMLINK_NOFOLLOW;
        if ((a4 & ~knownFlags) != 0) return -(int)Errno.EINVAL;
        var followLink = (a4 & LinuxConstants.AT_SYMLINK_NOFOLLOW) == 0;
        var loc = sm.PathWalkWithFlags(path, startLoc.IsValid ? startLoc : sm.CurrentWorkingDirectory,
            followLink ? LookupFlags.FollowSymlink : LookupFlags.None);
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        var mode = (int)a3;
        if ((mode & ~7) != 0) return -(int)Errno.EINVAL;
        if (mode == 0) return 0; // F_OK

        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        RefreshHostfsProjectionForCaller(sm, loc.Dentry.Inode);

        var req = AccessMode.None;
        if ((mode & 4) != 0) req |= AccessMode.MayRead;
        if ((mode & 2) != 0) req |= AccessMode.MayWrite;
        if ((mode & 1) != 0) req |= AccessMode.MayExec;
        var useEffectiveIds = (a4 & AT_EACCESS) != 0;
        return DacPolicy.CheckPathAccess(task.Process, loc.Dentry.Inode, req, useEffectiveIds);
    }

    private static async ValueTask<int> SysFaccessAt2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        return await SysFaccessAt(state, a1, a2, a3, a4, a5, a6);
    }

    private static async ValueTask<int> SysFchmodAt2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        // fchmodat2 currently supports the same behavior as fchmodat(2) with flags=0.
        if (a4 != 0) return -(int)Errno.EINVAL;
        return await SysFchmodAt(state, a1, a2, a3, a4, a5, a6);
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
        var oldPath = sm.ReadString(a2);
        var newPath = sm.ReadString(a4);
        Logger.LogInformation($"[RenameAt] olddirfd={(int)a1} oldpath='{oldPath}' newdirfd={(int)a3} newpath='{newPath}'");
        return ImplRename(sm, (int)a1, oldPath, (int)a3, newPath, 0);
    }

    private static async ValueTask<int> SysRenameAt2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var oldPath = sm.ReadString(a2);
        var newPath = sm.ReadString(a4);
        Logger.LogInformation($"[RenameAt2] olddirfd={(int)a1} oldpath='{oldPath}' newdirfd={(int)a3} newpath='{newPath}' flags={a5}");
        return ImplRename(sm, (int)a1, oldPath, (int)a3, newPath, a5);
    }

    private static int ImplRename(SyscallManager sm, int oldDirFd, string oldPath, int newDirFd, string newPath,
        uint flags)
    {
        // TODO: add full renameat2 semantics for RENAME_EXCHANGE and RENAME_WHITEOUT.
        var supportedFlags = LinuxConstants.RENAME_NOREPLACE | LinuxConstants.RENAME_EXCHANGE;
        if ((flags & ~supportedFlags) != 0)
            return -(int)Errno.EINVAL;
        if ((flags & LinuxConstants.RENAME_NOREPLACE) != 0 && (flags & LinuxConstants.RENAME_EXCHANGE) != 0)
            return -(int)Errno.EINVAL;

        PathLocation? oldStart = null;
        if (oldDirFd != -100 && !oldPath.StartsWith("/"))
        {
            var f = sm.GetFD(oldDirFd);
            if (f == null) return -(int)Errno.EBADF;
            oldStart = new PathLocation(f.Dentry, f.Mount);
        }

        PathLocation? newStart = null;
        if (newDirFd != -100 && !newPath.StartsWith("/"))
        {
            var f = sm.GetFD(newDirFd);
            if (f == null) return -(int)Errno.EBADF;
            newStart = new PathLocation(f.Dentry, f.Mount);
        }

        var (oldParentLoc, oldName, oldErr) = sm.PathWalkForCreate(oldPath, oldStart);
        if (oldErr != 0)
        {
            Logger.LogInformation($"[Rename] PathWalkForCreate(oldPath) failed err={oldErr}");
            return oldErr;
        }

        var (newParentLoc, newName, newErr) = sm.PathWalkForCreate(newPath, newStart);
        if (newErr != 0)
        {
            Logger.LogInformation($"[Rename] PathWalkForCreate(newPath) failed err={newErr}");
            return newErr;
        }

        var oldLoc = oldStart.HasValue
            ? sm.PathWalkWithFlags(oldPath, oldStart.Value, LookupFlags.None)
            : sm.PathWalkWithFlags(oldPath, LookupFlags.None);
        if (!oldLoc.IsValid || oldLoc.Dentry?.Inode == null)
            return -(int)Errno.ENOENT;

        if (!ReferenceEquals(oldParentLoc.Mount, newParentLoc.Mount))
            return -(int)Errno.EXDEV;
        if (!ReferenceEquals(oldParentLoc.Mount, oldLoc.Mount))
            return -(int)Errno.EBUSY;

        var targetLoc = newStart.HasValue
            ? sm.PathWalkWithFlags(newPath, newStart.Value, LookupFlags.None)
            : sm.PathWalkWithFlags(newPath, LookupFlags.None);
        var targetExists = targetLoc.IsValid && targetLoc.Dentry?.Inode != null;
        var replacedTargetInode = targetExists ? targetLoc.Dentry!.Inode : null;
        if (targetExists && !ReferenceEquals(newParentLoc.Mount, targetLoc.Mount))
            return -(int)Errno.EBUSY;
        if (targetExists && ReferenceEquals(oldLoc.Mount, targetLoc.Mount) && ReferenceEquals(oldLoc.Dentry, targetLoc.Dentry))
            return 0;

        if ((flags & LinuxConstants.RENAME_NOREPLACE) != 0)
        {
            if (targetExists)
                return -(int)Errno.EEXIST;
        }

        if ((flags & LinuxConstants.RENAME_EXCHANGE) != 0)
        {
            if (!targetExists)
                return -(int)Errno.ENOENT;

            var tempName = $".rename-exchange-{Guid.NewGuid():N}";

            try
            {
                oldParentLoc.Dentry!.Inode!.Rename(oldName, oldParentLoc.Dentry.Inode, tempName);
                try
                {
                    newParentLoc.Dentry!.Inode!.Rename(newName, oldParentLoc.Dentry.Inode!, oldName);
                    try
                    {
                        oldParentLoc.Dentry.Inode!.Rename(tempName, newParentLoc.Dentry.Inode!, newName);
                    }
                    catch
                    {
                        try
                        {
                            oldParentLoc.Dentry.Inode!.Rename(oldName, newParentLoc.Dentry.Inode!, newName);
                        }
                        catch
                        {
                        }

                        throw;
                    }
                }
                catch
                {
                    try
                    {
                        oldParentLoc.Dentry.Inode!.Rename(tempName, oldParentLoc.Dentry.Inode, oldName);
                    }
                    catch
                    {
                    }

                    throw;
                }

                foreach (var pDentry in oldParentLoc.Dentry.Inode!.Dentries.ToList())
                {
                    pDentry.Children.Remove(oldName);
                    pDentry.Children.Remove(tempName);
                }

                foreach (var pDentry in newParentLoc.Dentry!.Inode!.Dentries.ToList())
                    pDentry.Children.Remove(newName);

                if (!ReferenceEquals(oldParentLoc.Dentry.Inode, newParentLoc.Dentry.Inode))
                {
                    foreach (var pDentry in oldParentLoc.Dentry.Inode.Dentries.ToList())
                        pDentry.Children.Remove(newName);
                    foreach (var pDentry in newParentLoc.Dentry.Inode.Dentries.ToList())
                        pDentry.Children.Remove(oldName);
                }

                return 0;
            }
            catch (FileNotFoundException)
            {
                return -(int)Errno.ENOENT;
            }
            catch (DirectoryNotFoundException)
            {
                return -(int)Errno.ENOENT;
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.LogInformation($"[RenameExchange] UnauthorizedAccessException: {ex.Message}");
                return -(int)Errno.EACCES;
            }
            catch (IOException ex)
            {
                Logger.LogInformation($"[RenameExchange] IOException: {ex.Message}");
                return -(int)Errno.EIO;
            }
            catch (Exception ex)
            {
                Logger.LogInformation($"[RenameExchange] Exception: {ex.Message}");
                return -(int)Errno.EACCES;
            }
        }

        try
        {
            oldParentLoc.Dentry!.Inode!.Rename(oldName, newParentLoc.Dentry!.Inode!, newName);

            // Update the VFS wrapper dentry cache across all views of the parent inodes.
            // This is critical because multiple Dentries might exist for the same Inode
            // (e.g., due to different path resolutions or OverlayFS wrapping).
            var oldParentInode = oldParentLoc.Dentry?.Inode;
            var newParentInode = newParentLoc.Dentry?.Inode;

            if (oldParentInode != null)
            {
                foreach (var pDentry in oldParentInode.Dentries.ToList())
                {
                    pDentry.Children.Remove(oldName);
                }
            }

            if (newParentInode != null)
            {
                foreach (var pDentry in newParentInode.Dentries.ToList())
                {
                    // Clean up only the pre-existing target dentry that got replaced.
                    // Do not tear down the freshly moved source dentry.
                    if (replacedTargetInode != null &&
                        pDentry.Children.TryGetValue(newName, out var victimDentry) &&
                        ReferenceEquals(victimDentry.Inode, replacedTargetInode))
                    {
                        if (victimDentry.Inode != null)
                        {
                            victimDentry.UnbindInode("SysRename.cleanup-replaced-target");
                        }
                        pDentry.Children.Remove(newName);
                    }
                }
            }

            // Note: We don't necessarily need to move the dentry object here; 
            // the next Lookup will create a fresh Dentry pointing to the correct Inode.
            // This ensures maximum correctness across all possible "views" of the FS.

            return 0;
        }
        catch (FileNotFoundException)
        {
            return -(int)Errno.ENOENT;
        }
        catch (DirectoryNotFoundException)
        {
            return -(int)Errno.ENOENT;
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.LogInformation($"[Rename] UnauthorizedAccessException: {ex.Message}");
            return -(int)Errno.EACCES;
        }
        catch (IOException ex)
        {
            Logger.LogInformation($"[Rename] IOException: {ex.Message}");
            return -(int)Errno.EIO;
        }
        catch (Exception ex)
        {
            Logger.LogInformation($"[Rename] Exception: {ex.Message}");
            return -(int)Errno.EACCES;
        }
    }

    private static async ValueTask<int> SysStat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var path = sm.ReadString(a1);
        var loc = sm.PathWalkWithFlags(path, LookupFlags.FollowSymlink);
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        RefreshHostfsProjectionForCaller(sm, loc.Dentry.Inode);
        WriteStat(sm, a2, loc.Dentry.Inode);
        return 0;
    }

    private static async ValueTask<int> SysLstat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var path = sm.ReadString(a1);
        var loc = sm.PathWalkWithFlags(path, LookupFlags.None);
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        RefreshHostfsProjectionForCaller(sm, loc.Dentry.Inode);
        WriteStat(sm, a2, loc.Dentry.Inode);
        return 0;
    }

    private static async ValueTask<int> SysFstat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var f = sm.GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;

        RefreshHostfsProjectionForCaller(sm, f.OpenedInode);
        WriteStat(sm, a2, f.OpenedInode);
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
            if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;
            RefreshHostfsProjectionForCaller(sm, f.OpenedInode);
            WriteStatx(sm, statxAddr, f.OpenedInode, mask);
            return 0;
        }

        PathLocation startLoc = default;
        if (dirfd != unchecked((int)LinuxConstants.AT_FDCWD) && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startLoc = new PathLocation(fdir.Dentry, fdir.Mount);
        }

        var followLink = (flags & LinuxConstants.AT_SYMLINK_NOFOLLOW) == 0;
        var loc = sm.PathWalkWithFlags(path, startLoc.IsValid ? startLoc : sm.CurrentWorkingDirectory,
            followLink ? LookupFlags.FollowSymlink : LookupFlags.None);
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        RefreshHostfsProjectionForCaller(sm, loc.Dentry.Inode);
        WriteStatx(sm, statxAddr, loc.Dentry.Inode, mask);
        return 0;
    }

    private static async ValueTask<int> SysChmod(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var path = sm.ReadString(a1);
        var mode = a2;

        var loc = sm.PathWalkWithFlags(path, LookupFlags.FollowSymlink);
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -2; // ENOENT

        RefreshHostfsProjectionForCaller(sm, loc.Dentry.Inode);
        var t = sm.Engine.Owner as FiberTask;
        if (t == null) return -(int)Errno.EPERM;
        var allowed = DacPolicy.CanChmod(t.Process, loc.Dentry.Inode);
        if (allowed != 0) return allowed;

        return ApplyModeChange(loc.Dentry.Inode, (int)mode, t.Process);
    }

    private static async ValueTask<int> SysFchmod(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var mode = a2;

        var f = sm.GetFD(fd);
        if (f == null || f.OpenedInode == null) return -9; // EBADF

        RefreshHostfsProjectionForCaller(sm, f.OpenedInode);
        var t = sm.Engine.Owner as FiberTask;
        if (t == null) return -(int)Errno.EPERM;
        var allowed = DacPolicy.CanChmod(t.Process, f.OpenedInode);
        if (allowed != 0) return allowed;

        return ApplyModeChange(f.OpenedInode, (int)mode, t.Process);
    }

    private static async ValueTask<int> SysChown(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var path = sm.ReadString(a1);
        var uid = (int)a2;
        var gid = (int)a3;

        var loc = sm.PathWalkWithFlags(path, LookupFlags.FollowSymlink);
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -2; // ENOENT

        RefreshHostfsProjectionForCaller(sm, loc.Dentry.Inode);
        var t = sm.Engine.Owner as FiberTask;
        if (t == null) return -(int)Errno.EPERM;
        var allowed = DacPolicy.CanChown(t.Process, loc.Dentry.Inode, uid, gid);
        if (allowed != 0) return allowed;

        return ApplyOwnershipChange(loc.Dentry.Inode, uid, gid);
    }

    private static async ValueTask<int> SysFchown(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var uid = (int)a2;
        var gid = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null || f.OpenedInode == null) return -9; // EBADF

        RefreshHostfsProjectionForCaller(sm, f.OpenedInode);
        var t = sm.Engine.Owner as FiberTask;
        if (t == null) return -(int)Errno.EPERM;
        var allowed = DacPolicy.CanChown(t.Process, f.OpenedInode, uid, gid);
        if (allowed != 0) return allowed;

        return ApplyOwnershipChange(f.OpenedInode, uid, gid);
    }

    private static async ValueTask<int> SysLchown(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var path = sm.ReadString(a1);
        var uid = (int)a2;
        var gid = (int)a3;

        var loc = sm.PathWalkWithFlags(path, LookupFlags.None);
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        RefreshHostfsProjectionForCaller(sm, loc.Dentry.Inode);
        var t = sm.Engine.Owner as FiberTask;
        if (t == null) return -(int)Errno.EPERM;
        var allowed = DacPolicy.CanChown(t.Process, loc.Dentry.Inode, uid, gid);
        if (allowed != 0) return allowed;

        return ApplyOwnershipChange(loc.Dentry.Inode, uid, gid);
    }

    private static async ValueTask<int> SysGetCwd(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var bufAddr = a1;
        var size = a2;

        var cwd = sm.GetAbsolutePath(sm.CurrentWorkingDirectory);
        if (cwd.Length + 1 > size) return -(int)Errno.ERANGE;

        if (!sm.Engine.CopyToUser(bufAddr, Encoding.ASCII.GetBytes(cwd + "\0"))) return -(int)Errno.EFAULT;
        return cwd.Length + 1;
    }

    private static async ValueTask<int> SysSync(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        sm.SyncContainerPageCache();
        return 0;
    }

    private static async ValueTask<int> SysFsync(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        var file = sm.GetFD((int)a1);
        if (file == null) return -(int)Errno.EBADF;
        var inode = file.OpenedInode;
        if (inode == null) return -(int)Errno.EBADF;

        ProcessAddressSpaceSync.SyncMappedFile(sm.Mem, sm.Engine, file);
        var writebackRc = inode.WritePages(file, new WritePagesRequest(0, long.MaxValue, true));
        if (writebackRc < 0 && writebackRc != -(int)Errno.EOPNOTSUPP && writebackRc != -(int)Errno.EROFS)
            return writebackRc;
        inode.Sync(file);
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

        var (parentLoc, name, err) = sm.PathWalkForCreate(path);
        if (err != 0) return err;

        var targetLoc = sm.PathWalkWithFlags(path, LookupFlags.None);
        if (!targetLoc.IsValid || targetLoc.Dentry!.Inode == null) return -(int)Errno.ENOENT;
        if (targetLoc.Dentry.Inode.Type == InodeType.Directory) return -(int)Errno.EISDIR;

        try
        {
            parentLoc.Dentry!.Inode!.Unlink(name);
            parentLoc.Dentry.Children.Remove(name);
            return 0;
        }
        catch (Exception ex)
        {
            return MapFsExceptionToErrno(ex, Errno.ENOENT);
        }
    }

    private static async ValueTask<int> SysAccess(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var path = sm.ReadString(a1);
        var mode = (int)a2;
        if ((mode & ~7) != 0) return -(int)Errno.EINVAL;
        var loc = sm.PathWalkWithFlags(path, LookupFlags.FollowSymlink);
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;
        if (mode == 0) return 0; // F_OK

        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        RefreshHostfsProjectionForCaller(sm, loc.Dentry.Inode);

        var req = AccessMode.None;
        if ((mode & 4) != 0) req |= AccessMode.MayRead;
        if ((mode & 2) != 0) req |= AccessMode.MayWrite;
        if ((mode & 1) != 0) req |= AccessMode.MayExec;
        return DacPolicy.CheckPathAccess(task.Process, loc.Dentry.Inode, req, useEffectiveIds: false);
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
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;

        try
        {
            var entries = f.OpenedInode.GetEntries();

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

    private static void RefreshHostfsProjectionForCaller(SyscallManager sm, Inode inode)
    {
        if (inode is not HostInode hostInode) return;
        var task = sm.Engine.Owner as FiberTask;
        var uid = task?.Process.EUID ?? 0;
        var gid = task?.Process.EGID ?? 0;
        hostInode.RefreshProjectedMetadata(uid, gid);
    }

    private static int ApplyOwnershipChange(Inode inode, int uid, int gid)
    {
        var oldUid = inode.Uid;
        var oldGid = inode.Gid;
        var newUid = uid == -1 ? oldUid : uid;
        var newGid = gid == -1 ? oldGid : gid;

        if (inode is HostInode hostInode)
        {
            var rc = hostInode.SetProjectedOwnership(uid, gid);
            if (rc != 0) return rc;
            inode.Mode = DacPolicy.ApplySetIdClearOnChown(inode, oldUid, oldGid, newUid, newGid);
            return 0;
        }

        inode.Uid = newUid;
        inode.Gid = newGid;
        inode.Mode = DacPolicy.ApplySetIdClearOnChown(inode, oldUid, oldGid, newUid, newGid);
        inode.CTime = DateTime.Now;
        return 0;
    }

    private static int ApplyModeChange(Inode inode, int mode, Process? process = null)
    {
        var normalizedMode = process == null ? (mode & 0xFFF) : DacPolicy.NormalizeChmodMode(process, inode, mode);
        if (inode is HostInode hostInode) return hostInode.SetProjectedMode(normalizedMode);

        inode.Mode = normalizedMode;
        inode.CTime = DateTime.Now;
        return 0;
    }

    private static void WriteStat64(SyscallManager sm, uint addr, Inode inode)
    {
        var buf = new byte[96];

        var mode = (uint)inode.Mode | (uint)inode.Type;
        var size = (long)inode.Size;
        var uid = (uint)inode.Uid;
        var gid = (uint)inode.Gid;

        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0), inode.Dev);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), (uint)inode.Ino); // __st_ino
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), mode);
        var nlink = inode.GetDebugNlinkForStat("WriteStat64", inode.GetLinkCountForStat());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), nlink);

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

        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), (ushort)inode.Dev); // st_dev
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)inode.Ino);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), (ushort)mode);
        var nlink = inode.GetDebugNlinkForStat("WriteStat", inode.GetLinkCountForStat());
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10), (ushort)nlink);
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

        var nlink = inode.GetDebugNlinkForStat("WriteStatx", inode.GetLinkCountForStat());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x10), nlink);
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
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x88), (inode.Dev >> 8) & 0xFF); // dev_major
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x8C), inode.Dev & 0xFF); // dev_minor

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
        var loc = sm.PathWalkWithFlags(path, LookupFlags.FollowSymlink);
        if (!loc.IsValid || loc.Dentry?.Inode == null) return -(int)Errno.ENOENT;

        if (!sm.Engine.CopyToUser(a2, new byte[64])) return -(int)Errno.EFAULT;
        WriteStatfs32(sm, a2, loc.Dentry);
        return 0;
    }

    private static async ValueTask<int> SysFstatfs(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var file = sm.GetFD(fd);
        if (file?.OpenedInode == null) return -(int)Errno.EBADF;

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
        var loc = sm.PathWalkWithFlags(path, LookupFlags.FollowSymlink);
        if (!loc.IsValid || loc.Dentry?.Inode == null) return -(int)Errno.ENOENT;

        if (!sm.Engine.CopyToUser(a3, new byte[84])) return -(int)Errno.EFAULT;
        WriteStatfs64(sm, a3, loc.Dentry);
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
        if (file?.OpenedInode == null) return -(int)Errno.EBADF;

        if (!sm.Engine.CopyToUser(a3, new byte[84])) return -(int)Errno.EFAULT;
        WriteStatfs64(sm, a3, file.Dentry);
        return 0;
    }

    private static async ValueTask<int> SysStat64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return ImplStat64(state, a1, a2, true);
    }

    private static async ValueTask<int> SysLstat64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return ImplStat64(state, a1, a2, false);
    }

    private static int ImplStat64(IntPtr state, uint ptrPath, uint ptrStat, bool followLink)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var path = sm.Engine.ReadStringSafe(ptrPath);
        if (path == null) return -(int)Errno.EFAULT;

        Logger.LogInformation($"[Stat64] Path='{path}'");
        var loc = sm.PathWalkWithFlags(path, followLink ? LookupFlags.FollowSymlink : LookupFlags.None);
        if (!loc.IsValid || loc.Dentry!.Inode == null)
        {
            Logger.LogWarning($"[Stat64] PathWalk failed for '{path}'");
            return -(int)Errno.ENOENT;
        }

        RefreshHostfsProjectionForCaller(sm, loc.Dentry.Inode);
        WriteStat64(sm, ptrStat, loc.Dentry.Inode);
        return 0;
    }

    private static async ValueTask<int> SysFstat64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var f = sm.GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;

        RefreshHostfsProjectionForCaller(sm, f.OpenedInode);
        WriteStat64(sm, a2, f.OpenedInode);
        return 0;
    }

    private static async ValueTask<int> SysSymlink(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var target = sm.ReadString(a1);
        var linkpath = sm.ReadString(a2);

        var (parentLoc, name, err) = sm.PathWalkForCreate(linkpath);
        if (err != 0) return err;

        var t = sm.Engine.Owner as FiberTask;
        var uid = t?.Process.EUID ?? 0;
        var gid = t?.Process.EGID ?? 0;

        try
        {
            var dentry = new Dentry(name, null, parentLoc.Dentry, parentLoc.Dentry!.SuperBlock);
            parentLoc.Dentry.Inode!.Symlink(dentry, target, uid, gid);
            return 0;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "symlink(target={Target}, linkpath={LinkPath}) failed", target, linkpath);
            return MapFsExceptionToErrno(ex, Errno.EACCES);
        }
    }

    private static async ValueTask<int> SysReadlink(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var path = sm.ReadString(a1);
        var bufAddr = a2;
        var bufSize = (int)a3;

        var loc = sm.PathWalkWithFlags(path, LookupFlags.None);
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;
        if (loc.Dentry.Inode.Type != InodeType.Symlink) return -(int)Errno.EINVAL;

        var target = loc.Dentry.Inode.Readlink();
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

        PathLocation? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = new PathLocation(fdir.Dentry, fdir.Mount);
        }

        var loc = sm.PathWalkWithFlags(path, startAt ?? sm.CurrentWorkingDirectory, LookupFlags.None);
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;
        if (loc.Dentry.Inode.Type != InodeType.Symlink) return -(int)Errno.EINVAL;

        var target = loc.Dentry.Inode.Readlink();
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

        PathLocation? startAt = null;
        if (dirfd != -100 && !linkpath.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = new PathLocation(fdir.Dentry, fdir.Mount);
        }

        var (parentLoc, name, err) = sm.PathWalkForCreate(linkpath, startAt);
        if (err != 0) return err;

        var t = sm.Engine.Owner as FiberTask;
        var uid = t?.Process.EUID ?? 0;
        var gid = t?.Process.EGID ?? 0;

        try
        {
            var dentry = new Dentry(name, null, parentLoc.Dentry, parentLoc.Dentry!.SuperBlock);
            parentLoc.Dentry.Inode!.Symlink(dentry, target, uid, gid);
            return 0;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "symlinkat(target={Target}, dirfd={Dirfd}, linkpath={LinkPath}) failed", target, dirfd,
                linkpath);
            return MapFsExceptionToErrno(ex, Errno.EACCES);
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
        string? dataString = null;
        if (dataAddr != 0) dataString = sm.ReadString(dataAddr);

        var targetLoc = sm.PathWalkWithFlags(target, LookupFlags.None);
        if (!targetLoc.IsValid) return -(int)Errno.ENOENT;

        var targetDentry = targetLoc.Dentry!;
        var targetMount = targetLoc.Mount!;

        // Handle MS_REMOUNT - change flags on existing mount
        if ((flags & LinuxConstants.MS_REMOUNT) != 0)
        {
            if (targetMount == null) return -(int)Errno.EINVAL;

            var remountSet = flags & SyscallManager.MountFlagMask;
            var remountClear = (~flags) & SyscallManager.MountFlagMask;
            targetMount.Flags = SyscallManager.ApplyMountFlagUpdate(targetMount.Flags, remountSet, remountClear);
            SyscallManager.RefreshMountOptions(targetMount);

            // Update MountList
            var targetPath = sm.GetAbsolutePath(targetLoc);

            return 0;
        }

        // Check if target is already a mount point
        if (targetDentry.IsMounted)
            return -(int)Errno.EBUSY;

        // Handle MS_BIND (Bind Mount)
        if ((flags & LinuxConstants.MS_BIND) != 0)
        {
            var srcLoc = sm.PathWalkWithFlags(source, LookupFlags.FollowSymlink);
            if (!srcLoc.IsValid || srcLoc.Dentry!.Inode == null)
                return -(int)Errno.ENOENT;

            var srcDentry = srcLoc.Dentry;
            var srcMount = srcLoc.Mount!;

            // Create a bind mount - clone the source mount with the specific dentry as root
            var bindMount = srcMount.Clone(srcDentry);
            bindMount.Source = source;
            bindMount.FsType = "none"; // bind mounts show as "none" in /proc/mounts
            bindMount.Flags = flags & SyscallManager.MountFlagMask;
            bindMount.Options = SyscallManager.BuildMountOptions(bindMount.Flags);

            var attachRc = sm.AttachDetachedMount(bindMount, targetLoc);
            if (attachRc != 0) return attachRc;

            var targetPath = sm.GetAbsolutePath(targetLoc);

            return 0;
        }

        // Regular mount (non-bind): converge to fs context + detached mount path
        var mountFlags = flags & SyscallManager.MountFlagMask;
        var fsCtx = sm.BuildFsContextFromLegacyMount(fstype, source, mountFlags, dataString);
        var mountRc = sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var newMount, (int)flags);
        if (mountRc != 0 || newMount == null)
            return mountRc != 0 ? mountRc : -(int)Errno.EINVAL;

        var attachRegularRc = sm.AttachDetachedMount(newMount, targetLoc);
        if (attachRegularRc != 0) return attachRegularRc;

        var targetPath2 = sm.GetAbsolutePath(targetLoc);

        return 0;
    }

    private static async ValueTask<int> SysUmount(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var target = sm.ReadString(a1);
        var targetLoc = sm.PathWalkWithFlags(target, LookupFlags.FollowSymlink);

        if (!targetLoc.IsValid || targetLoc.Mount == null) return -22; // EINVAL

        var mount = targetLoc.Mount;
        // If the path is not the root of the mount, it's not a mount point
        if (targetLoc.Dentry != mount.Root) return -22; // EINVAL

        if (mount == sm.Root.Mount) return -22; // EINVAL // Cannot unmount root

        // Check if filesystem is busy (has active inodes)
        if (mount.SB.HasActiveInodes()) return -16; // EBUSY

        var targetPath = sm.GetAbsolutePath(targetLoc);

        // Detach mount
        sm.UnregisterMount(mount);

        // Release SuperBlock reference
        mount.SB.Put();
        return 0;
    }

    private static async ValueTask<int> SysUmount2(IntPtr state, uint a1, uint flags, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var target = sm.ReadString(a1);
        var targetLoc = sm.PathWalkWithFlags(target, LookupFlags.FollowSymlink);

        if (!targetLoc.IsValid || targetLoc.Mount == null) return -22; // EINVAL

        var mount = targetLoc.Mount;
        if (targetLoc.Dentry != mount.Root) return -22; // EINVAL
        if (mount == sm.Root.Mount) return -22; // EINVAL // Cannot unmount root

        const uint MNT_FORCE = 1;
        const uint MNT_DETACH = 2;

        var targetPath = sm.GetAbsolutePath(targetLoc);

        if ((flags & MNT_DETACH) != 0)
        {
            // Lazy unmount: detach immediately but allow active references to continue
            sm.UnregisterMount(mount);
            // Don't call sb.Put() - let reference counting naturally decrease when files close
            return 0;
        }

        // Normal umount with optional force
        if (mount.SB.HasActiveInodes() && (flags & MNT_FORCE) == 0) return -16; // EBUSY

        // Force unmount or no active inodes
        sm.UnregisterMount(mount);
        mount.SB.Put();

        return 0;
    }

    private static async ValueTask<int> SysChroot(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        var allowed = DacPolicy.CanChroot(task.Process);
        if (allowed != 0) return allowed;

        var path = sm.ReadString(a1);
        var loc = sm.PathWalkWithFlags(path, LookupFlags.FollowSymlink);

        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;
        if (loc.Dentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR; // ENOTDIR

        sm.UpdateProcessRoot(loc, "SysChroot");
        return 0;
    }

    private static async ValueTask<int> SysFlock(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var op = (int)a2;

        var f = sm.GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;

        return f.OpenedInode.Flock(f, op);
    }

    #region New Mount API (open_tree, move_mount, mount_setattr)

    // fsopen flags
    private const uint FSOPEN_CLOEXEC = 0x00000001;

    // fsconfig commands
    private const uint FSCONFIG_SET_FLAG = 0;
    private const uint FSCONFIG_SET_STRING = 1;
    private const uint FSCONFIG_CMD_CREATE = 6;

    // fsmount flags
    private const uint FSMOUNT_CLOEXEC = 0x00000001;

    // open_tree flags
    private const uint OPEN_TREE_CLONE = 1;
    private const uint OPEN_TREE_CLOEXEC = (uint)FileFlags.O_CLOEXEC;
    private const uint AT_EMPTY_PATH = 0x1000;
    private const uint AT_SYMLINK_NOFOLLOW = 0x100;

    // move_mount flags
    private const uint MOVE_MOUNT_F_SYMLINKS = 0x00000001;
    private const uint MOVE_MOUNT_F_AUTOMOUNTS = 0x00000002;
    private const uint MOVE_MOUNT_F_EMPTY_PATH = 0x00000004;
    private const uint MOVE_MOUNT_T_SYMLINKS = 0x00000010;
    private const uint MOVE_MOUNT_T_AUTOMOUNTS = 0x00000020;
    private const uint MOVE_MOUNT_T_EMPTY_PATH = 0x00000040;

    // mount_setattr flags
    private const uint MOUNT_ATTR_RDONLY = 0x00000001;
    private const uint MOUNT_ATTR_NOSUID = 0x00000002;
    private const uint MOUNT_ATTR_NODEV = 0x00000004;
    private const uint MOUNT_ATTR_NOEXEC = 0x00000008;
    private const uint MOUNT_ATTR_ATIME = 0x00000010;
    private const uint MOUNT_ATTR_NOATIME = 0x00000020;
    private const uint MOUNT_ATTR_STRICTATIME = 0x00000040;
    private const uint MOUNT_ATTR_NODIRATIME = 0x00000080;
    private const uint MOUNT_ATTR_IDMAP = 0x00100000;

    /// <summary>
    /// fsopen(2) - Open filesystem context
    /// syscall number 430 on x86
    /// </summary>
    private static async ValueTask<int> SysFsopen(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fsName = sm.ReadString(a1);
        var flags = a2;

        if (string.IsNullOrEmpty(fsName)) return -(int)Errno.EINVAL;
        if ((flags & ~FSOPEN_CLOEXEC) != 0) return -(int)Errno.EINVAL;

        var fsType = FileSystemRegistry.Get(fsName);
        if (fsType == null) return -(int)Errno.ENODEV;

        var fileFlags = (flags & FSOPEN_CLOEXEC) != 0 ? FileFlags.O_CLOEXEC | FileFlags.O_RDONLY : FileFlags.O_RDONLY;
        var fsCtx = new FsContextFile(sm.AnonMount.Root, sm.AnonMount, fsName, fileFlags);
        return sm.AllocFD(fsCtx);
    }

    /// <summary>
    /// fsconfig(2) - Configure filesystem context
    /// syscall number 431 on x86
    /// </summary>
    private static async ValueTask<int> SysFsconfig(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fsFd = (int)a1;
        var cmd = a2;
        var keyPtr = a3;
        var valuePtr = a4;
        var aux = (int)a5;

        var file = sm.GetFD(fsFd);
        if (file is not FsContextFile fsCtx) return -(int)Errno.EBADF;
        if (fsCtx.State == FsContextState.Created && cmd != FSCONFIG_CMD_CREATE) return -(int)Errno.EBUSY;

        switch (cmd)
        {
            case FSCONFIG_SET_FLAG:
            {
                var key = sm.ReadString(keyPtr);
                if (string.IsNullOrEmpty(key)) return -(int)Errno.EINVAL;
                fsCtx.SetFlag(key);
                return 0;
            }
            case FSCONFIG_SET_STRING:
            {
                var key = sm.ReadString(keyPtr);
                if (string.IsNullOrEmpty(key)) return -(int)Errno.EINVAL;
                if (valuePtr == 0) return -(int)Errno.EINVAL;
                var value = sm.ReadString(valuePtr);
                fsCtx.SetString(key, value);
                return 0;
            }
            case FSCONFIG_CMD_CREATE:
            {
                // Minimal validation: only one create transition.
                if (fsCtx.State == FsContextState.Created) return -(int)Errno.EBUSY;
                fsCtx.State = FsContextState.Created;
                return 0;
            }
            default:
                _ = aux; // reserved for future command support
                return -(int)Errno.EINVAL;
        }
    }

    /// <summary>
    /// fsmount(2) - Create detached mount from context
    /// syscall number 432 on x86
    /// </summary>
    private static async ValueTask<int> SysFsmount(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fsFd = (int)a1;
        var flags = a2;
        var mountAttrs = a3;

        if ((flags & ~FSMOUNT_CLOEXEC) != 0) return -(int)Errno.EINVAL;

        var file = sm.GetFD(fsFd);
        if (file is not FsContextFile fsCtx) return -(int)Errno.EBADF;
        if (fsCtx.State != FsContextState.Created) return -(int)Errno.EINVAL;

        var mountRc = sm.CreateDetachedMountFromFsContext(fsCtx, mountAttrs, out var detachedMount);
        if (mountRc != 0) return mountRc;

        var mountFileFlags =
            (flags & FSMOUNT_CLOEXEC) != 0 ? FileFlags.O_CLOEXEC | FileFlags.O_RDONLY : FileFlags.O_RDONLY;
        var mountFile = new MountFile(detachedMount!, mountFileFlags);
        return sm.AllocFD(mountFile);
    }

    /// <summary>
    /// open_tree(2) - Get a file descriptor for a mount point
    /// syscall number 428 on x86
    /// </summary>
    private static async ValueTask<int> SysOpenTree(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var dfd = (int)a1; // dirfd
        var pathname = a2; // path string address
        var flags = a3; // flags (OPEN_TREE_CLONE, etc.)

        // Resolve path
        PathLocation loc;
        if ((flags & AT_EMPTY_PATH) != 0 && pathname == 0)
        {
            // Use dfd directly
            var f = sm.GetFD(dfd);
            if (f == null) return -(int)Errno.EBADF;
            loc = new PathLocation(f.Dentry, f.Mount);
        }
        else
        {
            var path = sm.ReadString(pathname);
            var followSymlinks = (flags & AT_SYMLINK_NOFOLLOW) == 0;
            loc = sm.PathWalkWithFlags(path, followSymlinks ? LookupFlags.FollowSymlink : LookupFlags.None);
        }

        if (!loc.IsValid) return -(int)Errno.ENOENT;
        if (loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        var mount = loc.Mount!;

        MountFile mountFile;
        if ((flags & OPEN_TREE_CLONE) != 0)
        {
            // Clone the mount with the specific dentry as root (for bind mounts)
            var clonedMount = mount.Clone(loc.Dentry);
            var mountFileFlags =
                (flags & OPEN_TREE_CLOEXEC) != 0 ? FileFlags.O_CLOEXEC | FileFlags.O_RDONLY : FileFlags.O_RDONLY;
            mountFile = new MountFile(clonedMount, mountFileFlags);
        }
        else
        {
            // Return a file descriptor for the mount point (not cloning)
            var mountFileFlags =
                (flags & OPEN_TREE_CLOEXEC) != 0 ? FileFlags.O_CLOEXEC | FileFlags.O_RDONLY : FileFlags.O_RDONLY;
            mountFile = new MountFile(mount, mountFileFlags);
        }

        // Allocate FD
        var newFd = sm.AllocFD(mountFile);
        return newFd;
    }

    /// <summary>
    /// move_mount(2) - Move a mount from one place to another
    /// syscall number 429 on x86
    /// </summary>
    private static async ValueTask<int> SysMoveMount(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fromDfd = (int)a1; // source dirfd
        var fromPath = a2; // source path
        var toDfd = (int)a3; // target dirfd
        var toPath = a4; // target path
        var flags = a5; // flags

        // Get source mount from fromDfd (must be a MountFile from open_tree)
        var fromFile = sm.GetFD(fromDfd);
        if (fromFile == null) return -(int)Errno.EBADF;
        if (fromFile is not MountFile mountFile) return -(int)Errno.EINVAL;

        var mount = mountFile.Mount;
        if (mount == null) return -(int)Errno.EINVAL;

        // Resolve target path
        PathLocation toLoc;
        if ((flags & MOVE_MOUNT_T_EMPTY_PATH) != 0 && toPath == 0)
        {
            var f = sm.GetFD(toDfd);
            if (f == null) return -(int)Errno.EBADF;
            toLoc = new PathLocation(f.Dentry, f.Mount);
        }
        else
        {
            var path = sm.ReadString(toPath);
            toLoc = sm.PathWalkWithFlags(path, LookupFlags.FollowSymlink);
        }

        if (!toLoc.IsValid) return -(int)Errno.ENOENT;
        if (toLoc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        var attachRc = sm.AttachDetachedMount(mount, toLoc);
        if (attachRc != 0) return attachRc;

        var targetPathStr = sm.GetAbsolutePath(toLoc);

        return 0;
    }

    /// <summary>
    /// mount_setattr(2) - Set attributes on a mount
    /// syscall number 442 on x86
    /// </summary>
    private static async ValueTask<int> SysMountSetattr(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var dfd = (int)a1; // dirfd
        var pathAddr = a2; // path
        var flags = a3; // flags (AT_RECURSIVE, etc.)
        var uattr = a4; // struct mount_attr pointer
        var usize = (int)a5; // size of mount_attr

        // Resolve path
        PathLocation loc;
        if ((flags & AT_EMPTY_PATH) != 0 && pathAddr == 0)
        {
            var f = sm.GetFD(dfd);
            if (f == null) return -(int)Errno.EBADF;
            loc = new PathLocation(f.Dentry, f.Mount);
        }
        else
        {
            var pathStr = sm.ReadString(pathAddr);
            loc = sm.PathWalkWithFlags(pathStr, LookupFlags.FollowSymlink);
        }

        if (!loc.IsValid) return -(int)Errno.ENOENT;

        var mount = loc.Mount!;
        if (mount == null) return -(int)Errno.ENOENT;

        // Read mount_attr structure from guest memory
        // struct mount_attr {
        //     __u64 attr_set;     /* Mount properties to set */
        //     __u64 attr_clr;     /* Mount properties to clear */
        //     __u64 propagation;  /* Mount propagation type */
        //     __u64 userns_fd;    /* User namespace file descriptor */
        // };
        if (uattr == 0 || usize < 32) return -(int)Errno.EINVAL;

        var buf = new byte[32];
        if (!sm.Engine.CopyFromUser(uattr, buf))
            return -(int)Errno.EFAULT;

        var attrSet = BitConverter.ToUInt64(buf, 0);
        var attrClr = BitConverter.ToUInt64(buf, 8);

        var setMask = SyscallManager.MapMountAttrToMountFlags(attrSet);
        var clearMask = SyscallManager.MapMountAttrToMountFlags(attrClr);
        mount.Flags = SyscallManager.ApplyMountFlagUpdate(mount.Flags, setMask, clearMask);
        SyscallManager.RefreshMountOptions(mount);

        return 0;
    }

    #endregion
}
