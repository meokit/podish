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

        if (epFile.OpenedInode is not EpollInode epollInode) return -(int)Errno.EINVAL; // Not an epoll fd

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
        return await DoEpollWait(sm, a1, a2, a3, (int)a4);
    }

    private static async ValueTask<int> SysEpollPwait(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        // a1: epfd, a2: events, a3: maxevents, a4: timeout, a5: sigmask, a6: sigsetsize
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        if (!TryReadDirectSigmask(sm, a5, a6, out var hasMask, out var newMask, out var maskErr)) return maskErr;

        var oldMask = task.SignalMask;
        if (hasMask) task.SignalMask = newMask;
        int result;
        try
        {
            result = await DoEpollWait(sm, a1, a2, a3, (int)a4);
        }
        finally
        {
            if (hasMask) task.SignalMask = oldMask;
        }

        return result;
    }

    private static async ValueTask<int> SysEpollPwait2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        // a1: epfd, a2: events, a3: maxevents, a4: timespec64*, a5: sigmask, a6: sigsetsize
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        if (!TryReadTimespec64TimeoutMs(sm, a4, out var timeoutMs, out var tsErr)) return tsErr;
        if (!TryReadDirectSigmask(sm, a5, a6, out var hasMask, out var newMask, out var maskErr)) return maskErr;

        var oldMask = task.SignalMask;
        if (hasMask) task.SignalMask = newMask;
        try
        {
            return await DoEpollWait(sm, a1, a2, a3, timeoutMs);
        }
        finally
        {
            if (hasMask) task.SignalMask = oldMask;
        }
    }

    private static async ValueTask<int> DoEpollWait(SyscallManager sm, uint epfdArg, uint eventsPtr, uint maxeventsArg,
        int timeoutMs)
    {
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var epfd = (int)epfdArg;
        var maxevents = (int)maxeventsArg;
        if (maxevents <= 0) return -(int)Errno.EINVAL;

        var epFile = sm.GetFD(epfd);
        if (epFile == null) return -(int)Errno.EBADF;
        if (epFile.OpenedInode is not EpollInode epollInode) return -(int)Errno.EINVAL;

        // epoll_event is 12 bytes on i386.
        var buf = new byte[maxevents * 12];

        var ready = epollInode.TryHarvestNow(buf, maxevents);
        if (ready > 0 || timeoutMs == 0)
        {
            if (ready > 0 && !task.CPU.CopyToUser(eventsPtr, buf.AsSpan(0, ready * 12)))
                return -(int)Errno.EFAULT;
            return ready;
        }

        var result = await epollInode.WaitAsync(buf, maxevents, timeoutMs);
        if (result > 0 && !task.CPU.CopyToUser(eventsPtr, buf.AsSpan(0, result * 12)))
            return -(int)Errno.EFAULT;

        return result;
    }
#pragma warning restore CS1998
}