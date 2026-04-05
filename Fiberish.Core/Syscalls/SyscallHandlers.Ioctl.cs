using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Diagnostics;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    private static readonly ILogger IoctlLogger = Logging.CreateLogger<SyscallManager>();

#pragma warning disable CS1998 // Async method lacks await operators - syscall handlers require async signature
    private async ValueTask<int> SysIoctl(Engine engine, uint fd, uint request, uint arg, uint a4, uint a5,
        uint a6)
    {
        if (!FDs.TryGetValue((int)fd, out var file)) return -(int)Errno.EBADF;

        // Delegate to the inode's Ioctl method (polymorphic dispatch)
        var inode = file.OpenedInode;
        if (inode != null)
        {
            IoctlLogger.LogTrace("[IoctlDispatch] fd={Fd} req=0x{Request:X} arg=0x{Arg:X} inode={InodeType}",
                fd, request, arg, inode.GetType().Name);
            var task = engine.Owner as FiberTask;
            if (task == null)
                return -(int)Errno.EPERM;
            var ret = inode.Ioctl(file, task, request, arg);
            IoctlLogger.LogTrace("[IoctlDispatch] fd={Fd} req=0x{Request:X} inode={InodeType} ret={Ret}",
                fd, request, inode.GetType().Name, ret);
            return ret;
        }

        return -(int)Errno.ENOTTY;
    }

    private async ValueTask<int> SysFcntl64(Engine engine, uint fd, uint cmd, uint arg, uint a4, uint a5, uint a6)
    {
        var targetFd = (int)fd;
        if (!FDs.TryGetValue(targetFd, out var file)) return -(int)Errno.EBADF;

        const uint F_DUPFD = 0;
        const uint F_GETFD = 1;
        const uint F_SETFD = 2;
        const uint F_GETFL = 3;
        const uint F_SETFL = 4;
        const uint F_GETLK = 5;
        const uint F_SETLK = 6;
        const uint F_SETLKW = 7;
        const uint F_GETLK64 = 12;
        const uint F_SETLK64 = 13;
        const uint F_SETLKW64 = 14;
        const uint F_DUPFD_CLOEXEC = 1030;
        const uint F_ADD_SEALS = 1033;
        const uint F_GET_SEALS = 1034;

        const uint FD_CLOEXEC = 1;
        const int O_ASYNC = 0x2000;
        const short F_UNLCK = 2;

        return cmd switch
        {
            F_DUPFD => DupFD(targetFd, (int)arg),
            F_GETFD => IsFdCloseOnExec(targetFd) ? (int)FD_CLOEXEC : 0,
            F_SETFD => SetFdFlags(this, targetFd, arg),
            F_GETFL => (int)(file.Flags & ~FileFlags.O_CLOEXEC),
            F_SETFL => SetStatusFlags(file, arg),
            F_GETLK => WriteUnlockedFlock(engine, arg, false),
            F_SETLK => 0,
            F_SETLKW => 0,
            F_GETLK64 => WriteUnlockedFlock(engine, arg, true),
            F_SETLK64 => 0,
            F_SETLKW64 => 0,
            F_DUPFD_CLOEXEC => DupFD(targetFd, (int)arg, true),
            F_ADD_SEALS => AddSeals(file, arg),
            F_GET_SEALS => GetSeals(file),
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

        static int WriteUnlockedFlock(Engine engine, uint arg, bool is64)
        {
            if (arg == 0) return -(int)Errno.EFAULT;

            Span<byte> flock = stackalloc byte[is64 ? 24 : 16];
            BinaryPrimitives.WriteInt16LittleEndian(flock, F_UNLCK);
            return engine.CopyToUser(arg, flock) ? 0 : -(int)Errno.EFAULT;
        }

        static int AddSeals(LinuxFile file, uint arg)
        {
            return file.OpenedInode is TmpfsInode tmpfsInode
                ? tmpfsInode.AddSeals(arg)
                : -(int)Errno.EINVAL;
        }

        static int GetSeals(LinuxFile file)
        {
            return file.OpenedInode is TmpfsInode tmpfsInode
                ? tmpfsInode.GetSeals()
                : -(int)Errno.EINVAL;
        }
    }
}