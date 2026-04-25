using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Fiberish.Auth.Permission;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.SilkFS;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    private const int StackDirentBufferLimit = 512;

    private static bool IsDotPath(ReadOnlySpan<byte> path)
    {
        return path.Length == 1 && path[0] == (byte)'.';
    }

    private static bool EndsWithSlashDot(ReadOnlySpan<byte> path)
    {
        return path.Length >= 2 && path[^2] == (byte)'/' && path[^1] == (byte)'.';
    }

#pragma warning disable CS1998 // Async method lacks await operators - syscall handlers require async signature
    private async ValueTask<int> SysLink(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var oldPathErr = ReadPathArgumentBytes(a1, out var oldPath);
        if (oldPathErr != 0) return oldPathErr;
        using var oldPathLease = oldPath;
        var newPathErr = ReadPathArgumentBytes(a2, out var newPath);
        if (newPathErr != 0) return newPathErr;
        using var newPathLease = newPath;

        var oldLookup = PathWalker.PathWalkWithData(oldPath.UnsafeBuffer, oldPath.Length, null, LookupFlags.None);
        if (oldLookup.HasError) return oldLookup.ErrorCode;
        var oldLoc = oldLookup.Path;
        if (!oldLoc.IsValid) return -(int)Errno.ENOENT;

        var (dirLoc, name, err) = PathWalker.PathWalkForCreate(newPath.UnsafeBuffer, newPath.Length);
        if (err != 0) return err;
        if (!dirLoc.IsValid || dirLoc.Dentry!.Inode!.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

        if (oldLoc.Dentry!.Inode!.Type == InodeType.Directory)
        {
            var newLookup = PathWalker.PathWalkWithData(newPath.UnsafeBuffer, newPath.Length, null, LookupFlags.None);
            if (!newLookup.HasError && newLookup.Path.IsValid)
                return -(int)Errno.EEXIST;
            return -(int)Errno.EPERM;
        }

        if (!ReferenceEquals(oldLoc.Mount, dirLoc.Mount)) return -(int)Errno.EXDEV;

        var newDentry = new Dentry(name, null, dirLoc.Dentry, dirLoc.Dentry.SuperBlock);
        return dirLoc.Dentry.Inode.Link(newDentry, oldLoc.Dentry.Inode);
    }

    private async ValueTask<int> SysLinkat(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var olddirfd = (int)a1;
        var oldPathErr = ReadPathArgumentBytes(a2, out var oldpath);
        if (oldPathErr != 0) return oldPathErr;
        using var oldPathLease = oldpath;
        var newdirfd = (int)a3;
        var newPathErr = ReadPathArgumentBytes(a4, out var newpath);
        if (newPathErr != 0) return newPathErr;
        using var newPathLease = newpath;
        var flags = (int)a5;
        if ((flags & ~LinuxConstants.AT_SYMLINK_FOLLOW) != 0) return -(int)Errno.EINVAL;

        PathLocation oldStartLoc = default;
        if (olddirfd != unchecked((int)LinuxConstants.AT_FDCWD) && !oldpath.IsAbsolute)
        {
            var fdir = GetFD(olddirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            oldStartLoc = fdir.LivePath;
        }

        PathLocation newStartLoc = default;
        if (newdirfd != unchecked((int)LinuxConstants.AT_FDCWD) && !newpath.IsAbsolute)
        {
            var fdir = GetFD(newdirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            newStartLoc = fdir.LivePath;
        }

        var followLink = (flags & LinuxConstants.AT_SYMLINK_FOLLOW) != 0;
        var oldLookup = PathWalker.PathWalkWithData(oldpath.UnsafeBuffer, oldpath.Length,
            oldStartLoc.IsValid ? oldStartLoc : CurrentWorkingDirectory,
            followLink ? LookupFlags.FollowSymlink : LookupFlags.None);
        if (oldLookup.HasError) return oldLookup.ErrorCode;
        var oldLoc = oldLookup.Path;
        if (!oldLoc.IsValid) return -(int)Errno.ENOENT;

        var (dirLoc, name, err) =
            PathWalker.PathWalkForCreate(newpath.UnsafeBuffer, newpath.Length, newStartLoc.IsValid ? newStartLoc : null);
        if (err != 0) return err;
        if (!dirLoc.IsValid || dirLoc.Dentry!.Inode!.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

        if (oldLoc.Dentry!.Inode!.Type == InodeType.Directory)
        {
            var newLookup = PathWalker.PathWalkWithData(newpath.UnsafeBuffer, newpath.Length,
                newStartLoc.IsValid ? newStartLoc : CurrentWorkingDirectory, LookupFlags.None);
            if (!newLookup.HasError && newLookup.Path.IsValid)
                return -(int)Errno.EEXIST;
            return -(int)Errno.EPERM;
        }

        if (!ReferenceEquals(oldLoc.Mount, dirLoc.Mount)) return -(int)Errno.EXDEV;

        var newDentry = new Dentry(name, null, dirLoc.Dentry, dirLoc.Dentry.SuperBlock);
        return dirLoc.Dentry.Inode.Link(newDentry, oldLoc.Dentry.Inode);
    }

    private async ValueTask<int> SysChdir(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var pathErr = ReadPathArgumentBytes(a1, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        var lookup = PathWalker.PathWalkWithData(path.UnsafeBuffer, path.Length, null, LookupFlags.FollowSymlink);
        if (lookup.HasError) return lookup.ErrorCode;
        var loc = lookup.Path;
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;
        if (loc.Dentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

        UpdateCurrentWorkingDirectory(loc, "SysChdir");
        return 0;
    }

    private async ValueTask<int> SysFchdir(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fd = (int)a1;
        var f = GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;
        if (f.OpenedInode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

        UpdateCurrentWorkingDirectory(f.LivePath, "SysFchdir");
        return 0;
    }

    private async ValueTask<int> SysMkdir(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var pathErr = ReadPathArgumentBytes(a1, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        if (IsRootOnlyPath(path.UnsafeBuffer, path.Length))
            return -(int)Errno.EEXIST;
        var mode = a2;

        var (parentLoc, name, err) = PathWalker.PathWalkForCreate(path.UnsafeBuffer, path.Length);
        if (err != 0) return err;

        var dentry = new Dentry(name, null, parentLoc.Dentry, parentLoc.Dentry!.SuperBlock);
        var create = DacPolicy.ComputeCreationMetadata((engine.Owner as FiberTask)?.Process, parentLoc.Dentry.Inode!,
            (int)mode, true);
        return parentLoc.Dentry.Inode!.Mkdir(dentry, create.Mode, create.Uid, create.Gid);
    }

    private async ValueTask<int> SysTruncate(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await DoTruncate(this, engine, a1, a2);
    }

    private async ValueTask<int> SysTruncate64(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        // 32-bit truncate64 has padding if we use 64-bit length. a2 and a3 are the split 64 bit integer
        var length = (long)(((ulong)a3 << 32) | a2);
        return await DoTruncate(this, engine, a1, length);
    }

    private static ValueTask<int> DoTruncate(SyscallManager sm, Engine engine, uint pathPtr, long length)
    {
        var pathErr = sm.ReadPathArgumentBytes(pathPtr, out var path);
        if (pathErr != 0)
            return new ValueTask<int>(pathErr);
        using var pathLease = path;

        var lookup = sm.PathWalker.PathWalkWithData(path.UnsafeBuffer, path.Length, null, LookupFlags.FollowSymlink);
        if (lookup.HasError)
            return new ValueTask<int>(lookup.ErrorCode);

        var loc = lookup.Path;
        if (!loc.IsValid || loc.Dentry!.Inode == null)
            return new ValueTask<int>(-(int)Errno.ENOENT);

        if (sm.CurrentTask?.Process != null)
        {
            var accessRc =
                DacPolicy.CheckPathAccess(sm.CurrentTask.Process, loc.Dentry.Inode, AccessMode.MayWrite, true);
            if (accessRc < 0)
                return new ValueTask<int>(accessRc);
        }

        // Check mount read-only
        if (loc.Mount!.IsReadOnly) return new ValueTask<int>(-(int)Errno.EROFS);

        // TODO: Permission Check

        var inode = loc.Dentry.Inode;
        return new ValueTask<int>(inode.Truncate(length, sm.CreateFileMutationContext(engine)));
    }

    private async ValueTask<int> SysFtruncate(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fd = (int)a1;
        var length = (long)a2;

        var f = GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;
        if (f.OpenedInode.Type == InodeType.Directory) return -(int)Errno.EINVAL;
        if ((f.Flags & FileFlags.O_ACCMODE) == FileFlags.O_RDONLY) return -(int)Errno.EINVAL;

        return f.OpenedInode.Truncate(length, CreateFileMutationContext(engine));
    }

    private async ValueTask<int> SysFtruncate64(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var fd = (int)a1;
        var length = (long)(((ulong)a3 << 32) | a2);

        var f = GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;
        if (f.OpenedInode.Type == InodeType.Directory) return -(int)Errno.EINVAL;
        if ((f.Flags & FileFlags.O_ACCMODE) == FileFlags.O_RDONLY) return -(int)Errno.EINVAL;

        return f.OpenedInode.Truncate(length, CreateFileMutationContext(engine));
    }

    private async ValueTask<int> SysFallocate(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        const int FallocFlKeepSize = 0x01;
        const int supportedFlags = FallocFlKeepSize;

        var fd = (int)a1;
        var mode = unchecked((int)a2);
        var offset = (long)(((ulong)a4 << 32) | a3);
        var length = (long)(((ulong)a6 << 32) | a5);

        if (mode < 0 || (mode & ~supportedFlags) != 0)
            return -(int)Errno.EOPNOTSUPP;
        if (offset < 0 || length <= 0)
            return -(int)Errno.EINVAL;

        var file = GetFD(fd);
        if (file?.OpenedInode == null)
            return -(int)Errno.EBADF;
        if (file.OpenedInode.Type == InodeType.Directory)
            return -(int)Errno.EISDIR;

        long endOffset;
        try
        {
            endOffset = checked(offset + length);
        }
        catch (OverflowException)
        {
            return -(int)Errno.EFBIG;
        }

        if ((mode & FallocFlKeepSize) != 0)
            return 0;

        if (endOffset <= (long)file.OpenedInode.Size)
            return 0;

        return file.OpenedInode.Truncate(endOffset, CreateFileMutationContext(engine));
    }

    private async ValueTask<int> SysRmdir(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var pathErr = ReadPathArgumentBytes(a1, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        if (IsDotPath(path.Span) || EndsWithSlashDot(path.Span)) return -(int)Errno.EINVAL;

        var (parentLoc, name, err) = PathWalker.PathWalkForCreate(path.UnsafeBuffer, path.Length);
        if (err != 0) return err;

        // Check if directory exists and is empty
        var targetLookup = PathWalker.PathWalkWithData(path.UnsafeBuffer, path.Length, null, LookupFlags.None);
        if (targetLookup.HasError) return targetLookup.ErrorCode;
        var targetLoc = targetLookup.Path;
        if (!targetLoc.IsValid || targetLoc.Dentry!.Inode == null) return -(int)Errno.ENOENT;
        if (targetLoc.Dentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
        if (!ReferenceEquals(parentLoc.Mount, targetLoc.Mount)) return -(int)Errno.EBUSY;

        // Check if empty (only . and .. entries)
        var task = engine.Owner as FiberTask;
        var entries = task != null && targetLoc.Dentry.Inode is IContextualDirectoryInode contextualDirectory
            ? contextualDirectory.GetEntries(task)
            : targetLoc.Dentry.Inode.GetEntries();
        if (entries.Count > 2) return -(int)Errno.ENOTEMPTY; // Has more than . and ..
        if (task?.Process != null)
        {
            var stickyRc =
                DacPolicy.CanRemoveOrRenameEntry(task.Process, parentLoc.Dentry!.Inode!, targetLoc.Dentry.Inode);
            if (stickyRc != 0) return stickyRc;
        }

        var rmdirRc = parentLoc.Dentry!.Inode!.Rmdir(name);
        if (rmdirRc < 0)
            return rmdirRc;
        _ = parentLoc.Dentry.TryUncacheChild(name, "SysRmdir", out _);
        return 0;
    }

    private async ValueTask<int> SysMkdirAt(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var dirfd = (int)a1;
        var pathErr = ReadPathArgumentBytes(a2, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        if (IsRootOnlyPath(path.UnsafeBuffer, path.Length))
            return -(int)Errno.EEXIST;
        var mode = a3;

        PathLocation startLoc = default;
        if (dirfd != -100 && !path.IsAbsolute)
        {
            var fdir = GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startLoc = fdir.LivePath;
        }

        var (parentLoc, name, err) =
            PathWalker.PathWalkForCreate(path.UnsafeBuffer, path.Length, startLoc.IsValid ? startLoc : null);
        if (err != 0) return err;

        var dentry = new Dentry(name, null, parentLoc.Dentry, parentLoc.Dentry!.SuperBlock);
        var create = DacPolicy.ComputeCreationMetadata((engine.Owner as FiberTask)?.Process, parentLoc.Dentry.Inode!,
            (int)mode, true);
        return parentLoc.Dentry.Inode!.Mkdir(dentry, create.Mode, create.Uid, create.Gid);
    }

    private static bool IsRootOnlyPath(byte[] pathBytes, int pathLength)
    {
        if (pathLength <= 0)
            return false;

        for (var i = 0; i < pathLength; i++)
            if (pathBytes[i] != (byte)'/')
                return false;

        return true;
    }

    private async ValueTask<int> SysMknod(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await SysMknodat(engine, LinuxConstants.AT_FDCWD, a1, a2, a3, 0, 0);
    }

    private async ValueTask<int> SysMknodat(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var dirfd = (int)a1;
        var pathErr = ReadPathArgumentBytes(a2, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        var mode = (int)a3;
        var dev = a4;

        var startAtErr = ResolveStartAt(this, dirfd, path, out var startAt);
        if (startAtErr != 0) return startAtErr;

        var (parentLoc, name, err) = PathWalker.PathWalkForCreate(path.UnsafeBuffer, path.Length, startAt);
        if (err != 0) return err;
        if (parentLoc.Mount != null && parentLoc.Mount.IsReadOnly) return -(int)Errno.EROFS;

        const int S_IFMT = 0xF000;
        const int S_IFIFO = 0x1000;
        const int S_IFCHR = 0x2000;
        const int S_IFBLK = 0x6000;
        const int S_IFREG = 0x8000;
        const int S_IFSOCK = 0xC000;

        var fileType = mode & S_IFMT;
        var dentry = new Dentry(name, null, parentLoc.Dentry, parentLoc.Dentry!.SuperBlock);
        var create = DacPolicy.ComputeCreationMetadata((engine.Owner as FiberTask)?.Process, parentLoc.Dentry.Inode!,
            mode & 0x0FFF, false);

        switch (fileType)
        {
            case S_IFREG:
                return parentLoc.Dentry.Inode!.Create(dentry, create.Mode, create.Uid, create.Gid);
            case S_IFIFO:
                return parentLoc.Dentry.Inode!.Mknod(dentry, create.Mode, create.Uid, create.Gid, InodeType.Fifo, 0);
            case S_IFCHR:
                return parentLoc.Dentry.Inode!.Mknod(dentry, create.Mode, create.Uid, create.Gid, InodeType.CharDev,
                    dev);
            case S_IFBLK:
                return parentLoc.Dentry.Inode!.Mknod(dentry, create.Mode, create.Uid, create.Gid, InodeType.BlockDev,
                    dev);
            case S_IFSOCK:
                return parentLoc.Dentry.Inode!.Mknod(dentry, create.Mode, create.Uid, create.Gid, InodeType.Socket,
                    0);
            default:
                return -(int)Errno.EINVAL;
        }
    }

    private async ValueTask<int> SysSetXAttr(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await SetXAttrPath(this, a1, a2, a3, a4, a5, LookupFlags.FollowSymlink);
    }

    private async ValueTask<int> SysLSetXAttr(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await SetXAttrPath(this, a1, a2, a3, a4, a5, LookupFlags.None);
    }

    private async ValueTask<int> SysFSetXAttr(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fd = (int)a1;
        var nameErr = ReadPathArgumentBytes(a2, out var name, allowEmpty: true);
        if (nameErr != 0) return nameErr;
        using var nameLease = name;
        if (name.IsEmpty) return -(int)Errno.EINVAL;
        if (a4 > int.MaxValue) return -(int)Errno.EINVAL;

        var f = GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;

        var readErr = ReadUserBuffer(engine, a3, (int)a4, out var valueBytes);
        if (readErr != 0) return readErr;
        return f.OpenedInode.SetXAttr(name.Span, valueBytes, (int)a5);
    }

    private async ValueTask<int> SysGetXAttr(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await GetXAttrPath(this, a1, a2, a3, a4, LookupFlags.FollowSymlink);
    }

    private async ValueTask<int> SysLGetXAttr(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await GetXAttrPath(this, a1, a2, a3, a4, LookupFlags.None);
    }

    private async ValueTask<int> SysFGetXAttr(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fd = (int)a1;
        var nameErr = ReadPathArgumentBytes(a2, out var name, allowEmpty: true);
        if (nameErr != 0) return nameErr;
        using var nameLease = name;
        var valueAddr = a3;
        if (a4 > int.MaxValue) return -(int)Errno.EINVAL;
        var size = (int)a4;
        if (name.IsEmpty) return -(int)Errno.EINVAL;

        var f = GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;
        return CopyXAttrToUser(engine, f.OpenedInode, name.Span, valueAddr, size);
    }

    private async ValueTask<int> SysListXAttr(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await ListXAttrPath(this, a1, a2, a3, LookupFlags.FollowSymlink);
    }

    private async ValueTask<int> SysLListXAttr(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        return await ListXAttrPath(this, a1, a2, a3, LookupFlags.None);
    }

    private async ValueTask<int> SysFListXAttr(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var fd = (int)a1;
        var listAddr = a2;
        if (a3 > int.MaxValue) return -(int)Errno.EINVAL;
        var size = (int)a3;

        var f = GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;
        return CopyXAttrListToUser(engine, f.OpenedInode, listAddr, size);
    }

    private async ValueTask<int> SysRemoveXAttr(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        return await RemoveXAttrPath(this, a1, a2, LookupFlags.FollowSymlink);
    }

    private async ValueTask<int> SysLRemoveXAttr(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        return await RemoveXAttrPath(this, a1, a2, LookupFlags.None);
    }

    private async ValueTask<int> SysFRemoveXAttr(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var fd = (int)a1;
        var nameErr = ReadPathArgumentBytes(a2, out var name, allowEmpty: true);
        if (nameErr != 0) return nameErr;
        using var nameLease = name;
        if (name.IsEmpty) return -(int)Errno.EINVAL;

        var f = GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;
        return f.OpenedInode.RemoveXAttr(name.Span);
    }

    private static ValueTask<int> SetXAttrPath(SyscallManager sm, uint pathPtr, uint namePtr, uint valuePtr,
        uint sizeRaw,
        uint flags, LookupFlags lookupFlags)
    {
        var pathErr = sm.ReadPathArgumentBytes(pathPtr, out var path);
        if (pathErr != 0) return new ValueTask<int>(pathErr);
        using var pathLease = path;
        var nameErr = sm.ReadPathArgumentBytes(namePtr, out var name, allowEmpty: true);
        if (nameErr != 0) return new ValueTask<int>(nameErr);
        using var nameLease = name;
        if (name.IsEmpty) return new ValueTask<int>(-(int)Errno.EINVAL);
        if (sizeRaw > int.MaxValue) return new ValueTask<int>(-(int)Errno.EINVAL);

        var loc = sm.PathWalker.PathWalk(path.UnsafeBuffer, path.Length, lookupFlags);
        if (!loc.IsValid || loc.Dentry?.Inode == null) return new ValueTask<int>(-(int)Errno.ENOENT);
        if (loc.Mount != null && loc.Mount.IsReadOnly) return new ValueTask<int>(-(int)Errno.EROFS);

        var readErr = ReadUserBuffer(sm.CurrentSyscallEngine, valuePtr, (int)sizeRaw, out var valueBytes);
        if (readErr != 0) return new ValueTask<int>(readErr);
        return new ValueTask<int>(loc.Dentry.Inode.SetXAttr(name.Span, valueBytes, (int)flags));
    }

    private static ValueTask<int> GetXAttrPath(SyscallManager sm, uint pathPtr, uint namePtr, uint valuePtr,
        uint sizeRaw,
        LookupFlags lookupFlags)
    {
        var pathErr = sm.ReadPathArgumentBytes(pathPtr, out var path);
        if (pathErr != 0) return new ValueTask<int>(pathErr);
        using var pathLease = path;
        var nameErr = sm.ReadPathArgumentBytes(namePtr, out var name, allowEmpty: true);
        if (nameErr != 0) return new ValueTask<int>(nameErr);
        using var nameLease = name;
        if (name.IsEmpty) return new ValueTask<int>(-(int)Errno.EINVAL);
        if (sizeRaw > int.MaxValue) return new ValueTask<int>(-(int)Errno.EINVAL);

        var loc = sm.PathWalker.PathWalk(path.UnsafeBuffer, path.Length, lookupFlags);
        if (!loc.IsValid || loc.Dentry?.Inode == null) return new ValueTask<int>(-(int)Errno.ENOENT);
        return new ValueTask<int>(CopyXAttrToUser(sm.CurrentSyscallEngine, loc.Dentry.Inode, name.Span, valuePtr,
            (int)sizeRaw));
    }

    private static ValueTask<int> ListXAttrPath(SyscallManager sm, uint pathPtr, uint listPtr, uint sizeRaw,
        LookupFlags lookupFlags)
    {
        var pathErr = sm.ReadPathArgumentBytes(pathPtr, out var path);
        if (pathErr != 0) return new ValueTask<int>(pathErr);
        using var pathLease = path;
        if (sizeRaw > int.MaxValue) return new ValueTask<int>(-(int)Errno.EINVAL);

        var loc = sm.PathWalker.PathWalk(path.UnsafeBuffer, path.Length, lookupFlags);
        if (!loc.IsValid || loc.Dentry?.Inode == null) return new ValueTask<int>(-(int)Errno.ENOENT);
        return new ValueTask<int>(CopyXAttrListToUser(sm.CurrentSyscallEngine, loc.Dentry.Inode, listPtr,
            (int)sizeRaw));
    }

    private static ValueTask<int> RemoveXAttrPath(SyscallManager sm, uint pathPtr, uint namePtr,
        LookupFlags lookupFlags)
    {
        var pathErr = sm.ReadPathArgumentBytes(pathPtr, out var path);
        if (pathErr != 0) return new ValueTask<int>(pathErr);
        using var pathLease = path;
        var nameErr = sm.ReadPathArgumentBytes(namePtr, out var name, allowEmpty: true);
        if (nameErr != 0) return new ValueTask<int>(nameErr);
        using var nameLease = name;
        if (name.IsEmpty) return new ValueTask<int>(-(int)Errno.EINVAL);

        var loc = sm.PathWalker.PathWalk(path.UnsafeBuffer, path.Length, lookupFlags);
        if (!loc.IsValid || loc.Dentry?.Inode == null) return new ValueTask<int>(-(int)Errno.ENOENT);
        if (loc.Mount != null && loc.Mount.IsReadOnly) return new ValueTask<int>(-(int)Errno.EROFS);
        return new ValueTask<int>(loc.Dentry.Inode.RemoveXAttr(name.Span));
    }

    private static int CopyXAttrToUser(Engine engine, Inode inode, ReadOnlySpan<byte> name, uint valueAddr, int size)
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
        if (!engine.CopyToUser(valueAddr, buf.AsSpan(0, rc))) return -(int)Errno.EFAULT;
        return rc;
    }

    private static int CopyXAttrListToUser(Engine engine, Inode inode, uint listAddr, int size)
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
        if (!engine.CopyToUser(listAddr, buf.AsSpan(0, rc))) return -(int)Errno.EFAULT;
        return rc;
    }

    private static int ResolveStartAt(SyscallManager sm, int dirfd, RentedUserBytes path, out PathLocation? startAt)
    {
        startAt = null;
        if (path.IsAbsolute) return 0;
        if (dirfd == unchecked((int)LinuxConstants.AT_FDCWD)) return 0;

        var fdir = sm.GetFD(dirfd);
        if (fdir == null) return -(int)Errno.EBADF;
        startAt = fdir.LivePath;
        return 0;
    }

    private static int ReadUserBuffer(Engine engine, uint addr, int size, out byte[] valueBytes)
    {
        valueBytes = [];
        if (size < 0) return -(int)Errno.EINVAL;
        if (size == 0) return 0;
        if (addr == 0) return -(int)Errno.EFAULT;

        valueBytes = new byte[size];
        if (!engine.CopyFromUser(addr, valueBytes)) return -(int)Errno.EFAULT;
        return 0;
    }

    private async ValueTask<int> SysUnlinkAt(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var dirfd = (int)a1;
        var pathErr = ReadPathArgumentBytes(a2, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        var flags = a3;
        if ((flags & ~LinuxConstants.AT_REMOVEDIR) != 0) return -(int)Errno.EINVAL;

        PathLocation startLoc = default;
        if (dirfd != -100 && !path.IsAbsolute)
        {
            var fdir = GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startLoc = fdir.LivePath;
        }

        var (parentLoc, name, err) =
            PathWalker.PathWalkForCreate(path.UnsafeBuffer, path.Length, startLoc.IsValid ? startLoc : null);
        if (err != 0) return err;

        var targetLookup = PathWalker.PathWalkWithData(path.UnsafeBuffer, path.Length,
            startLoc.IsValid ? startLoc : CurrentWorkingDirectory, LookupFlags.None);
        if (targetLookup.HasError) return targetLookup.ErrorCode;
        var targetLoc = targetLookup.Path;
        if (!targetLoc.IsValid || targetLoc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        if ((flags & LinuxConstants.AT_REMOVEDIR) != 0)
        {
            if (IsDotPath(path.Span) || EndsWithSlashDot(path.Span)) return -(int)Errno.EINVAL;
            if (targetLoc.Dentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
            if (!ReferenceEquals(parentLoc.Mount, targetLoc.Mount)) return -(int)Errno.EBUSY;

            var task = engine.Owner as FiberTask;
            var entries = task != null && targetLoc.Dentry.Inode is IContextualDirectoryInode contextualDirectory
                ? contextualDirectory.GetEntries(task)
                : targetLoc.Dentry.Inode.GetEntries();
            if (entries.Count > 2) return -(int)Errno.ENOTEMPTY;
            if (task?.Process != null)
            {
                var stickyRc =
                    DacPolicy.CanRemoveOrRenameEntry(task.Process, parentLoc.Dentry!.Inode!, targetLoc.Dentry.Inode);
                if (stickyRc != 0) return stickyRc;
            }

            var rmdirRc = parentLoc.Dentry!.Inode!.Rmdir(name);
            if (rmdirRc < 0)
                return rmdirRc;
            _ = parentLoc.Dentry.TryUncacheChild(name, "SysUnlinkAt.Rmdir", out _);
            return 0;
        }

        if (targetLoc.Dentry.Inode.Type == InodeType.Directory) return -(int)Errno.EISDIR;
        if (!ReferenceEquals(parentLoc.Mount, targetLoc.Mount)) return -(int)Errno.EBUSY;
        var taskForUnlinkAt = engine.Owner as FiberTask;
        if (taskForUnlinkAt?.Process != null)
        {
            var stickyRc =
                DacPolicy.CanRemoveOrRenameEntry(taskForUnlinkAt.Process, parentLoc.Dentry!.Inode!,
                    targetLoc.Dentry.Inode);
            if (stickyRc != 0) return stickyRc;
        }

        var unlinkRc = parentLoc.Dentry!.Inode!.Unlink(name);
        if (unlinkRc < 0)
            return unlinkRc;
        _ = parentLoc.Dentry.TryUncacheChild(name, "SysUnlinkAt.Unlink", out _);
        return 0;
    }

    private async ValueTask<int> SysGetdents(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fd = (int)a1;
        var bufAddr = a2;
        var count = (int)a3;

        var f = GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;
        if (f.OpenedInode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

        try
        {
            var task = engine.Owner as FiberTask;
            var entries = task != null && f.OpenedInode is IContextualDirectoryInode contextualDirectory
                ? contextualDirectory.GetEntries(task)
                : f.OpenedInode.GetEntries();
            var startIdx = (int)f.Position;
            if (startIdx >= entries.Count) return 0;

            var writeOffset = 0;
            for (var i = startIdx; i < entries.Count; i++)
            {
                var entry = entries[i];
                var nameBytes = entry.Name.Bytes;
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

                nameBytes.CopyTo(buf.AsSpan(10));
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

                if (!engine.CopyToUser(baseAddr, buf))
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

    private async ValueTask<int> SysNewFstatAt(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var dirfd = (int)a1;
        var pathErr = ReadPathArgumentBytes(a2, out var path, allowEmpty: true);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        var statAddr = a3;
        var flags = a4;
        var knownFlags = LinuxConstants.AT_EMPTY_PATH | LinuxConstants.AT_SYMLINK_NOFOLLOW;
        if ((flags & ~knownFlags) != 0) return -(int)Errno.EINVAL;

        if (path.IsEmpty && (flags & 0x1000) != 0) // AT_EMPTY_PATH
            return await SysFstat64(engine, a1, a3, 0, 0, 0, 0);

        PathLocation startLoc = default;
        if (dirfd != -100 && !path.IsAbsolute)
        {
            var fdir = GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startLoc = fdir.LivePath;
        }

        var followLink = (flags & LinuxConstants.AT_SYMLINK_NOFOLLOW) == 0;
        var lookup = PathWalker.PathWalkWithData(
            path.UnsafeBuffer,
            path.Length,
            startLoc.IsValid ? startLoc : CurrentWorkingDirectory,
            followLink ? LookupFlags.FollowSymlink : LookupFlags.None);
        if (lookup.HasError) return lookup.ErrorCode;
        var loc = lookup.Path;
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        RefreshHostfsProjectionForCaller(this, loc.Dentry.Inode);
        WriteStat64(engine, statAddr, loc.Dentry.Inode);
        return 0;
    }

    private async ValueTask<int> SysUtimensAt(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var dirfd = (int)a1;
        var timesAddr = a3;
        var flags = a4;
        var knownFlags = LinuxConstants.AT_EMPTY_PATH | LinuxConstants.AT_SYMLINK_NOFOLLOW;
        if ((flags & ~knownFlags) != 0) return -(int)Errno.EINVAL;

        PathLocation loc;
        if (a2 == 0)
        {
            if (dirfd == unchecked((int)LinuxConstants.AT_FDCWD))
                return -(int)Errno.EFAULT;
            if ((flags & LinuxConstants.AT_SYMLINK_NOFOLLOW) != 0)
                return -(int)Errno.EINVAL;
            var file = GetFD(dirfd);
            if (file == null) return -(int)Errno.EBADF;
            loc = file.LivePath;
        }
        else
        {
            var pathErr = ReadPathArgumentBytes(a2, out var path, allowEmpty: true);
            if (pathErr != 0) return pathErr;
            using var pathLease = path;
            if ((flags & LinuxConstants.AT_EMPTY_PATH) != 0 && path.IsEmpty)
            {
                if (dirfd == unchecked((int)LinuxConstants.AT_FDCWD))
                    loc = CurrentWorkingDirectory;
                else
                {
                    var file = GetFD(dirfd);
                    if (file == null) return -(int)Errno.EBADF;
                    loc = file.LivePath;
                }
                goto LocResolvedUtimens32;
            }

            PathLocation startLoc = default;
            if (dirfd != -100 && !path.IsAbsolute)
            {
                var fdir = GetFD(dirfd);
                if (fdir == null) return -(int)Errno.EBADF;
                startLoc = fdir.LivePath;
            }

            var followLink = (flags & LinuxConstants.AT_SYMLINK_NOFOLLOW) == 0;
            var lookup = PathWalker.PathWalkWithData(
                path.UnsafeBuffer,
                path.Length,
                startLoc.IsValid ? startLoc : CurrentWorkingDirectory,
                followLink ? LookupFlags.FollowSymlink : LookupFlags.None);
            if (lookup.HasError) return lookup.ErrorCode;
            loc = lookup.Path;
        }

LocResolvedUtimens32:
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;
        if (loc.Mount != null && loc.Mount.IsReadOnly) return -(int)Errno.EROFS;

        if (timesAddr == 0)
        {
            var permissionRc = CheckUtimensPermissions(engine, loc, true);
            if (permissionRc < 0) return permissionRc;
            var resolved = ResolveRequestedTimes(loc.Dentry.Inode.ATime, loc.Dentry.Inode.MTime, true);
            return loc.Dentry.Inode.UpdateTimes(resolved.Atime, resolved.Mtime, resolved.Ctime);
        }

        var buf = new byte[16];
        if (!engine.CopyFromUser(timesAddr, buf)) return -(int)Errno.EFAULT;
        var atimeSec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
        var atimeNsec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));
        if (!engine.CopyFromUser(timesAddr + 8, buf.AsSpan(0, 8))) return -(int)Errno.EFAULT;
        var mtimeSec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
        var mtimeNsec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));

        return DoUtimensAtResolveTimes(engine, loc, atimeSec, atimeNsec, mtimeSec, mtimeNsec);
    }

    /// <summary>
    ///     utimensat_time64 - same as utimensat but with 64-bit timespec
    ///     syscall 412
    /// </summary>
    private async ValueTask<int> SysUtimensAtTime64(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var dirfd = (int)a1;
        var timesAddr = a3;
        var flags = a4;
        var knownFlags = LinuxConstants.AT_EMPTY_PATH | LinuxConstants.AT_SYMLINK_NOFOLLOW;
        if ((flags & ~knownFlags) != 0) return -(int)Errno.EINVAL;

        PathLocation loc;
        if (a2 == 0)
        {
            if (dirfd == unchecked((int)LinuxConstants.AT_FDCWD))
                return -(int)Errno.EFAULT;
            if ((flags & LinuxConstants.AT_SYMLINK_NOFOLLOW) != 0)
                return -(int)Errno.EINVAL;
            var file = GetFD(dirfd);
            if (file == null) return -(int)Errno.EBADF;
            loc = file.LivePath;
        }
        else
        {
            var pathErr = ReadPathArgumentBytes(a2, out var path, allowEmpty: true);
            if (pathErr != 0) return pathErr;
            using var pathLease = path;
            if ((flags & LinuxConstants.AT_EMPTY_PATH) != 0 && path.IsEmpty)
            {
                if (dirfd == unchecked((int)LinuxConstants.AT_FDCWD))
                    loc = CurrentWorkingDirectory;
                else
                {
                    var file = GetFD(dirfd);
                    if (file == null) return -(int)Errno.EBADF;
                    loc = file.LivePath;
                }
                goto LocResolvedUtimens64;
            }

            PathLocation startLoc = default;
            if (dirfd != -100 && !path.IsAbsolute)
            {
                var fdir = GetFD(dirfd);
                if (fdir == null) return -(int)Errno.EBADF;
                startLoc = fdir.LivePath;
            }

            var followLink = (flags & LinuxConstants.AT_SYMLINK_NOFOLLOW) == 0;
            var lookup = PathWalker.PathWalkWithData(
                path.UnsafeBuffer,
                path.Length,
                startLoc.IsValid ? startLoc : CurrentWorkingDirectory,
                followLink ? LookupFlags.FollowSymlink : LookupFlags.None);
            if (lookup.HasError) return lookup.ErrorCode;
            loc = lookup.Path;
        }

LocResolvedUtimens64:
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;
        if (loc.Mount != null && loc.Mount.IsReadOnly) return -(int)Errno.EROFS;

        if (timesAddr == 0)
        {
            var permissionRc = CheckUtimensPermissions(engine, loc, true);
            if (permissionRc < 0) return permissionRc;
            var resolved = ResolveRequestedTimes(loc.Dentry.Inode.ATime, loc.Dentry.Inode.MTime, true);
            return loc.Dentry.Inode.UpdateTimes(resolved.Atime, resolved.Mtime, resolved.Ctime);
        }

        // 64-bit timespec: 16 bytes per timespec (8-byte sec + 8-byte nsec)
        var buf = new byte[16];
        if (!engine.CopyFromUser(timesAddr, buf)) return -(int)Errno.EFAULT;
        var atimeSec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0, 8));
        var atimeNsec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8, 8));
        if (!engine.CopyFromUser(timesAddr + 16, buf)) return -(int)Errno.EFAULT;
        var mtimeSec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0, 8));
        var mtimeNsec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8, 8));

        return DoUtimensAtResolveTimes(engine, loc, atimeSec, (int)atimeNsec, mtimeSec, (int)mtimeNsec);
    }

    private int DoUtimensAtResolveTimes(Engine engine, PathLocation loc, long atimeSec, int atimeNsec, long mtimeSec,
        int mtimeNsec)
    {
        var permissionRc = CheckUtimensPermissions(
            engine,
            loc,
            false,
            atimeNsec,
            mtimeNsec);
        if (permissionRc < 0)
            return permissionRc;

        var inode = loc.Dentry!.Inode!;
        var requested = ResolveRequestedTimes(
            inode.ATime,
            inode.MTime,
            false,
            atimeSec,
            atimeNsec,
            mtimeSec,
            mtimeNsec);
        if (requested.Error.HasValue) return requested.Error.Value;
        return inode.UpdateTimes(requested.Atime, requested.Mtime, requested.Ctime);
    }

    private int CheckUtimensPermissions(Engine engine, PathLocation loc, bool useCurrentTimeShortcut,
        int? atimeNsec = null,
        int? mtimeNsec = null)
    {
        var task = engine.Owner as FiberTask;
        var process = task?.Process;
        if (process == null)
            return 0;

        var inode = loc.Dentry!.Inode!;
        var isOwnerOrRoot = process.FSUID == 0 || process.FSUID == inode.Uid;
        if (isOwnerOrRoot)
            return 0;

        var isSetToCurrentTime = useCurrentTimeShortcut ||
                                 (atimeNsec == UtimeNow && mtimeNsec == UtimeNow);
        if (isSetToCurrentTime)
            return DacPolicy.CheckPathAccess(process, inode, AccessMode.MayWrite, true);

        return -(int)Errno.EPERM;
    }

    private async ValueTask<int> SysFchownAt(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var dirfd = (int)a1;
        var pathErr = ReadPathArgumentBytes(a2, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        var flags = a5;
        if ((flags & ~LinuxConstants.AT_SYMLINK_NOFOLLOW) != 0) return -(int)Errno.EINVAL;
        PathLocation startLoc = default;
        if (dirfd != -100 && !path.IsAbsolute)
        {
            var fdir = GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startLoc = fdir.LivePath;
        }

        var followLink = (flags & LinuxConstants.AT_SYMLINK_NOFOLLOW) == 0;
        var lookup = PathWalker.PathWalkWithData(
            path.UnsafeBuffer,
            path.Length,
            startLoc.IsValid ? startLoc : CurrentWorkingDirectory,
            followLink ? LookupFlags.FollowSymlink : LookupFlags.None);
        if (lookup.HasError) return lookup.ErrorCode;
        var loc = lookup.Path;
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        var uid = (int)a3;
        var gid = (int)a4;
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        RefreshHostfsProjectionForCaller(this, loc.Dentry.Inode);
        var allowed = DacPolicy.CanChown(task.Process, loc.Dentry.Inode, uid, gid);
        if (allowed != 0) return allowed;
        return ApplyOwnershipChange(loc.Dentry.Inode, uid, gid);
    }

    private async ValueTask<int> SysFchmodAt(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var dirfd = (int)a1;
        var pathErr = ReadPathArgumentBytes(a2, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        var flags = a4;
        if ((flags & ~LinuxConstants.AT_SYMLINK_NOFOLLOW) != 0) return -(int)Errno.EINVAL;
        PathLocation startLoc = default;
        if (dirfd != -100 && !path.IsAbsolute)
        {
            var fdir = GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startLoc = fdir.LivePath;
        }

        var followLink = (flags & LinuxConstants.AT_SYMLINK_NOFOLLOW) == 0;
        var lookup = PathWalker.PathWalkWithData(
            path.UnsafeBuffer,
            path.Length,
            startLoc.IsValid ? startLoc : CurrentWorkingDirectory,
            followLink ? LookupFlags.FollowSymlink : LookupFlags.None);
        if (lookup.HasError) return lookup.ErrorCode;
        var loc = lookup.Path;
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        var mode = a3;
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        RefreshHostfsProjectionForCaller(this, loc.Dentry.Inode);
        var allowed = DacPolicy.CanChmod(task.Process, loc.Dentry.Inode);
        if (allowed != 0) return allowed;
        return ApplyModeChange(loc.Dentry.Inode, (int)mode, task.Process);
    }

    private async ValueTask<int> SysFaccessAt(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var dirfd = (int)a1;
        var pathErr = ReadPathArgumentBytes(a2, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        PathLocation startLoc = default;
        if (dirfd != -100 && !path.IsAbsolute)
        {
            var fdir = GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startLoc = fdir.LivePath;
        }

        const uint AT_EACCESS = 0x200;
        var knownFlags = AT_EACCESS | LinuxConstants.AT_SYMLINK_NOFOLLOW;
        if ((a4 & ~knownFlags) != 0) return -(int)Errno.EINVAL;
        var followLink = (a4 & LinuxConstants.AT_SYMLINK_NOFOLLOW) == 0;
        var lookup = PathWalker.PathWalkWithData(
            path.UnsafeBuffer,
            path.Length,
            startLoc.IsValid ? startLoc : CurrentWorkingDirectory,
            followLink ? LookupFlags.FollowSymlink : LookupFlags.None);
        if (lookup.HasError) return lookup.ErrorCode;
        var loc = lookup.Path;
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        var mode = (int)a3;
        if ((mode & ~7) != 0) return -(int)Errno.EINVAL;
        if (mode == 0) return 0; // F_OK

        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        RefreshHostfsProjectionForCaller(this, loc.Dentry.Inode);

        var req = AccessMode.None;
        if ((mode & 4) != 0) req |= AccessMode.MayRead;
        if ((mode & 2) != 0) req |= AccessMode.MayWrite;
        if ((mode & 1) != 0) req |= AccessMode.MayExec;
        var useEffectiveIds = (a4 & AT_EACCESS) != 0;
        return DacPolicy.CheckPathAccess(task.Process, loc.Dentry.Inode, req, useEffectiveIds);
    }

    private async ValueTask<int> SysFaccessAt2(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        return await SysFaccessAt(engine, a1, a2, a3, a4, a5, a6);
    }

    private async ValueTask<int> SysFchmodAt2(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        // Support the Linux fchmodat2 flag subset currently exercised by tar.
        if ((a4 & ~LinuxConstants.AT_SYMLINK_NOFOLLOW) != 0) return -(int)Errno.EINVAL;
        return await SysFchmodAt(engine, a1, a2, a3, a4, a5, a6);
    }

    private async ValueTask<int> SysRename(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var oldPathErr = ReadPathArgumentBytes(a1, out var oldPath);
        if (oldPathErr != 0) return oldPathErr;
        using var oldPathLease = oldPath;
        var newPathErr = ReadPathArgumentBytes(a2, out var newPath);
        if (newPathErr != 0) return newPathErr;
        using var newPathLease = newPath;

        return ImplRename(this, -100, oldPath, -100, newPath, 0);
    }

    private async ValueTask<int> SysRenameAt(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var oldPathErr = ReadPathArgumentBytes(a2, out var oldPath);
        if (oldPathErr != 0) return oldPathErr;
        using var oldPathLease = oldPath;
        var newPathErr = ReadPathArgumentBytes(a4, out var newPath);
        if (newPathErr != 0) return newPathErr;
        using var newPathLease = newPath;
        LogRenameAt(oldPath.Span, (int)a1, newPath.Span, (int)a3);
        return ImplRename(this, (int)a1, oldPath, (int)a3, newPath, 0);
    }

    private async ValueTask<int> SysRenameAt2(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var oldPathErr = ReadPathArgumentBytes(a2, out var oldPath);
        if (oldPathErr != 0) return oldPathErr;
        using var oldPathLease = oldPath;
        var newPathErr = ReadPathArgumentBytes(a4, out var newPath);
        if (newPathErr != 0) return newPathErr;
        using var newPathLease = newPath;
        LogRenameAt2(oldPath.Span, (int)a1, newPath.Span, (int)a3, a5);
        return ImplRename(this, (int)a1, oldPath, (int)a3, newPath, a5);
    }

    private static int ImplRename(SyscallManager sm, int oldDirFd, RentedUserBytes oldPath, int newDirFd,
        RentedUserBytes newPath,
        uint flags)
    {
        // TODO: add full renameat2 semantics for RENAME_EXCHANGE and RENAME_WHITEOUT.
        var supportedFlags = LinuxConstants.RENAME_NOREPLACE | LinuxConstants.RENAME_EXCHANGE;
        if ((flags & ~supportedFlags) != 0)
            return -(int)Errno.EINVAL;
        if ((flags & LinuxConstants.RENAME_NOREPLACE) != 0 && (flags & LinuxConstants.RENAME_EXCHANGE) != 0)
            return -(int)Errno.EINVAL;

        PathLocation? oldStart = null;
        if (oldDirFd != -100 && !oldPath.IsAbsolute)
        {
            var f = sm.GetFD(oldDirFd);
            if (f == null) return -(int)Errno.EBADF;
            oldStart = f.LivePath;
        }

        PathLocation? newStart = null;
        if (newDirFd != -100 && !newPath.IsAbsolute)
        {
            var f = sm.GetFD(newDirFd);
            if (f == null) return -(int)Errno.EBADF;
            newStart = f.LivePath;
        }

        var (oldParentLoc, oldName, oldErr) = sm.PathWalker.PathWalkForCreate(oldPath.UnsafeBuffer, oldPath.Length, oldStart);
        if (oldErr != 0)
        {
            LogRenamePathWalkFailure("oldPath", oldErr);
            return oldErr;
        }

        var (newParentLoc, newName, newErr) = sm.PathWalker.PathWalkForCreate(newPath.UnsafeBuffer, newPath.Length, newStart);
        if (newErr != 0)
        {
            LogRenamePathWalkFailure("newPath", newErr);
            return newErr;
        }

        if (oldName.IsDotOrDotDot || newName.IsDotOrDotDot)
            return -(int)Errno.EINVAL;

        var oldLoc = oldStart.HasValue
            ? sm.PathWalker.PathWalk(oldPath.UnsafeBuffer, oldPath.Length, oldStart.Value, LookupFlags.None)
            : sm.PathWalker.PathWalk(oldPath.UnsafeBuffer, oldPath.Length, LookupFlags.None);
        if (!oldLoc.IsValid || oldLoc.Dentry?.Inode == null)
            return -(int)Errno.ENOENT;

        if (!ReferenceEquals(oldParentLoc.Mount, newParentLoc.Mount))
            return -(int)Errno.EXDEV;
        if (!ReferenceEquals(oldParentLoc.Mount, oldLoc.Mount))
            return -(int)Errno.EBUSY;

        var targetLoc = newStart.HasValue
            ? sm.PathWalker.PathWalk(newPath.UnsafeBuffer, newPath.Length, newStart.Value, LookupFlags.None)
            : sm.PathWalker.PathWalk(newPath.UnsafeBuffer, newPath.Length, LookupFlags.None);
        var targetExists = targetLoc.IsValid && targetLoc.Dentry?.Inode != null;
        var replacedTargetInode = targetExists ? targetLoc.Dentry!.Inode : null;
        if (targetExists && !ReferenceEquals(newParentLoc.Mount, targetLoc.Mount))
            return -(int)Errno.EBUSY;
        if (targetExists &&
            ReferenceEquals(oldLoc.Mount, targetLoc.Mount) &&
            (ReferenceEquals(oldLoc.Dentry, targetLoc.Dentry) ||
             ReferenceEquals(oldLoc.Dentry!.Inode, targetLoc.Dentry!.Inode)))
            return 0;

        var sourceInode = oldLoc.Dentry!.Inode!;
        var sourceIsDirectory = sourceInode.Type == InodeType.Directory;
        var movedAcrossParents = !ReferenceEquals(oldParentLoc.Dentry!.Inode, newParentLoc.Dentry!.Inode);
        if (sourceIsDirectory && IsRenameTargetWithinSource(sourceInode, newParentLoc.Dentry))
            return -(int)Errno.EINVAL;

        if (sm.CurrentTask?.Process != null)
        {
            var stickyRc =
                DacPolicy.CanRemoveOrRenameEntry(sm.CurrentTask.Process, oldParentLoc.Dentry!.Inode!,
                    sourceInode);
            if (stickyRc != 0) return stickyRc;
            if (targetExists)
            {
                stickyRc = DacPolicy.CanRemoveOrRenameEntry(sm.CurrentTask.Process, newParentLoc.Dentry!.Inode!,
                    targetLoc.Dentry!.Inode!);
                if (stickyRc != 0) return stickyRc;
            }

            const int S_ISVTX = 0x200;
            if (sourceIsDirectory &&
                movedAcrossParents &&
                (oldParentLoc.Dentry.Inode!.Mode & S_ISVTX) != 0 &&
                sm.CurrentTask.Process.FSUID != 0 &&
                sm.CurrentTask.Process.FSUID != sourceInode.Uid)
                return -(int)Errno.EPERM;
        }

        if ((flags & LinuxConstants.RENAME_NOREPLACE) != 0)
            if (targetExists)
                return -(int)Errno.EEXIST;

        if ((flags & LinuxConstants.RENAME_EXCHANGE) != 0)
        {
            if (!targetExists)
                return -(int)Errno.ENOENT;

            var tempName = FsName.FromString($".rename-exchange-{Guid.NewGuid():N}");

            var rc = oldParentLoc.Dentry!.Inode!.Rename(oldName.Bytes, oldParentLoc.Dentry.Inode, tempName.Bytes);
            if (rc < 0)
                return rc;

            rc = newParentLoc.Dentry!.Inode!.Rename(newName.Bytes, oldParentLoc.Dentry.Inode!, oldName.Bytes);
            if (rc < 0)
            {
                _ = oldParentLoc.Dentry.Inode!.Rename(tempName.Bytes, oldParentLoc.Dentry.Inode, oldName.Bytes);
                return rc;
            }

            rc = oldParentLoc.Dentry.Inode!.Rename(tempName.Bytes, newParentLoc.Dentry.Inode!, newName.Bytes);
            if (rc < 0)
            {
                _ = oldParentLoc.Dentry.Inode!.Rename(oldName.Bytes, newParentLoc.Dentry.Inode!, newName.Bytes);
                _ = oldParentLoc.Dentry.Inode!.Rename(tempName.Bytes, oldParentLoc.Dentry.Inode, oldName.Bytes);
                return rc;
            }

            foreach (var pDentry in oldParentLoc.Dentry.Inode!.Dentries.ToList())
            {
                _ = pDentry.TryUncacheChild(oldName.Bytes, "SysRename.exchange.cleanup-old", out _);
                _ = pDentry.TryUncacheChild(tempName.Bytes, "SysRename.exchange.cleanup-temp", out _);
            }

            foreach (var pDentry in newParentLoc.Dentry!.Inode!.Dentries.ToList())
                _ = pDentry.TryUncacheChild(newName.Bytes, "SysRename.exchange.cleanup-new", out _);

            if (!ReferenceEquals(oldParentLoc.Dentry.Inode, newParentLoc.Dentry.Inode))
            {
                foreach (var pDentry in oldParentLoc.Dentry.Inode.Dentries.ToList())
                    _ = pDentry.TryUncacheChild(newName.Bytes, "SysRename.exchange.cleanup-old-new", out _);
                foreach (var pDentry in newParentLoc.Dentry.Inode.Dentries.ToList())
                    _ = pDentry.TryUncacheChild(oldName.Bytes, "SysRename.exchange.cleanup-new-old", out _);
            }

            return 0;
        }

        var renameRc = oldParentLoc.Dentry!.Inode!.Rename(oldName.Bytes, newParentLoc.Dentry!.Inode!, newName.Bytes);
        if (renameRc < 0)
            return renameRc;

        // Update the VFS wrapper dentry cache across all views of the parent inodes.
        // This is critical because multiple Dentries might exist for the same Inode
        // (e.g., due to different path resolutions or OverlayFS wrapping).
        var oldParentInode = oldParentLoc.Dentry?.Inode;
        var newParentInode = newParentLoc.Dentry?.Inode;

        if (oldParentInode != null)
            foreach (var pDentry in oldParentInode.Dentries.ToList())
                _ = pDentry.TryUncacheChild(oldName.Bytes, "SysRename.cleanup-old", out _);

        if (newParentInode != null)
            foreach (var pDentry in newParentInode.Dentries.ToList())
                // Clean up only the pre-existing target dentry that got replaced.
                // Do not tear down the freshly moved source dentry.
                if (replacedTargetInode != null &&
                    pDentry.TryGetCachedChild(newName.Bytes, out var victimDentry) &&
                    ReferenceEquals(victimDentry.Inode, replacedTargetInode))
                {
                    if (victimDentry.Inode != null) victimDentry.UnbindInode("SysRename.cleanup-replaced-target");

                    _ = pDentry.TryUncacheChild(newName.Bytes, "SysRename.cleanup-replaced-target", out _);
                }

        // Note: We don't necessarily need to move the dentry object here; 
        // the next Lookup will create a fresh Dentry pointing to the correct Inode.
        // This ensures maximum correctness across all possible "views" of the FS.

        return 0;
    }

    private static bool IsRenameTargetWithinSource(Inode sourceInode, Dentry targetParent)
    {
        for (var current = targetParent; current != null; current = current.Parent)
        {
            if (ReferenceEquals(current.Inode, sourceInode))
                return true;
            if (ReferenceEquals(current.Parent, current))
                break;
        }

        return false;
    }

    private async ValueTask<int> SysStat(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var pathErr = ReadPathArgumentBytes(a1, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        var lookup = PathWalker.PathWalkWithData(path.UnsafeBuffer, path.Length, null, LookupFlags.FollowSymlink);
        if (lookup.HasError) return lookup.ErrorCode;
        var loc = lookup.Path;
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        RefreshHostfsProjectionForCaller(this, loc.Dentry.Inode);
        WriteStat(engine, a2, loc.Dentry.Inode);
        return 0;
    }

    private async ValueTask<int> SysLstat(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var pathErr = ReadPathArgumentBytes(a1, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        var lookup = PathWalker.PathWalkWithData(path.UnsafeBuffer, path.Length, null, LookupFlags.None);
        if (lookup.HasError) return lookup.ErrorCode;
        var loc = lookup.Path;
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        RefreshHostfsProjectionForCaller(this, loc.Dentry.Inode);
        WriteStat(engine, a2, loc.Dentry.Inode);
        return 0;
    }

    private async ValueTask<int> SysFstat(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fd = (int)a1;
        var f = GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;

        RefreshHostfsProjectionForCaller(this, f.OpenedInode);
        WriteStat(engine, a2, f.OpenedInode);
        return 0;
    }

    private ValueTask<int> SysStatx(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var dirfd = (int)a1;
        var flags = a3;
        var mask = a4;
        var statxAddr = a5;

        var knownFlags = LinuxConstants.AT_EMPTY_PATH |
                         LinuxConstants.AT_SYMLINK_NOFOLLOW |
                         LinuxConstants.AT_NO_AUTOMOUNT |
                         LinuxConstants.AT_STATX_SYNC_TYPE;
        if ((flags & ~knownFlags) != 0) return ValueTask.FromResult(-(int)Errno.EINVAL);

        var syncFlags = flags & LinuxConstants.AT_STATX_SYNC_TYPE;
        if (syncFlags != LinuxConstants.AT_STATX_SYNC_AS_STAT &&
            syncFlags != LinuxConstants.AT_STATX_FORCE_SYNC &&
            syncFlags != LinuxConstants.AT_STATX_DONT_SYNC)
            return ValueTask.FromResult(-(int)Errno.EINVAL);

        if (a2 == 0)
        {
            if ((flags & LinuxConstants.AT_EMPTY_PATH) == 0)
                return ValueTask.FromResult(-(int)Errno.EFAULT);
            if (dirfd == unchecked((int)LinuxConstants.AT_FDCWD))
            {
                if (!WriteStatx(engine, statxAddr, CurrentWorkingDirectory.Dentry!.Inode!, mask,
                        CurrentWorkingDirectory.Mount))
                    return ValueTask.FromResult(-(int)Errno.EFAULT);

                return ValueTask.FromResult(0);
            }

            var f = GetFD(dirfd);
            if (f == null || f.OpenedInode == null) return ValueTask.FromResult(-(int)Errno.EBADF);
            RefreshHostfsProjectionForCaller(this, f.OpenedInode);

            if (!WriteStatx(engine, statxAddr, f.OpenedInode, mask, f.Mount))
                return ValueTask.FromResult(-(int)Errno.EFAULT);

            return ValueTask.FromResult(0);
        }

        var pathErr = ReadPathArgumentBytes(a2, out var path, allowEmpty: true);
        if (pathErr != 0) return ValueTask.FromResult(pathErr);
        using var pathLease = path;

        if (path.IsEmpty && (flags & LinuxConstants.AT_EMPTY_PATH) != 0)
        {
            if (dirfd == unchecked((int)LinuxConstants.AT_FDCWD))
            {
                if (!WriteStatx(engine, statxAddr, CurrentWorkingDirectory.Dentry!.Inode!, mask,
                        CurrentWorkingDirectory.Mount))
                    return ValueTask.FromResult(-(int)Errno.EFAULT);

                return ValueTask.FromResult(0);
            }

            var f = GetFD(dirfd);
            if (f == null || f.OpenedInode == null) return ValueTask.FromResult(-(int)Errno.EBADF);
            RefreshHostfsProjectionForCaller(this, f.OpenedInode);

            if (!WriteStatx(engine, statxAddr, f.OpenedInode, mask, f.Mount))
                return ValueTask.FromResult(-(int)Errno.EFAULT);

            return ValueTask.FromResult(0);
        }

        if (path.IsEmpty)
            return ValueTask.FromResult(-(int)Errno.ENOENT);

        PathLocation startLoc = default;
        if (dirfd != unchecked((int)LinuxConstants.AT_FDCWD) && !path.IsAbsolute)
        {
            var fdir = GetFD(dirfd);
            if (fdir == null) return ValueTask.FromResult(-(int)Errno.EBADF);
            startLoc = fdir.LivePath;
        }

        var followLink = (flags & LinuxConstants.AT_SYMLINK_NOFOLLOW) == 0;
        var lookup = PathWalker.PathWalkWithData(
            path.UnsafeBuffer,
            path.Length,
            startLoc.IsValid ? startLoc : CurrentWorkingDirectory,
            followLink ? LookupFlags.FollowSymlink : LookupFlags.None);
        if (lookup.HasError) return ValueTask.FromResult(lookup.ErrorCode);
        var loc = lookup.Path;
        if (!loc.IsValid || loc.Dentry!.Inode == null) return ValueTask.FromResult(-(int)Errno.ENOENT);

        RefreshHostfsProjectionForCaller(this, loc.Dentry.Inode);

        // We don't model automount transitions or remote-filesystem sync policy yet.
        // Linux treats these as lookup/stat hints, so accept them and use local cached state.

        if (!WriteStatx(engine, statxAddr, loc.Dentry.Inode, mask, loc.Mount))
            return ValueTask.FromResult(-(int)Errno.EFAULT);

        return ValueTask.FromResult(0);
    }

    private async ValueTask<int> SysChmod(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var pathErr = ReadPathArgumentBytes(a1, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        var mode = a2;

        var lookup = PathWalker.PathWalkWithData(path.UnsafeBuffer, path.Length, null, LookupFlags.FollowSymlink);
        if (lookup.HasError) return lookup.ErrorCode;
        var loc = lookup.Path;
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        RefreshHostfsProjectionForCaller(this, loc.Dentry.Inode);
        var t = engine.Owner as FiberTask;
        if (t == null) return -(int)Errno.EPERM;
        var allowed = DacPolicy.CanChmod(t.Process, loc.Dentry.Inode);
        if (allowed != 0) return allowed;

        return ApplyModeChange(loc.Dentry.Inode, (int)mode, t.Process);
    }

    private async ValueTask<int> SysFchmod(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fd = (int)a1;
        var mode = a2;

        var f = GetFD(fd);
        if (f == null || f.OpenedInode == null) return -9; // EBADF

        RefreshHostfsProjectionForCaller(this, f.OpenedInode);
        var t = engine.Owner as FiberTask;
        if (t == null) return -(int)Errno.EPERM;
        var allowed = DacPolicy.CanChmod(t.Process, f.OpenedInode);
        if (allowed != 0) return allowed;

        return ApplyModeChange(f.OpenedInode, (int)mode, t.Process);
    }

    private async ValueTask<int> SysChown(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var pathErr = ReadPathArgumentBytes(a1, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        var uid = (int)a2;
        var gid = (int)a3;

        var lookup = PathWalker.PathWalkWithData(path.UnsafeBuffer, path.Length, null, LookupFlags.FollowSymlink);
        if (lookup.HasError) return lookup.ErrorCode;
        var loc = lookup.Path;
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        RefreshHostfsProjectionForCaller(this, loc.Dentry.Inode);
        var t = engine.Owner as FiberTask;
        if (t == null) return -(int)Errno.EPERM;
        var allowed = DacPolicy.CanChown(t.Process, loc.Dentry.Inode, uid, gid);
        if (allowed != 0) return allowed;

        return ApplyOwnershipChange(loc.Dentry.Inode, uid, gid);
    }

    private async ValueTask<int> SysFchown(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fd = (int)a1;
        var uid = (int)a2;
        var gid = (int)a3;

        var f = GetFD(fd);
        if (f == null || f.OpenedInode == null) return -9; // EBADF

        RefreshHostfsProjectionForCaller(this, f.OpenedInode);
        var t = engine.Owner as FiberTask;
        if (t == null) return -(int)Errno.EPERM;
        var allowed = DacPolicy.CanChown(t.Process, f.OpenedInode, uid, gid);
        if (allowed != 0) return allowed;

        return ApplyOwnershipChange(f.OpenedInode, uid, gid);
    }

    private async ValueTask<int> SysLchown(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var pathErr = ReadPathArgumentBytes(a1, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        var uid = (int)a2;
        var gid = (int)a3;

        var lookup = PathWalker.PathWalkWithData(path.UnsafeBuffer, path.Length, null, LookupFlags.None);
        if (lookup.HasError) return lookup.ErrorCode;
        var loc = lookup.Path;
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        RefreshHostfsProjectionForCaller(this, loc.Dentry.Inode);
        var t = engine.Owner as FiberTask;
        if (t == null) return -(int)Errno.EPERM;
        var allowed = DacPolicy.CanChown(t.Process, loc.Dentry.Inode, uid, gid);
        if (allowed != 0) return allowed;

        return ApplyOwnershipChange(loc.Dentry.Inode, uid, gid);
    }

    private async ValueTask<int> SysGetCwd(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var bufAddr = a1;
        var size = a2;

        var cwd = GetAbsolutePathBytes(CurrentWorkingDirectory);
        if (cwd.Length + 1 > size) return -(int)Errno.ERANGE;

        var buf = new byte[cwd.Length + 1];
        cwd.CopyTo(buf, 0);
        if (!engine.CopyToUser(bufAddr, buf)) return -(int)Errno.EFAULT;
        return buf.Length;
    }

    private async ValueTask<int> SysSync(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        SyncContainerPageCache();
        return 0;
    }

    private async ValueTask<int> SysFsync(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var file = GetFD((int)a1);
        if (file == null) return -(int)Errno.EBADF;
        var inode = file.OpenedInode;
        if (inode == null) return -(int)Errno.EBADF;
        var task = engine.Owner as FiberTask;

        BlockingHostOperationDebug.Trace(
            $"SysFsync begin fd={(int)a1} inode={inode.Ino} tid={task?.TID} thread={Environment.CurrentManagedThreadId}");
        ProcessAddressSpaceSync.SyncMappedFile(Mem, engine, file);
        var writebackRc = inode.WritePages(file, new WritePagesRequest(0, long.MaxValue,
            PageWritebackMode.WritebackOnly));
        BlockingHostOperationDebug.Trace(
            $"SysFsync after-writeback fd={(int)a1} inode={inode.Ino} tid={task?.TID} rc={writebackRc}");
        if (writebackRc < 0 && writebackRc != -(int)Errno.EOPNOTSUPP && writebackRc != -(int)Errno.EROFS)
            return writebackRc;
        var durableRc = await inode.FlushWritebackToDurableAsync(file, task);
        BlockingHostOperationDebug.Trace(
            $"SysFsync after-durable fd={(int)a1} inode={inode.Ino} tid={task?.TID} rc={durableRc}");
        if (durableRc < 0 && durableRc != -(int)Errno.EOPNOTSUPP && durableRc != -(int)Errno.EROFS)
            return durableRc;
        return 0;
    }

    private async ValueTask<int> SysFdatasync(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await SysFsync(engine, a1, a2, a3, a4, a5, a6);
    }

    private async ValueTask<int> SysUnlink(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var pathErr = ReadPathArgumentBytes(a1, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;

        var (parentLoc, name, err) = PathWalker.PathWalkForCreate(path.UnsafeBuffer, path.Length);
        if (err != 0) return err;

        var targetLookup = PathWalker.PathWalkWithData(path.UnsafeBuffer, path.Length, null, LookupFlags.None);
        if (targetLookup.HasError) return targetLookup.ErrorCode;
        var targetLoc = targetLookup.Path;
        if (!targetLoc.IsValid || targetLoc.Dentry!.Inode == null) return -(int)Errno.ENOENT;
        if (targetLoc.Dentry.Inode.Type == InodeType.Directory) return -(int)Errno.EISDIR;
        if (!ReferenceEquals(parentLoc.Mount, targetLoc.Mount)) return -(int)Errno.EBUSY;
        var task = engine.Owner as FiberTask;
        if (task?.Process != null)
        {
            var stickyRc =
                DacPolicy.CanRemoveOrRenameEntry(task.Process, parentLoc.Dentry!.Inode!, targetLoc.Dentry.Inode);
            if (stickyRc != 0) return stickyRc;
        }

        var unlinkRc2 = parentLoc.Dentry!.Inode!.Unlink(name);
        if (unlinkRc2 < 0)
            return unlinkRc2;
        _ = parentLoc.Dentry.TryUncacheChild(name, "SysUnlink", out _);
        return 0;
    }

    private async ValueTask<int> SysAccess(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var pathErr = ReadPathArgumentBytes(a1, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        var mode = (int)a2;
        if ((mode & ~7) != 0) return -(int)Errno.EINVAL;
        var lookup = PathWalker.PathWalkWithData(path.UnsafeBuffer, path.Length, null, LookupFlags.FollowSymlink);
        if (lookup.HasError) return lookup.ErrorCode;
        var loc = lookup.Path;
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;
        if (mode == 0) return 0; // F_OK

        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        RefreshHostfsProjectionForCaller(this, loc.Dentry.Inode);

        var req = AccessMode.None;
        if ((mode & 4) != 0) req |= AccessMode.MayRead;
        if ((mode & 2) != 0) req |= AccessMode.MayWrite;
        if ((mode & 1) != 0) req |= AccessMode.MayExec;
        return DacPolicy.CheckPathAccess(task.Process, loc.Dentry.Inode, req, false);
    }

    private ValueTask<int> SysGetdents64(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var fd = (int)a1;
        var bufAddr = a2;
        var count = (int)a3;

        var f = GetFD(fd);
        if (f == null || f.OpenedInode == null) return ValueTask.FromResult(-(int)Errno.EBADF);

        try
        {
            var task = engine.Owner as FiberTask;
            var entries = task != null && f.OpenedInode is IContextualDirectoryInode contextualDirectory
                ? contextualDirectory.GetEntries(task)
                : f.OpenedInode.GetEntries();

            // Simplified logic: uses f.Position as index in entries list
            var startIdx = (int)f.Position;
            if (startIdx >= entries.Count) return ValueTask.FromResult(0);

            var writeOffset = 0;
            for (var i = startIdx; i < entries.Count; i++)
            {
                var entry = entries[i];
                var nameBytes = entry.Name.Bytes;
                var nameLen = nameBytes.Length + 1;
                var recLen = (8 + 8 + 2 + 1 + nameLen + 7) & ~7;

                if (writeOffset + recLen > count) break;

                if (!WriteDirentRecord(engine, bufAddr + (uint)writeOffset, entry, recLen, writeOffset + recLen))
                    return ValueTask.FromResult(-(int)Errno.EFAULT);

                writeOffset += recLen;
                f.Position = i + 1;
            }

            return ValueTask.FromResult(writeOffset);
        }
        catch
        {
            return ValueTask.FromResult(-(int)Errno.EPERM);
        }
    }

    private static bool WriteDirentRecord(Engine engine, uint baseAddr, DirectoryEntry entry, int recLen, long nextOffset)
    {
        byte[]? rented = null;
        try
        {
            Span<byte> buf = recLen <= StackDirentBufferLimit
                ? stackalloc byte[recLen]
                : (rented = ArrayPool<byte>.Shared.Rent(recLen)).AsSpan(0, recLen);
            buf.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(0, 8), entry.Ino);
            BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(8, 8), nextOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(16, 2), (ushort)recLen);

            byte dType = 8; // DT_REG
            if (entry.Type == InodeType.Directory) dType = 4;
            buf[18] = dType;

            var nameBytes = entry.Name.Bytes;
            nameBytes.CopyTo(buf.Slice(19, nameBytes.Length));
            buf[19 + nameBytes.Length] = 0;

            return engine.CopyToUser(baseAddr, buf.Slice(0, recLen));
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    private static void RefreshHostfsProjectionForCaller(SyscallManager sm, Inode inode)
    {
        if (inode is not HostInode hostInode) return;
        var task = sm.CurrentTask;
        var uid = task?.Process.EUID ?? 0;
        var gid = task?.Process.EGID ?? 0;
        hostInode.RefreshProjectedMetadata(uid, gid);
    }

    private static int ApplyOwnershipChange(Inode inode, int uid, int gid)
    {
        var target = ResolveMetadataMutationTarget(inode, out var resolveRc);
        if (resolveRc != 0)
            return resolveRc;

        var oldUid = target.Uid;
        var oldGid = target.Gid;
        var newUid = uid == -1 ? oldUid : uid;
        var newGid = gid == -1 ? oldGid : gid;

        if (target is HostInode hostInode)
        {
            var rc = hostInode.SetProjectedOwnership(uid, gid);
            if (rc != 0) return rc;
            target.Mode = DacPolicy.ApplySetIdClearOnChown(target, oldUid, oldGid, newUid, newGid);
            return 0;
        }

        target.Uid = newUid;
        target.Gid = newGid;
        target.Mode = DacPolicy.ApplySetIdClearOnChown(target, oldUid, oldGid, newUid, newGid);
        target.CTime = DateTime.Now;
        PersistMetadataMutationIfNeeded(target);
        return 0;
    }

    private static int ApplyModeChange(Inode inode, int mode, Process? process = null)
    {
        var target = ResolveMetadataMutationTarget(inode, out var resolveRc);
        if (resolveRc != 0)
            return resolveRc;

        var normalizedMode = process == null ? mode & 0xFFF : DacPolicy.NormalizeChmodMode(process, target, mode);
        if (target is HostInode hostInode) return hostInode.SetProjectedMode(normalizedMode);

        target.Mode = normalizedMode;
        target.CTime = DateTime.Now;
        PersistMetadataMutationIfNeeded(target);
        return 0;
    }

    private static Inode ResolveMetadataMutationTarget(Inode inode, out int rc)
    {
        if (inode is OverlayInode overlayInode)
        {
            rc = overlayInode.ResolveMetadataMutationTarget(out var target);
            return target!;
        }

        rc = 0;
        return inode;
    }

    private static void PersistMetadataMutationIfNeeded(Inode inode)
    {
        if (inode is SilkInode silkInode)
            silkInode.PersistMetadataImmediately();
    }

    private static void WriteStat64(Engine engine, uint addr, Inode inode)
    {
        Span<byte> buf = stackalloc byte[96];
        buf.Clear();

        var mode = (uint)inode.Mode | (uint)inode.Type;
        var size = (long)inode.Size;
        var uid = (uint)inode.Uid;
        var gid = (uint)inode.Gid;

        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(0), inode.Dev);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(12), (uint)inode.Ino); // __st_ino
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(16), mode);
        var nlink = inode.GetDebugNlinkForStat("WriteStat64", inode.GetLinkCountForStat());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(20), nlink);

        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(24), uid);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(28), gid);

        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(32), inode.Rdev); // st_rdev
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(44), (ulong)size);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(52), 4096);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(56), (ulong)((size + 511) / 512));

        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(64),
            (uint)new DateTimeOffset(inode.ATime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(72),
            (uint)new DateTimeOffset(inode.MTime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(80),
            (uint)new DateTimeOffset(inode.CTime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(88), inode.Ino);

        if (!engine.CopyToUser(addr, buf)) return;
    }

    private static void WriteStat(Engine engine, uint addr, Inode inode)
    {
        Span<byte> buf = stackalloc byte[64];
        buf.Clear();

        var mode = (uint)inode.Mode | (uint)inode.Type;
        var size = (long)inode.Size;
        var uid = (uint)inode.Uid;
        var gid = (uint)inode.Gid;

        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(0), (ushort)inode.Dev); // st_dev
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(4), (uint)inode.Ino);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(8), (ushort)mode);
        var nlink = inode.GetDebugNlinkForStat("WriteStat", inode.GetLinkCountForStat());
        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(10), (ushort)nlink);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(12), (ushort)uid);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(14), (ushort)gid);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(16), (ushort)inode.Rdev); // st_rdev
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(20), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(24), 4096); // blksize
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(28), (uint)((size + 511) / 512));

        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(32),
            (uint)new DateTimeOffset(inode.ATime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(40),
            (uint)new DateTimeOffset(inode.MTime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(48),
            (uint)new DateTimeOffset(inode.CTime).ToUnixTimeSeconds());

        if (!engine.CopyToUser(addr, buf)) return;
    }

    private static bool WriteStatx(Engine engine, uint addr, Inode inode, uint mask, Mount? mount)
    {
        Span<byte> buf = stackalloc byte[256];
        buf.Clear();

        var actualMask = LinuxConstants.STATX_BASIC_STATS;
        if ((mask & LinuxConstants.STATX_MNT_ID) != 0 && mount != null)
            actualMask |= LinuxConstants.STATX_MNT_ID;

        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(0x00), actualMask);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(0x04), 4096); // blksize
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(0x08), 0); // attributes

        var nlink = inode.GetDebugNlinkForStat("WriteStatx", inode.GetLinkCountForStat());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(0x10), nlink);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(0x14), (uint)inode.Uid);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(0x18), (uint)inode.Gid);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(0x1C), (ushort)((uint)inode.Mode | (uint)inode.Type));

        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(0x20), inode.Ino);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(0x28), inode.Size);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(0x30), (inode.Size + 511) / 512);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(0x38), 0); // attributes_mask

        WriteStatxTime(buf, 0x40, inode.ATime);
        // We don't track a filesystem-independent birth time yet, so leave stx_btime zeroed
        // and keep STATX_BTIME clear in stx_mask.
        WriteStatxTime(buf, 0x50, DateTime.UnixEpoch);
        WriteStatxTime(buf, 0x60, inode.CTime);
        WriteStatxTime(buf, 0x70, inode.MTime);

        // rdev is encoded as (major << 8) | minor, decode for statx
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(0x80), (inode.Rdev >> 8) & 0xFF); // rdev_major
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(0x84), inode.Rdev & 0xFF); // rdev_minor
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(0x88), (inode.Dev >> 8) & 0xFF); // dev_major
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(0x8C), inode.Dev & 0xFF); // dev_minor
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(0x90), mount != null ? (ulong)mount.Id : 0);

        // STATX_DIOALIGN is intentionally left unsupported for now: most in-memory/virtual
        // filesystems in the emulator don't expose direct-I/O alignment constraints.
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(0x98), 0); // stx_dio_mem_align
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(0x9C), 0); // stx_dio_offset_align

        return engine.CopyToUser(addr, buf);
    }

    private static void WriteStatxTime(Span<byte> buffer, int offset, DateTime dt)
    {
        var dto = new DateTimeOffset(dt);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), dto.ToUnixTimeSeconds());
        var nsec = (uint)(dto.UtcDateTime.Ticks % TimeSpan.TicksPerSecond * 100);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset + 8), nsec);
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

    private static void WriteStatfs32(Engine engine, uint addr, Dentry dentry)
    {
        const int blockSize = 4096;
        const int totalBlocks = 256 * 1024 * 1024 / blockSize; // 256 MiB synthetic capacity
        const int freeBlocks = totalBlocks / 2;
        const int totalFiles = 1_000_000;
        const int freeFiles = 900_000;
        const int fsid0 = 0x78656D75; // "xemu"
        const int fsid1 = 0x46535031; // "FSP1"

        Span<byte> buf = stackalloc byte[64];
        buf.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(0), GetFsMagic(dentry));
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(4), blockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(8), totalBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(12), freeBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(16), freeBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(20), totalFiles);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(24), freeFiles);
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(28), fsid0);
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(32), fsid1);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(36), 255); // f_namelen
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(40), blockSize); // f_frsize
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(44), 0); // f_flags
        // f_spare[4] at [48..63] left as zero

        engine.CopyToUser(addr, buf);
    }

    private static void WriteStatfs64(Engine engine, uint addr, Dentry dentry)
    {
        const int blockSize = 4096;
        const ulong totalBlocks = 256UL * 1024UL * 1024UL / blockSize; // 256 MiB synthetic capacity
        const ulong freeBlocks = totalBlocks / 2;
        const ulong totalFiles = 1_000_000;
        const ulong freeFiles = 900_000;
        const int fsid0 = 0x78656D75; // "xemu"
        const int fsid1 = 0x46535031; // "FSP1"

        // i386 statfs64: sizeof(struct statfs64) = 84 bytes.
        Span<byte> buf = stackalloc byte[84];
        buf.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(0), GetFsMagic(dentry));
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(4), blockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(8), totalBlocks);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(16), freeBlocks);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(24), freeBlocks);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(32), totalFiles);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(40), freeFiles);
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(48), fsid0);
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(52), fsid1);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(56), 255); // f_namelen
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(60), blockSize); // f_frsize
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(64), 0); // f_flags
        // f_spare[4] at [68..83] left as zero

        engine.CopyToUser(addr, buf);
    }

    private async ValueTask<int> SysStatfs(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var pathErr = ReadPathArgumentBytes(a1, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        var loc = PathWalker.PathWalk(path.UnsafeBuffer, path.Length, LookupFlags.FollowSymlink);
        if (!loc.IsValid || loc.Dentry?.Inode == null) return -(int)Errno.ENOENT;

        WriteStatfs32(engine, a2, loc.Dentry);
        return 0;
    }

    private async ValueTask<int> SysFstatfs(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fd = (int)a1;
        var file = GetFD(fd);
        if (file?.OpenedInode == null) return -(int)Errno.EBADF;

        WriteStatfs32(engine, a2, file.Dentry);
        return 0;
    }

    private async ValueTask<int> SysStatfs64(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var size = (int)a2;
        if (size < 84) return -(int)Errno.EINVAL;

        var pathErr = ReadPathArgumentBytes(a1, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        var loc = PathWalker.PathWalk(path.UnsafeBuffer, path.Length, LookupFlags.FollowSymlink);
        if (!loc.IsValid || loc.Dentry?.Inode == null) return -(int)Errno.ENOENT;

        WriteStatfs64(engine, a3, loc.Dentry);
        return 0;
    }

    private async ValueTask<int> SysFstatfs64(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var fd = (int)a1;
        var size = (int)a2;
        if (size < 84) return -(int)Errno.EINVAL;

        var file = GetFD(fd);
        if (file?.OpenedInode == null) return -(int)Errno.EBADF;

        WriteStatfs64(engine, a3, file.Dentry);
        return 0;
    }

    private async ValueTask<int> SysStat64(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return ImplStat64(this, engine, a1, a2, true);
    }

    private async ValueTask<int> SysLstat64(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return ImplStat64(this, engine, a1, a2, false);
    }

    private static int ImplStat64(SyscallManager sm, Engine engine, uint ptrPath, uint ptrStat, bool followLink)
    {
        var pathErr = sm.ReadPathArgumentBytes(ptrPath, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;

        Logger.LogInformation("[Stat64] Path='{Path}'", FsEncoding.DecodeUtf8Lossy(path.Span));
        var loc = sm.PathWalker.PathWalk(path.UnsafeBuffer, path.Length,
            followLink ? LookupFlags.FollowSymlink : LookupFlags.None);
        if (!loc.IsValid || loc.Dentry!.Inode == null)
        {
            Logger.LogWarning("[Stat64] PathWalk failed for '{Path}'", FsEncoding.DecodeUtf8Lossy(path.Span));
            return -(int)Errno.ENOENT;
        }

        RefreshHostfsProjectionForCaller(sm, loc.Dentry.Inode);
        WriteStat64(engine, ptrStat, loc.Dentry.Inode);
        return 0;
    }

    private async ValueTask<int> SysFstat64(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fd = (int)a1;
        var f = GetFD(fd);
        if (f == null || f.OpenedInode == null) return -(int)Errno.EBADF;

        RefreshHostfsProjectionForCaller(this, f.OpenedInode);
        WriteStat64(engine, a2, f.OpenedInode);
        return 0;
    }

    private async ValueTask<int> SysSymlink(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var targetErr = ReadPathArgumentBytes(a1, out var target);
        if (targetErr != 0) return targetErr;
        using var _ = target;
        var linkPathErr = ReadPathArgumentBytes(a2, out var linkpath);
        if (linkPathErr != 0) return linkPathErr;
        using var __ = linkpath;

        var (parentLoc, name, err) = PathWalker.PathWalkForCreate(linkpath.UnsafeBuffer, linkpath.Length);
        if (err != 0) return err;

        var dentry = new Dentry(name, null, parentLoc.Dentry, parentLoc.Dentry!.SuperBlock);
        var create = DacPolicy.ComputeCreationMetadata((engine.Owner as FiberTask)?.Process, parentLoc.Dentry.Inode!,
            0, false);
        return parentLoc.Dentry.Inode!.Symlink(dentry, target.ToArray(), create.Uid, create.Gid);
    }

    private async ValueTask<int> SysReadlink(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var pathErr = ReadPathArgumentBytes(a1, out var path);
        if (pathErr != 0) return pathErr;
        using var _ = path;
        var bufAddr = a2;
        var bufSize = (int)a3;

        var lookup = PathWalker.PathWalkWithData(path.UnsafeBuffer, path.Length, null, LookupFlags.None);
        if (lookup.HasError) return lookup.ErrorCode;
        var loc = lookup.Path;
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;
        if (loc.Dentry.Inode.Type != InodeType.Symlink) return -(int)Errno.EINVAL;

        var task = engine.Owner as FiberTask;
        byte[]? target;
        if (task != null && loc.Dentry.Inode is IContextualSymlinkInode contextualSymlink)
        {
            target = contextualSymlink.Readlink(task);
        }
        else
        {
            var readlinkRc = loc.Dentry.Inode.Readlink(out target);
            if (readlinkRc < 0)
                return readlinkRc;
        }

        if (target == null)
            return -(int)Errno.ENOENT;

        var len = Math.Min(target.Length, bufSize);
        if (!engine.CopyToUser(bufAddr, target.AsSpan(0, len))) return -(int)Errno.EFAULT;
        return len;
    }

    private async ValueTask<int> SysReadlinkAt(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var dirfd = (int)a1;
        var pathErr = ReadPathArgumentBytes(a2, out var path);
        if (pathErr != 0) return pathErr;
        using var _ = path;
        var bufAddr = a3;
        var bufSize = (int)a4;

        PathLocation? startAt = null;
        if (dirfd != -100 && !path.IsAbsolute)
        {
            var fdir = GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.LivePath;
        }

        var lookup = PathWalker.PathWalkWithData(path.UnsafeBuffer, path.Length, startAt ?? CurrentWorkingDirectory,
            LookupFlags.None);
        if (lookup.HasError) return lookup.ErrorCode;
        var loc = lookup.Path;
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;
        if (loc.Dentry.Inode.Type != InodeType.Symlink) return -(int)Errno.EINVAL;

        var task = engine.Owner as FiberTask;
        byte[]? target;
        if (task != null && loc.Dentry.Inode is IContextualSymlinkInode contextualSymlink)
        {
            target = contextualSymlink.Readlink(task);
        }
        else
        {
            var readlinkRc = loc.Dentry.Inode.Readlink(out target);
            if (readlinkRc < 0)
                return readlinkRc;
        }

        if (target == null)
            return -(int)Errno.ENOENT;

        var len = Math.Min(target.Length, bufSize);
        if (!engine.CopyToUser(bufAddr, target.AsSpan(0, len))) return -(int)Errno.EFAULT;
        return len;
    }

    private async ValueTask<int> SysSymlinkAt(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var targetErr = ReadPathArgumentBytes(a1, out var target);
        if (targetErr != 0) return targetErr;
        using var _ = target;
        var dirfd = (int)a2;
        var linkPathErr = ReadPathArgumentBytes(a3, out var linkpath);
        if (linkPathErr != 0) return linkPathErr;
        using var __ = linkpath;

        PathLocation? startAt = null;
        if (dirfd != -100 && !linkpath.IsAbsolute)
        {
            var fdir = GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.LivePath;
        }

        var (parentLoc, name, err) = PathWalker.PathWalkForCreate(linkpath.UnsafeBuffer, linkpath.Length, startAt);
        if (err != 0) return err;

        var dentry2 = new Dentry(name, null, parentLoc.Dentry, parentLoc.Dentry!.SuperBlock);
        var create = DacPolicy.ComputeCreationMetadata((engine.Owner as FiberTask)?.Process, parentLoc.Dentry.Inode!,
            0, false);
        return parentLoc.Dentry.Inode!.Symlink(dentry2, target.ToArray(), create.Uid, create.Gid);
    }

    private async ValueTask<int> SysMount(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        RentedUserBytes source = default;
        if (a1 != 0)
        {
            var sourceErr = ReadPathArgumentBytes(a1, out source, allowEmpty: true);
            if (sourceErr != 0) return sourceErr;
        }
        using var sourceLease = source;
        var sourceText = source.IsEmpty ? string.Empty : FsEncoding.DecodeUtf8Lossy(source.Span);
        var targetErr = ReadPathArgumentBytes(a2, out var target);
        if (targetErr != 0) return targetErr;
        using var targetLease = target;
        var fstype = a3 == 0 ? "" : ReadString(a3);
        var flags = a4;
        var dataAddr = a5;
        string? dataString = null;
        if (dataAddr != 0) dataString = ReadString(dataAddr);

        var targetLoc = PathWalker.PathWalk(target.UnsafeBuffer, target.Length, LookupFlags.None);
        if (!targetLoc.IsValid) return -(int)Errno.ENOENT;

        var targetDentry = targetLoc.Dentry!;
        var targetMount = targetLoc.Mount!;

        // Handle MS_REMOUNT - change flags on existing mount
        if ((flags & LinuxConstants.MS_REMOUNT) != 0)
        {
            if (targetMount == null) return -(int)Errno.EINVAL;

            var remountSet = flags & MountFlagMask;
            var remountClear = ~flags & MountFlagMask;
            targetMount.Flags = ApplyMountFlagUpdate(targetMount.Flags, remountSet, remountClear);
            RefreshMountOptions(targetMount);

            // Update MountList
            var targetPath = GetAbsolutePath(targetLoc);

            return 0;
        }

        // Check if target is already a mount point
        if (targetDentry.IsMounted)
            return -(int)Errno.EBUSY;

        // Handle MS_BIND (Bind Mount)
        if ((flags & LinuxConstants.MS_BIND) != 0)
        {
            if (source.IsEmpty)
                return -(int)Errno.ENOENT;

            var srcLoc = PathWalker.PathWalk(source.UnsafeBuffer, source.Length, LookupFlags.FollowSymlink);
            if (!srcLoc.IsValid || srcLoc.Dentry!.Inode == null)
                return -(int)Errno.ENOENT;

            var srcDentry = srcLoc.Dentry;
            var srcMount = srcLoc.Mount!;

            // Create a bind mount - clone the source mount with the specific dentry as root
            var bindMount = srcMount.Clone(srcDentry);
            bindMount.Source = sourceText;
            bindMount.FsType = "none"; // bind mounts show as "none" in /proc/mounts
            bindMount.Flags = flags & MountFlagMask;
            bindMount.Options = BuildMountOptions(bindMount.Flags);

            var attachRc = AttachDetachedMount(bindMount, targetLoc);
            if (attachRc != 0) return attachRc;

            var targetPath = GetAbsolutePath(targetLoc);

            return 0;
        }

        // Regular mount (non-bind): converge to fs context + detached mount path
        var mountFlags = flags & MountFlagMask;
        var fsCtx = BuildFsContextFromLegacyMount(fstype, sourceText, mountFlags, dataString);
        var mountRc = CreateDetachedMountFromFsContext(fsCtx, 0, out var newMount, (int)flags);
        if (mountRc != 0 || newMount == null)
            return mountRc != 0 ? mountRc : -(int)Errno.EINVAL;

        var attachRegularRc = AttachDetachedMount(newMount, targetLoc);
        if (attachRegularRc != 0) return attachRegularRc;

        var targetPath2 = GetAbsolutePath(targetLoc);

        return 0;
    }

    private async ValueTask<int> SysUmount(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var targetErr = ReadPathArgumentBytes(a1, out var target);
        if (targetErr != 0) return targetErr;
        using var targetLease = target;
        var targetLoc = PathWalker.PathWalk(target.UnsafeBuffer, target.Length, LookupFlags.FollowSymlink);

        if (!targetLoc.IsValid || targetLoc.Mount == null) return -22; // EINVAL

        var mount = targetLoc.Mount;
        // If the path is not the root of the mount, it's not a mount point
        if (targetLoc.Dentry != mount.Root) return -22; // EINVAL

        if (mount == Root.Mount) return -22; // EINVAL // Cannot unmount root

        // Check if filesystem is busy (has active inodes)
        if (mount.SB.HasActiveInodes()) return -16; // EBUSY

        var targetPath = GetAbsolutePath(targetLoc);

        // Detach mount
        UnregisterMount(mount);

        // Release SuperBlock reference
        mount.SB.Put();
        return 0;
    }

    private async ValueTask<int> SysUmount2(Engine engine, uint a1, uint flags, uint a3, uint a4, uint a5,
        uint a6)
    {
        var targetErr = ReadPathArgumentBytes(a1, out var target);
        if (targetErr != 0) return targetErr;
        using var targetLease = target;
        var targetLoc = PathWalker.PathWalk(target.UnsafeBuffer, target.Length, LookupFlags.FollowSymlink);

        if (!targetLoc.IsValid || targetLoc.Mount == null) return -22; // EINVAL

        var mount = targetLoc.Mount;
        if (targetLoc.Dentry != mount.Root) return -22; // EINVAL
        if (mount == Root.Mount) return -22; // EINVAL // Cannot unmount root

        const uint MNT_FORCE = 1;
        const uint MNT_DETACH = 2;

        var targetPath = GetAbsolutePath(targetLoc);

        if ((flags & MNT_DETACH) != 0)
        {
            // Lazy unmount: detach immediately but allow active references to continue
            UnregisterMount(mount);
            // Don't call sb.Put() - let reference counting naturally decrease when files close
            return 0;
        }

        // Normal umount with optional force
        if (mount.SB.HasActiveInodes() && (flags & MNT_FORCE) == 0) return -16; // EBUSY

        // Force unmount or no active inodes
        UnregisterMount(mount);
        mount.SB.Put();

        return 0;
    }

    private async ValueTask<int> SysChroot(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        var allowed = DacPolicy.CanChroot(task.Process);
        if (allowed != 0) return allowed;

        var pathErr = ReadPathArgumentBytes(a1, out var path);
        if (pathErr != 0) return pathErr;
        using var pathLease = path;
        var loc = PathWalker.PathWalk(path.UnsafeBuffer, path.Length, LookupFlags.FollowSymlink);

        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;
        if (loc.Dentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR; // ENOTDIR

        UpdateProcessRoot(loc, "SysChroot");
        return 0;
    }

    private async ValueTask<int> SysFlock(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fd = (int)a1;
        var op = (int)a2;

        var f = GetFD(fd);
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
    ///     fsopen(2) - Open filesystem context
    ///     syscall number 430 on x86
    /// </summary>
    private async ValueTask<int> SysFsopen(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fsName = ReadString(a1);
        var flags = a2;

        if (string.IsNullOrEmpty(fsName)) return -(int)Errno.EINVAL;
        if ((flags & ~FSOPEN_CLOEXEC) != 0) return -(int)Errno.EINVAL;

        var fsType = FileSystemRegistry.Get(fsName);
        if (fsType == null) return -(int)Errno.ENODEV;

        var fileFlags = (flags & FSOPEN_CLOEXEC) != 0 ? FileFlags.O_CLOEXEC | FileFlags.O_RDONLY : FileFlags.O_RDONLY;
        var fsCtx = new FsContextFile(AnonMount.Root, AnonMount, fsName, fileFlags);
        return AllocFD(fsCtx);
    }

    /// <summary>
    ///     fsconfig(2) - Configure filesystem context
    ///     syscall number 431 on x86
    /// </summary>
    private async ValueTask<int> SysFsconfig(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var fsFd = (int)a1;
        var cmd = a2;
        var keyPtr = a3;
        var valuePtr = a4;
        var aux = (int)a5;

        var file = GetFD(fsFd);
        if (file is not FsContextFile fsCtx) return -(int)Errno.EBADF;
        if (fsCtx.State == FsContextState.Created && cmd != FSCONFIG_CMD_CREATE) return -(int)Errno.EBUSY;

        switch (cmd)
        {
            case FSCONFIG_SET_FLAG:
            {
                var key = ReadString(keyPtr);
                if (string.IsNullOrEmpty(key)) return -(int)Errno.EINVAL;
                fsCtx.SetFlag(key);
                return 0;
            }
            case FSCONFIG_SET_STRING:
            {
                var key = ReadString(keyPtr);
                if (string.IsNullOrEmpty(key)) return -(int)Errno.EINVAL;
                if (valuePtr == 0) return -(int)Errno.EINVAL;
                var value = ReadString(valuePtr);
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
    ///     fsmount(2) - Create detached mount from context
    ///     syscall number 432 on x86
    /// </summary>
    private async ValueTask<int> SysFsmount(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var fsFd = (int)a1;
        var flags = a2;
        var mountAttrs = a3;

        if ((flags & ~FSMOUNT_CLOEXEC) != 0) return -(int)Errno.EINVAL;

        var file = GetFD(fsFd);
        if (file is not FsContextFile fsCtx) return -(int)Errno.EBADF;
        if (fsCtx.State != FsContextState.Created) return -(int)Errno.EINVAL;

        var mountRc = CreateDetachedMountFromFsContext(fsCtx, mountAttrs, out var detachedMount);
        if (mountRc != 0) return mountRc;

        var mountFileFlags =
            (flags & FSMOUNT_CLOEXEC) != 0 ? FileFlags.O_CLOEXEC | FileFlags.O_RDONLY : FileFlags.O_RDONLY;
        var mountFile = new MountFile(detachedMount!, mountFileFlags);
        return AllocFD(mountFile);
    }

    /// <summary>
    ///     open_tree(2) - Get a file descriptor for a mount point
    ///     syscall number 428 on x86
    /// </summary>
    private async ValueTask<int> SysOpenTree(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var dfd = (int)a1; // dirfd
        var pathname = a2; // path string address
        var flags = a3; // flags (OPEN_TREE_CLONE, etc.)

        // Resolve path
        PathLocation loc;
        if ((flags & AT_EMPTY_PATH) != 0 && pathname == 0)
        {
            // Use dfd directly
            var f = GetFD(dfd);
            if (f == null) return -(int)Errno.EBADF;
            loc = f.LivePath;
        }
        else
        {
            var pathErr = ReadPathArgumentBytes(pathname, out var path);
            if (pathErr != 0) return pathErr;
            using var pathLease = path;
            var followSymlinks = (flags & AT_SYMLINK_NOFOLLOW) == 0;
            loc = PathWalker.PathWalk(path.UnsafeBuffer, path.Length,
                followSymlinks ? LookupFlags.FollowSymlink : LookupFlags.None);
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
        var newFd = AllocFD(mountFile);
        return newFd;
    }

    /// <summary>
    ///     move_mount(2) - Move a mount from one place to another
    ///     syscall number 429 on x86
    /// </summary>
    private async ValueTask<int> SysMoveMount(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fromDfd = (int)a1; // source dirfd
        var fromPath = a2; // source path
        var toDfd = (int)a3; // target dirfd
        var toPath = a4; // target path
        var flags = a5; // flags

        // Get source mount from fromDfd (must be a MountFile from open_tree)
        var fromFile = GetFD(fromDfd);
        if (fromFile == null) return -(int)Errno.EBADF;
        if (fromFile is not MountFile mountFile) return -(int)Errno.EINVAL;

        var mount = mountFile.Mount;
        if (mount == null) return -(int)Errno.EINVAL;

        // Resolve target path
        PathLocation toLoc;
        if ((flags & MOVE_MOUNT_T_EMPTY_PATH) != 0 && toPath == 0)
        {
            var f = GetFD(toDfd);
            if (f == null) return -(int)Errno.EBADF;
            toLoc = f.LivePath;
        }
        else
        {
            var pathErr = ReadPathArgumentBytes(toPath, out var path);
            if (pathErr != 0) return pathErr;
            using var pathLease = path;
            toLoc = PathWalker.PathWalk(path.UnsafeBuffer, path.Length, LookupFlags.FollowSymlink);
        }

        if (!toLoc.IsValid) return -(int)Errno.ENOENT;
        if (toLoc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        var attachRc = AttachDetachedMount(mount, toLoc);
        if (attachRc != 0) return attachRc;

        var targetPathStr = GetAbsolutePath(toLoc);

        return 0;
    }

    /// <summary>
    ///     mount_setattr(2) - Set attributes on a mount
    ///     syscall number 442 on x86
    /// </summary>
    private async ValueTask<int> SysMountSetattr(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var dfd = (int)a1; // dirfd
        var pathAddr = a2; // path
        var flags = a3; // flags (AT_RECURSIVE, etc.)
        var uattr = a4; // struct mount_attr pointer
        var usize = (int)a5; // size of mount_attr

        // Resolve path
        PathLocation loc;
        if ((flags & AT_EMPTY_PATH) != 0 && pathAddr == 0)
        {
            var f = GetFD(dfd);
            if (f == null) return -(int)Errno.EBADF;
            loc = f.LivePath;
        }
        else
        {
            var pathErr = ReadPathArgumentBytes(pathAddr, out var path);
            if (pathErr != 0) return pathErr;
            using var pathLease = path;
            loc = PathWalker.PathWalk(path.UnsafeBuffer, path.Length, LookupFlags.FollowSymlink);
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
        if (!engine.CopyFromUser(uattr, buf))
            return -(int)Errno.EFAULT;

        var attrSet = BitConverter.ToUInt64(buf, 0);
        var attrClr = BitConverter.ToUInt64(buf, 8);

        var setMask = MapMountAttrToMountFlags(attrSet);
        var clearMask = MapMountAttrToMountFlags(attrClr);
        mount.Flags = ApplyMountFlagUpdate(mount.Flags, setMask, clearMask);
        RefreshMountOptions(mount);

        return 0;
    }

    #endregion
}
