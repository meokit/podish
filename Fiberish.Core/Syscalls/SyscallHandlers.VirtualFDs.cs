using System.Buffers.Binary;
using Fiberish.Auth.Permission;
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
        if ((flags & ~(uint)(LinuxConstants.TFD_NONBLOCK | LinuxConstants.TFD_CLOEXEC)) != 0)
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
        if (!FDs.TryGetValue((int)fd, out var file)) return -(int)Errno.EBADF;
        if (file.OpenedInode is not TimerFdInode timerFd) return -(int)Errno.EINVAL;
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        if ((flags & ~(uint)(LinuxConstants.TFD_TIMER_ABSTIME | LinuxConstants.TFD_TIMER_CANCEL_ON_SET)) != 0) return -(int)Errno.EINVAL;

        if (newValuePtr == 0) return -(int)Errno.EFAULT;

        Span<byte> buf = stackalloc byte[16];
        if (!engine.CopyFromUser(newValuePtr, buf)) return -(int)Errno.EFAULT;

        var intervalSec = BinaryPrimitives.ReadInt32LittleEndian(buf[..4]);
        var intervalNsec = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(4, 4));
        var valueSec = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(8, 4));
        var valueNsec = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(12, 4));

        if (intervalSec < 0 || valueSec < 0 ||
            (uint)intervalNsec >= 1_000_000_000 || (uint)valueNsec >= 1_000_000_000)
            return -(int)Errno.EINVAL;

        if (oldValuePtr != 0)
        {
            var oldRc = await SysTimerFdGetTime(engine, fd, oldValuePtr, 0, 0, 0, 0);
            if (oldRc != 0) return oldRc;
        }

        var isAbsolute = (flags & LinuxConstants.TFD_TIMER_ABSTIME) != 0;

        var intervalMs = (ulong)(intervalSec * 1000 + intervalNsec / 1000000);
        var valueMs = (ulong)(valueSec * 1000 + valueNsec / 1000000);

        timerFd.SetTime(task, (long)intervalMs, (long)valueMs, isAbsolute);

        return 0;
    }

    private async ValueTask<int> SysTimerFdGetTime(Engine engine, uint fd, uint curValuePtr, uint a3, uint a4,
        uint a5, uint a6)
    {
        if (!FDs.TryGetValue((int)fd, out var file)) return -(int)Errno.EBADF;
        if (file.OpenedInode is not TimerFdInode timerFd) return -(int)Errno.EINVAL;
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        if (curValuePtr == 0) return -(int)Errno.EFAULT;

        timerFd.GetTime(task, out var intervalMs, out var valueMs);

        Span<byte> buf = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(buf[..4], (int)(intervalMs / 1000));
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(4, 4), (int)(intervalMs % 1000 * 1000000));
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(8, 4), (int)(valueMs / 1000));
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(12, 4), (int)(valueMs % 1000 * 1000000));

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
        if ((flags & ~(uint)(LinuxConstants.SFD_NONBLOCK | LinuxConstants.SFD_CLOEXEC)) != 0)
            return -(int)Errno.EINVAL;

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

        // Modifying an existing signalfd updates its mask and returns the same fd.
        if (!FDs.TryGetValue((int)fd, out var existingFile)) return -(int)Errno.EBADF;
        if (existingFile.OpenedInode is SignalFdInode sfd)
        {
            sfd.SetMask(mask);
            return (int)fd;
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

        var ownerUid = (engine.Owner as FiberTask)?.Process.UID ?? 0;
        var inode = new InotifyInode(0, MemfdSuperBlock, ownerUid);
        if (!inode.InstanceSlotHeld)
            return -(int)Errno.EMFILE;

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

        // inotify_add_watch(2): the mask must contain at least one event bit, and
        // IN_MASK_CREATE cannot be combined with IN_MASK_ADD.
        const uint validMask = LinuxConstants.IN_ALL_EVENTS | LinuxConstants.IN_ONLYDIR |
                               LinuxConstants.IN_DONT_FOLLOW | LinuxConstants.IN_EXCL_UNLINK |
                               LinuxConstants.IN_MASK_CREATE | LinuxConstants.IN_MASK_ADD |
                               LinuxConstants.IN_ONESHOT | LinuxConstants.IN_ISDIR |
                               LinuxConstants.IN_UNMOUNT | LinuxConstants.IN_Q_OVERFLOW | LinuxConstants.IN_IGNORED;
        if ((mask & ~validMask) != 0) return -(int)Errno.EINVAL;
        if ((mask & LinuxConstants.IN_ALL_EVENTS) == 0) return -(int)Errno.EINVAL;
        if ((mask & LinuxConstants.IN_MASK_CREATE) != 0 && (mask & LinuxConstants.IN_MASK_ADD) != 0)
            return -(int)Errno.EINVAL;

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
        var watchedInode = loc.Dentry.Inode;
        if ((mask & LinuxConstants.IN_ONLYDIR) != 0 && watchedInode.Type != InodeType.Directory)
            return -(int)Errno.ENOTDIR;

        // inotify_add_watch(2) requires read permission on the watched object.
        var task = engine.Owner as FiberTask;
        if (task?.Process != null)
        {
            var accessRc = DacPolicy.CheckPathAccess(task.Process, watchedInode, AccessMode.MayRead, true);
            if (accessRc < 0) return accessRc;
        }

        return inotify.AddWatch(mask, watchedInode.Type == InodeType.Directory,
            watchedInode.SuperBlock, watchedInode.Ino, watchedInode);
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
