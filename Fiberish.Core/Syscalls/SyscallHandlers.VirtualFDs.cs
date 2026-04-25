using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    private static readonly FsName Inotify = FsName.FromString("anon_inode:[inotify]");

#pragma warning disable CS1998 // Async method lacks await operators
    private async ValueTask<int> SysEventFd(Engine engine, uint initval, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        return await SysEventFd2(engine, initval, 0, a3, a4, a5, a6);
    }

    private async ValueTask<int> SysEventFd2(Engine engine, uint initval, uint flags, uint a3, uint a4, uint a5,
        uint a6)
    {
        var eflags = FileFlags.O_RDWR;
        if ((flags & LinuxConstants.EFD_NONBLOCK) != 0) eflags |= FileFlags.O_NONBLOCK;
        if ((flags & LinuxConstants.EFD_CLOEXEC) != 0) eflags |= FileFlags.O_CLOEXEC;
        if ((flags & LinuxConstants.EFD_SEMAPHORE) != 0) eflags |= (FileFlags)LinuxConstants.EFD_SEMAPHORE;
        var task = engine.Owner as FiberTask;
        var inode = new EventFdInode(0, MemfdSuperBlock, task?.CommonKernel, initval, eflags);
        var dentry = new Dentry(FsName.FromString("anon_inode:[eventfd]"), inode, null, MemfdSuperBlock);
        var file = new LinuxFile(dentry, eflags, AnonMount);

        var fd = AllocFD(file);
        if (fd < 0) return fd;

        return fd;
    }

    private async ValueTask<int> SysTimerFdCreate(Engine engine, uint clockId, uint flags, uint a3, uint a4,
        uint a5, uint a6)
    {
        if (clockId != LinuxConstants.CLOCK_REALTIME && clockId != LinuxConstants.CLOCK_MONOTONIC)
            return -(int)Errno.EINVAL;

        var eflags = FileFlags.O_RDWR;
        if ((flags & LinuxConstants.TFD_NONBLOCK) != 0) eflags |= FileFlags.O_NONBLOCK;
        if ((flags & LinuxConstants.TFD_CLOEXEC) != 0) eflags |= FileFlags.O_CLOEXEC;

        var inode = new TimerFdInode(0, MemfdSuperBlock);
        var dentry = new Dentry(FsName.FromString("anon_inode:[timerfd]"), inode, null, MemfdSuperBlock);
        var file = new LinuxFile(dentry, eflags, AnonMount);

        var fd = AllocFD(file);
        if (fd < 0) return fd;

        return fd;
    }

    private async ValueTask<int> SysTimerFdSetTime(Engine engine, uint fd, uint flags, uint newValuePtr,
        uint oldValuePtr, uint a5, uint a6)
    {
        if (!FDs.TryGetValue((int)fd, out var file) || file.OpenedInode is not TimerFdInode timerFd)
            return -(int)Errno.EBADF;
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        if (newValuePtr == 0) return -(int)Errno.EFAULT;

        var buf = new byte[16];
        if (!engine.CopyFromUser(newValuePtr, buf)) return -(int)Errno.EFAULT;

        var intervalSec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
        var intervalNsec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));
        var valueSec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8, 4));
        var valueNsec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(12, 4));

        if (oldValuePtr != 0) await SysTimerFdGetTime(engine, fd, oldValuePtr, 0, 0, 0, 0);

        var isAbsolute = (flags & 1) != 0; // TFD_TIMER_ABSTIME

        var intervalMs = (ulong)(intervalSec * 1000 + intervalNsec / 1000000);
        var valueMs = (ulong)(valueSec * 1000 + valueNsec / 1000000);

        timerFd.SetTime(task, (long)intervalMs, (long)valueMs, isAbsolute);

        return 0;
    }

    private async ValueTask<int> SysTimerFdGetTime(Engine engine, uint fd, uint curValuePtr, uint a3, uint a4,
        uint a5, uint a6)
    {
        if (!FDs.TryGetValue((int)fd, out var file) || file.OpenedInode is not TimerFdInode timerFd)
            return -(int)Errno.EBADF;
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        if (curValuePtr == 0) return -(int)Errno.EFAULT;

        timerFd.GetTime(task, out var intervalMs, out var valueMs);

        var buf = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), (int)(intervalMs / 1000));
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), (int)(intervalMs % 1000 * 1000000));
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), (int)(valueMs / 1000));
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), (int)(valueMs % 1000 * 1000000));

        if (!engine.CopyToUser(curValuePtr, buf)) return -(int)Errno.EFAULT;

        return 0;
    }

    private async ValueTask<int> SysSignalFd(Engine engine, uint fd, uint maskPtr, uint size, uint a4, uint a5,
        uint a6)
    {
        // For sys_signalfd, flags are 0
        return await SysSignalFd4(engine, fd, maskPtr, size, 0, a5, a6);
    }

    private async ValueTask<int> SysSignalFd4(Engine engine, uint fd, uint maskPtr, uint size, uint flags,
        uint a5, uint a6)
    {
        if (size != 8) return -(int)Errno.EINVAL; // i386 sigset_t is 8 bytes

        var maskBuf = new byte[8];
        if (!engine.CopyFromUser(maskPtr, maskBuf)) return -(int)Errno.EFAULT;

        var mask = BinaryPrimitives.ReadUInt64LittleEndian(maskBuf);
        mask &= ~(1UL << ((int)Signal.SIGKILL - 1));
        mask &= ~(1UL << ((int)Signal.SIGSTOP - 1));

        // If fd == -1, create a new signalfd
        if (fd == unchecked((uint)-1))
        {
            var eflags = FileFlags.O_RDWR;
            if ((flags & LinuxConstants.SFD_NONBLOCK) != 0) eflags |= FileFlags.O_NONBLOCK;
            if ((flags & LinuxConstants.SFD_CLOEXEC) != 0) eflags |= FileFlags.O_CLOEXEC;

            var inode = new SignalFdInode(0, MemfdSuperBlock, mask);
            var dentry = new Dentry(FsName.FromString("anon_inode:[signalfd]"), inode, null, MemfdSuperBlock);
            var file = new LinuxFile(dentry, eflags, AnonMount);

            var newFd = AllocFD(file);
            return newFd;
        }

        // Modifying existing signalfd
        if (!FDs.TryGetValue((int)fd, out var existingFile)) return -(int)Errno.EBADF;
        if (existingFile.OpenedInode is SignalFdInode sfd)
        {
            sfd.SetMask(mask);
            return 0;
        }

        return -(int)Errno.EINVAL;
    }

    private async ValueTask<int> SysInotifyInit(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        return await SysInotifyInit1(engine, 0, a2, a3, a4, a5, a6);
    }

    private async ValueTask<int> SysInotifyInit1(Engine engine, uint flags, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        if ((flags & ~(LinuxConstants.IN_NONBLOCK | LinuxConstants.IN_CLOEXEC)) != 0)
            return -(int)Errno.EINVAL;

        var eflags = FileFlags.O_RDONLY;
        if ((flags & LinuxConstants.IN_NONBLOCK) != 0) eflags |= FileFlags.O_NONBLOCK;
        if ((flags & LinuxConstants.IN_CLOEXEC) != 0) eflags |= FileFlags.O_CLOEXEC;

        var inode = new InotifyInode(0, MemfdSuperBlock);
        var dentry = new Dentry(Inotify, inode, null, MemfdSuperBlock);
        var file = new LinuxFile(dentry, eflags, AnonMount);

        return AllocFD(file);
    }

    private async ValueTask<int> SysInotifyAddWatch(Engine engine, uint fd, uint pathPtr, uint mask, uint a4,
        uint a5, uint a6)
    {
        var file = GetFD((int)fd);
        if (file == null) return -(int)Errno.EBADF;
        if (file.OpenedInode is not InotifyInode inotify) return -(int)Errno.EINVAL;

        var pathErr = ReadPathArgumentBytes(pathPtr, out var path);
        if (pathErr != 0) return pathErr;
        using var _ = path;

        var lookupFlags = (mask & LinuxConstants.IN_DONT_FOLLOW) != 0
            ? LookupFlags.None
            : LookupFlags.FollowSymlink;
        var lookup = PathWalker.PathWalkWithData(path.UnsafeBuffer, path.Length, null, lookupFlags);
        if (lookup.HasError) return lookup.ErrorCode;

        var loc = lookup.Path;
        if (!loc.IsValid || loc.Dentry?.Inode == null) return -(int)Errno.ENOENT;
        if ((mask & LinuxConstants.IN_ONLYDIR) != 0 && loc.Dentry.Inode.Type != InodeType.Directory)
            return -(int)Errno.ENOTDIR;

        var pathKey = GetAbsolutePath(loc);
        return inotify.AddWatch(pathKey, mask, loc.Dentry.Inode.Type == InodeType.Directory);
    }

    private async ValueTask<int> SysInotifyRmWatch(Engine engine, uint fd, uint wd, uint a3, uint a4, uint a5,
        uint a6)
    {
        var file = GetFD((int)fd);
        if (file == null) return -(int)Errno.EBADF;
        if (file.OpenedInode is not InotifyInode inotify) return -(int)Errno.EINVAL;

        return inotify.RemoveWatch((int)wd);
    }
#pragma warning restore CS1998
}
