using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998
    private static async ValueTask<int> SysEpollCreate(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var size = (int)a1;
        if (size <= 0) return -(int)Errno.EINVAL;

        var inode = new EpollInode(0, sm.MemfdSuperBlock);
        var dentry = new Dentry("[epoll]", inode, null, sm.MemfdSuperBlock);
        var file = new LinuxFile(dentry, FileFlags.O_RDWR, sm.AnonMount);

        return sm.AllocFD(file);
    }

    private static async ValueTask<int> SysEpollCreate1(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var flags = (int)a1;

        var inode = new EpollInode(0, sm.MemfdSuperBlock);
        var fileFlags = FileFlags.O_RDWR;
        if ((flags & (int)FileFlags.O_CLOEXEC) != 0) fileFlags |= FileFlags.O_CLOEXEC;

        var dentry = new Dentry("[epoll]", inode, null, sm.MemfdSuperBlock);
        var file = new LinuxFile(dentry, fileFlags, sm.AnonMount);
        return sm.AllocFD(file);
    }

    private static async ValueTask<int> SysEpollCtl(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var epfd = (int)a1;
        var op = (int)a2;
        var fd = (int)a3;
        var eventPtr = a4;

        var epFile = sm.GetFD(epfd);
        if (epFile == null) return -(int)Errno.EBADF;

        if (epFile.Dentry.Inode is not EpollInode epollInode) return -(int)Errno.EINVAL; // Not an epoll fd

        var targetFile = sm.GetFD(fd);
        if (targetFile == null) return -(int)Errno.EBADF;

        if (epFile == targetFile) return -(int)Errno.EINVAL; // Cannot watch itself

        uint events = 0;
        ulong data = 0;

        if (op != LinuxConstants.EPOLL_CTL_DEL)
        {
            if (eventPtr == 0) return -(int)Errno.EFAULT;
            var buf = new byte[12];
            if (!task.CPU.CopyFromUser(eventPtr, buf)) return -(int)Errno.EFAULT;

            events = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4));
            data = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(4, 8));
        }

        return epollInode.Ctl(op, fd, targetFile, events, data);
    }

    private static async ValueTask<int> SysEpollWait(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var epfd = (int)a1;
        var eventsPtr = a2;
        var maxevents = (int)a3;
        var timeout = (int)a4;

        if (maxevents <= 0) return -(int)Errno.EINVAL;

        var epFile = sm.GetFD(epfd);
        if (epFile == null) return -(int)Errno.EBADF;
        if (epFile.Dentry.Inode is not EpollInode epollInode) return -(int)Errno.EINVAL;

        // Note: epoll_event is 12 bytes long on i386
        var buf = new byte[maxevents * 12];

        // 1. Synchronous check first (like SysPoll does ScanPoll before creating awaiter)
        var ready = epollInode.TryHarvestNow(buf, maxevents);
        if (ready > 0 || timeout == 0)
        {
            if (ready > 0)
                if (!task.CPU.CopyToUser(eventsPtr, buf.AsSpan(0, ready * 12)))
                    return -(int)Errno.EFAULT;
            return ready;
        }

        // 2. Nothing ready, go async
        var result = await epollInode.WaitAsync(buf, maxevents, timeout);

        if (result > 0)
            if (!task.CPU.CopyToUser(eventsPtr, buf.AsSpan(0, result * 12)))
                return -(int)Errno.EFAULT;

        return result;
    }

    private static async ValueTask<int> SysEpollPwait(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        // a1: epfd, a2: events, a3: maxevents, a4: timeout, a5: sigmask, a6: sigsetsize
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var sigmaskPtr = a5;
        var oldMask = task.SignalMask;

        if (sigmaskPtr != 0)
        {
            var sigsetsize = a6;
            if (sigsetsize != 8) return -(int)Errno.EINVAL;
            var maskBuf = new byte[8];
            if (!task.CPU.CopyFromUser(sigmaskPtr, maskBuf)) return -(int)Errno.EFAULT;
            var mask = BinaryPrimitives.ReadUInt64LittleEndian(maskBuf);

            mask &= ~(1UL << 8); // SIGKILL
            mask &= ~(1UL << 18); // SIGSTOP
            task.SignalMask = mask;
        }

        int result;
        try
        {
            result = await SysEpollWait(state, a1, a2, a3, a4, 0, 0);
        }
        finally
        {
            if (sigmaskPtr != 0) task.SignalMask = oldMask;
        }

        return result;
    }
#pragma warning restore CS1998
}