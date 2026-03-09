using Fiberish.Diagnostics;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    private static readonly ILogger IoctlLogger = Logging.CreateLogger<SyscallManager>();

#pragma warning disable CS1998 // Async method lacks await operators - syscall handlers require async signature
    private static async ValueTask<int> SysIoctl(IntPtr state, uint fd, uint request, uint arg, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        if (!sm.FDs.TryGetValue((int)fd, out var file)) return -(int)Errno.EBADF;

        // Delegate to the inode's Ioctl method (polymorphic dispatch)
        var inode = file.OpenedInode;
        if (inode != null)
        {
            IoctlLogger.LogTrace("[IoctlDispatch] fd={Fd} req=0x{Request:X} arg=0x{Arg:X} inode={InodeType}",
                fd, request, arg, inode.GetType().Name);
            var ret = inode.Ioctl(file, request, arg, sm.Engine);
            IoctlLogger.LogTrace("[IoctlDispatch] fd={Fd} req=0x{Request:X} inode={InodeType} ret={Ret}",
                fd, request, inode.GetType().Name, ret);
            return ret;
        }

        return -(int)Errno.ENOTTY;
    }

    private static async ValueTask<int> SysFcntl64(IntPtr state, uint fd, uint cmd, uint arg, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var targetFd = (int)fd;
        if (!sm.FDs.TryGetValue(targetFd, out var file)) return -(int)Errno.EBADF;

        const uint F_DUPFD = 0;
        const uint F_GETFD = 1;
        const uint F_SETFD = 2;
        const uint F_GETFL = 3;
        const uint F_SETFL = 4;
        const uint F_DUPFD_CLOEXEC = 1030;

        const uint FD_CLOEXEC = 1;
        const int O_ASYNC = 0x2000;

        return cmd switch
        {
            F_DUPFD => sm.DupFD(targetFd, (int)arg, closeOnExec: false),
            F_GETFD => sm.IsFdCloseOnExec(targetFd) ? (int)FD_CLOEXEC : 0,
            F_SETFD => SetFdFlags(sm, targetFd, arg),
            F_GETFL => (int)(file.Flags & ~FileFlags.O_CLOEXEC),
            F_SETFL => SetStatusFlags(file, arg),
            F_DUPFD_CLOEXEC => sm.DupFD(targetFd, (int)arg, closeOnExec: true),
            _ => -(int)Errno.EINVAL
        };

        static int SetFdFlags(SyscallManager sm, int fd, uint arg)
        {
            sm.SetFdCloseOnExec(fd, (arg & FD_CLOEXEC) != 0);
            return 0;
        }

        static int SetStatusFlags(LinuxFile file, uint arg)
        {
            // Linux: F_SETFL only updates a subset of status flags.
            var settableMask = FileFlags.O_APPEND | FileFlags.O_NONBLOCK | (FileFlags)O_ASYNC;
            var newStatusBits = (FileFlags)(int)arg & settableMask;
            file.Flags = (file.Flags & ~settableMask) | newStatusBits;
            return 0;
        }

    }
}
