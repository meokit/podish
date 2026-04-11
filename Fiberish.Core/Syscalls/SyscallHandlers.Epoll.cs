using System.Buffers;
using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    private static readonly FsName Epoll = FsName.FromString("[epoll]");
    private const int EpollEventSize = 12;
    private const int StackEpollBufferLimit = 256;

#pragma warning disable CS1998
    private static int ValidateAndNormalizeEpollCtlEvents(ref uint events)
    {
        const uint supportedEvents = LinuxConstants.EPOLLIN |
                                     LinuxConstants.EPOLLPRI |
                                     LinuxConstants.EPOLLOUT |
                                     LinuxConstants.EPOLLERR |
                                     LinuxConstants.EPOLLHUP |
                                     LinuxConstants.EPOLLRDNORM |
                                     LinuxConstants.EPOLLRDBAND |
                                     LinuxConstants.EPOLLWRNORM |
                                     LinuxConstants.EPOLLWRBAND |
                                     LinuxConstants.EPOLLMSG |
                                     LinuxConstants.EPOLLRDHUP |
                                     LinuxConstants.EPOLLET |
                                     LinuxConstants.EPOLLONESHOT |
                                     LinuxConstants.EPOLLWAKEUP |
                                     LinuxConstants.EPOLLEXCLUSIVE;

        if ((events & ~supportedEvents) != 0)
            return -(int)Errno.EINVAL;

        if ((events & LinuxConstants.EPOLLWAKEUP) != 0)
            // Linux may silently ignore EPOLLWAKEUP if the caller lacks CAP_BLOCK_SUSPEND.
            // We don't model autosleep/capability-gated wake locks yet, so accept the flag
            // but treat it as a no-op hint.
            events &= ~LinuxConstants.EPOLLWAKEUP;

        if ((events & LinuxConstants.EPOLLEXCLUSIVE) != 0)
            // Exclusive wakeups require kernel-level thundering-herd avoidance across
            // multiple epoll instances. We don't model that dispatch policy yet.
            return -(int)Errno.EINVAL;

        return 0;
    }

    private async ValueTask<int> SysEpollCreate(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var size = (int)a1;
        if (size <= 0) return -(int)Errno.EINVAL;

        var inode = new EpollInode(0, MemfdSuperBlock, task.CommonKernel);
        var dentry = new Dentry(Epoll, inode, null, MemfdSuperBlock);
        var file = new LinuxFile(dentry, FileFlags.O_RDWR, AnonMount);

        return AllocFD(file);
    }

    private async ValueTask<int> SysEpollCreate1(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var flags = (int)a1;

        var inode = new EpollInode(0, MemfdSuperBlock, task.CommonKernel);
        var fileFlags = FileFlags.O_RDWR;
        if ((flags & (int)FileFlags.O_CLOEXEC) != 0) fileFlags |= FileFlags.O_CLOEXEC;

        var dentry = new Dentry(Epoll, inode, null, MemfdSuperBlock);
        var file = new LinuxFile(dentry, fileFlags, AnonMount);
        return AllocFD(file);
    }

    private async ValueTask<int> SysEpollCtl(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var epfd = (int)a1;
        var op = (int)a2;
        var fd = (int)a3;
        var eventPtr = a4;

        var epFile = GetFD(epfd);
        if (epFile == null) return -(int)Errno.EBADF;

        if (epFile.OpenedInode is not EpollInode epollInode) return -(int)Errno.EINVAL; // Not an epoll fd

        var targetFile = GetFD(fd);
        if (targetFile == null) return -(int)Errno.EBADF;

        if (epFile == targetFile) return -(int)Errno.EINVAL; // Cannot watch itself

        uint events = 0;
        ulong data = 0;

        if (op != LinuxConstants.EPOLL_CTL_DEL)
        {
            if (eventPtr == 0) return -(int)Errno.EFAULT;
            Span<byte> buf = stackalloc byte[EpollEventSize];
            if (!task.CPU.CopyFromUser(eventPtr, buf)) return -(int)Errno.EFAULT;

            events = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(0, 4));
            data = BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(4, 8));

            var validateRc = ValidateAndNormalizeEpollCtlEvents(ref events);
            if (validateRc != 0) return validateRc;
        }

        return epollInode.Ctl(task, op, fd, targetFile, events, data);
    }

    private ValueTask<int> SysEpollWait(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) =>
        DoEpollWait(this, engine, a1, a2, a3, (int)a4, X86SyscallNumbers.epoll_wait);

    private async ValueTask<int> SysEpollPwait(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        // a1: epfd, a2: events, a3: maxevents, a4: timeout, a5: sigmask, a6: sigsetsize
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        if (!TryReadDirectSigmask(engine, a5, a6, out var hasMask, out var newMask, out var maskErr)) return maskErr;

        var oldMask = task.SignalMask;
        if (hasMask) task.SignalMask = newMask;
        var result = await DoEpollWait(this, engine, a1, a2, a3, (int)a4, X86SyscallNumbers.epoll_pwait);
        if (hasMask)
            if (result == -(int)Errno.ERESTARTSYS)
                task.DeferSignalMaskRestore(oldMask);
            else
                task.SignalMask = oldMask;
        return result;
    }

    private async ValueTask<int> SysEpollPwait2(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        // a1: epfd, a2: events, a3: maxevents, a4: timespec64*, a5: sigmask, a6: sigsetsize
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        if (!TryReadTimespec64TimeoutMs(engine, a4, out var timeoutMs, out var tsErr)) return tsErr;
        if (!TryReadDirectSigmask(engine, a5, a6, out var hasMask, out var newMask, out var maskErr)) return maskErr;

        var oldMask = task.SignalMask;
        if (hasMask) task.SignalMask = newMask;
        var result = await DoEpollWait(this, engine, a1, a2, a3, timeoutMs, X86SyscallNumbers.epoll_pwait2);
        if (hasMask)
            if (result == -(int)Errno.ERESTARTSYS)
                task.DeferSignalMaskRestore(oldMask);
            else
                task.SignalMask = oldMask;

        return result;
    }

    private static ValueTask<int> DoEpollWait(SyscallManager sm, Engine engine, uint epfdArg, uint eventsPtr,
        uint maxeventsArg, int timeoutMs, uint syscallNr)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return new ValueTask<int>(-(int)Errno.EPERM);

        var epfd = (int)epfdArg;
        var maxevents = (int)maxeventsArg;
        if (maxevents <= 0) return new ValueTask<int>(-(int)Errno.EINVAL);
        if (maxevents > int.MaxValue / EpollEventSize) return new ValueTask<int>(-(int)Errno.EINVAL);

        var epFile = sm.GetFD(epfd);
        if (epFile == null) return new ValueTask<int>(-(int)Errno.EBADF);
        if (epFile.OpenedInode is not EpollInode epollInode) return new ValueTask<int>(-(int)Errno.EINVAL);

        var bytesNeeded = maxevents * EpollEventSize;

        var ready = TryHarvestEpollEvents(task, epollInode, eventsPtr, maxevents, bytesNeeded, out var fastPathError);
        if (fastPathError != 0)
            return new ValueTask<int>(fastPathError);
        if (ready > 0 || timeoutMs == 0)
            return new ValueTask<int>(ready);

        return DoEpollWaitSlow(task, epollInode, eventsPtr, maxevents, bytesNeeded, timeoutMs, syscallNr);
    }

    private static int TryHarvestEpollEvents(FiberTask task, EpollInode epollInode, uint eventsPtr, int maxevents,
        int bytesNeeded, out int error)
    {
        byte[]? rented = null;
        try
        {
            Span<byte> fastBuffer = bytesNeeded <= StackEpollBufferLimit
                ? stackalloc byte[bytesNeeded]
                : (rented = ArrayPool<byte>.Shared.Rent(bytesNeeded)).AsSpan(0, bytesNeeded);

            var ready = epollInode.TryHarvestNow(fastBuffer, maxevents);
            if (ready > 0 && !task.CPU.CopyToUser(eventsPtr, fastBuffer.Slice(0, ready * EpollEventSize)))
            {
                error = -(int)Errno.EFAULT;
                return 0;
            }

            error = 0;
            return ready;
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    private static async ValueTask<int> DoEpollWaitSlow(FiberTask task, EpollInode epollInode, uint eventsPtr,
        int maxevents, int bytesNeeded, int timeoutMs, uint syscallNr)
    {
        var rented = ArrayPool<byte>.Shared.Rent(bytesNeeded);
        try
        {
            var result = await epollInode.WaitAsync(task, rented, maxevents, timeoutMs, syscallNr);
            if (result > 0 && !task.CPU.CopyToUser(eventsPtr, rented.AsSpan(0, result * EpollEventSize)))
                return -(int)Errno.EFAULT;

            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }
#pragma warning restore CS1998
}
