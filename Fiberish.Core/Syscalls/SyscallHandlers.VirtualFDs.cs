using System.Buffers.Binary;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998 // Async method lacks await operators
    private static async ValueTask<int> SysEventFd(IntPtr state, uint initval, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        return await SysEventFd2(state, initval, 0, a3, a4, a5, a6);
    }

    private static async ValueTask<int> SysEventFd2(IntPtr state, uint initval, uint flags, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var eflags = FileFlags.O_RDWR;
        if ((flags & LinuxConstants.EFD_NONBLOCK) != 0) eflags |= FileFlags.O_NONBLOCK;
        if ((flags & LinuxConstants.EFD_CLOEXEC) != 0) eflags |= FileFlags.O_CLOEXEC;
        if ((flags & LinuxConstants.EFD_SEMAPHORE) != 0) eflags |= (FileFlags)LinuxConstants.EFD_SEMAPHORE;

        var inode = new EventFdInode(0, sm.MemfdSuperBlock, initval, eflags);
        var dentry = new Dentry("anon_inode:[eventfd]", inode, null, sm.MemfdSuperBlock);
        var file = new LinuxFile(dentry, eflags, sm.AnonMount);

        var fd = sm.AllocFD(file);
        if (fd < 0) return fd;

        return fd;
    }

    private static async ValueTask<int> SysTimerFdCreate(IntPtr state, uint clockId, uint flags, uint a3, uint a4,
        uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        if (clockId != LinuxConstants.CLOCK_REALTIME && clockId != LinuxConstants.CLOCK_MONOTONIC)
            return -(int)Errno.EINVAL;

        var eflags = FileFlags.O_RDWR;
        if ((flags & LinuxConstants.TFD_NONBLOCK) != 0) eflags |= FileFlags.O_NONBLOCK;
        if ((flags & LinuxConstants.TFD_CLOEXEC) != 0) eflags |= FileFlags.O_CLOEXEC;

        var inode = new TimerFdInode(0, sm.MemfdSuperBlock);
        var dentry = new Dentry("anon_inode:[timerfd]", inode, null, sm.MemfdSuperBlock);
        var file = new LinuxFile(dentry, eflags, sm.AnonMount);

        var fd = sm.AllocFD(file);
        if (fd < 0) return fd;

        return fd;
    }

    private static async ValueTask<int> SysTimerFdSetTime(IntPtr state, uint fd, uint flags, uint newValuePtr,
        uint oldValuePtr, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        if (!sm.FDs.TryGetValue((int)fd, out var file) || file.OpenedInode is not TimerFdInode timerFd)
            return -(int)Errno.EBADF;

        if (newValuePtr == 0) return -(int)Errno.EFAULT;

        var buf = new byte[16];
        if (!sm.Engine.CopyFromUser(newValuePtr, buf)) return -(int)Errno.EFAULT;

        var intervalSec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
        var intervalNsec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));
        var valueSec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8, 4));
        var valueNsec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(12, 4));

        if (oldValuePtr != 0) await SysTimerFdGetTime(state, fd, oldValuePtr, 0, 0, 0, 0);

        var isAbsolute = (flags & 1) != 0; // TFD_TIMER_ABSTIME

        var intervalMs = (ulong)(intervalSec * 1000 + intervalNsec / 1000000);
        var valueMs = (ulong)(valueSec * 1000 + valueNsec / 1000000);

        timerFd.SetTime((long)intervalMs, (long)valueMs, isAbsolute);

        return 0;
    }

    private static async ValueTask<int> SysTimerFdGetTime(IntPtr state, uint fd, uint curValuePtr, uint a3, uint a4,
        uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        if (!sm.FDs.TryGetValue((int)fd, out var file) || file.OpenedInode is not TimerFdInode timerFd)
            return -(int)Errno.EBADF;

        if (curValuePtr == 0) return -(int)Errno.EFAULT;

        timerFd.GetTime(out var intervalMs, out var valueMs);

        var buf = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), (int)(intervalMs / 1000));
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), (int)(intervalMs % 1000 * 1000000));
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), (int)(valueMs / 1000));
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), (int)(valueMs % 1000 * 1000000));

        if (!sm.Engine.CopyToUser(curValuePtr, buf)) return -(int)Errno.EFAULT;

        return 0;
    }

    private static async ValueTask<int> SysSignalFd(IntPtr state, uint fd, uint maskPtr, uint size, uint a4, uint a5,
        uint a6)
    {
        // For sys_signalfd, flags are 0
        return await SysSignalFd4(state, fd, maskPtr, size, 0, a5, a6);
    }

    private static async ValueTask<int> SysSignalFd4(IntPtr state, uint fd, uint maskPtr, uint size, uint flags,
        uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        if (size != 8) return -(int)Errno.EINVAL; // i386 sigset_t is 8 bytes

        var maskBuf = new byte[8];
        if (!sm.Engine.CopyFromUser(maskPtr, maskBuf)) return -(int)Errno.EFAULT;

        var mask = BinaryPrimitives.ReadUInt64LittleEndian(maskBuf);

        // If fd == -1, create a new signalfd
        if (fd == unchecked((uint)-1))
        {
            var eflags = FileFlags.O_RDWR;
            if ((flags & LinuxConstants.SFD_NONBLOCK) != 0) eflags |= FileFlags.O_NONBLOCK;
            if ((flags & LinuxConstants.SFD_CLOEXEC) != 0) eflags |= FileFlags.O_CLOEXEC;

            var inode = new SignalFdInode(0, sm.MemfdSuperBlock, mask);
            var dentry = new Dentry("anon_inode:[signalfd]", inode, null, sm.MemfdSuperBlock);
            var file = new LinuxFile(dentry, eflags, sm.AnonMount);

            var newFd = sm.AllocFD(file);
            return newFd;
        }

        // Modifying existing signalfd
        if (!sm.FDs.TryGetValue((int)fd, out var existingFile)) return -(int)Errno.EBADF;
        if (existingFile.OpenedInode is SignalFdInode sfd)
        {
            sfd.SetMask(mask);
            return 0;
        }

        return -(int)Errno.EINVAL;
    }
#pragma warning restore CS1998
}