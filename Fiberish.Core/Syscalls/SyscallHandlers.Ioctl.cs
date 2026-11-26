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
    private async ValueTask<int> SysIoctl(Engine engine, uint fd, uint request, ulong arg, uint a4, uint a5,
        uint a6)
    {
        if (!FDs.TryGetValue((int)fd, out var file)) return -(int)Errno.EBADF;
        if (file.IsPathOnly) return -(int)Errno.EBADF;

        // Delegate to the inode's Ioctl method (polymorphic dispatch)
        var inode = file.IoInode;
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

    private async ValueTask<int> SysFcntl64(Engine engine, uint fd, uint cmd, ulong arg, uint a4, uint a5, uint a6)
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
        const uint F_SETOWN = 8;
        const uint F_GETOWN = 9;
        const uint F_SETSIG = 10;
        const uint F_GETSIG = 11;
        const uint F_GETLK64 = 12;
        const uint F_SETLK64 = 13;
        const uint F_SETLKW64 = 14;
        const uint F_SETOWN_EX = 15;
        const uint F_GETOWN_EX = 16;
        const uint F_OFD_GETLK = 36;
        const uint F_OFD_SETLK = 37;
        const uint F_OFD_SETLKW = 38;
        const uint F_GETLEASE = 1025;
        const uint F_SETLEASE = 1024;
        const uint F_NOTIFY = 1026;
        const uint F_DUPFD_CLOEXEC = 1030;
        const uint F_SETPIPE_SZ = 1031;
        const uint F_GETPIPE_SZ = 1032;
        const uint F_ADD_SEALS = 1033;
        const uint F_GET_SEALS = 1034;

        if (file.IsPathOnly && cmd is not (F_DUPFD or F_DUPFD_CLOEXEC or F_GETFD or F_SETFD or F_GETFL))
            return -(int)Errno.EBADF;

        const uint FD_CLOEXEC = 1;
        const int O_ASYNC = 0x2000;

        switch (cmd)
        {
            case F_DUPFD:
                return DupFD(targetFd, (int)arg);
            case F_GETFD:
                return IsFdCloseOnExec(targetFd) ? (int)FD_CLOEXEC : 0;
            case F_SETFD:
                return SetFdFlags(this, targetFd, (uint)arg);
            case F_GETFL:
                var flags = (uint)(file.Flags & ~FileFlags.O_CLOEXEC);
                return (int)(engine.IsAArch64 ? TranslateAArch64OpenFlags(flags) : flags);
            case F_SETFL:
                return SetStatusFlags(file, engine.IsAArch64 ? TranslateAArch64OpenFlags((uint)arg) : (uint)arg);
            case F_SETOWN:
                return SetOwn(file, unchecked((int)arg));
            case F_GETOWN:
                return GetOwn(file);
            case F_SETSIG:
                return SetSig(file, unchecked((int)arg));
            case F_GETSIG:
                return file.AsyncSignal;
            case F_SETOWN_EX:
                return SetOwnEx(engine, file, arg);
            case F_GETOWN_EX:
                return GetOwnEx(engine, file, arg);
            case F_GETLK:
            case F_SETLK:
            case F_SETLKW:
            case F_GETLK64:
            case F_SETLK64:
            case F_SETLKW64:
            case F_OFD_GETLK:
            case F_OFD_SETLK:
            case F_OFD_SETLKW:
                return await HandleRecordLockAsync(engine, file, cmd, arg);
            case F_GETLEASE:
                return file.OpenedInode?.GetLease() ?? -(int)Errno.EBADF;
            case F_SETLEASE:
            {
                if (file.OpenedInode == null) return -(int)Errno.EBADF;
                var task = engine.Owner as FiberTask;
                return file.OpenedInode.GetLockTargetInode()
                    .TrySetLease(file, task?.Process ?? CurrentProcess, unchecked((int)arg));
            }
            case F_DUPFD_CLOEXEC:
                return DupFD(targetFd, (int)arg, true);
            case F_NOTIFY:
            {
                var inode = file.OpenedInode;
                if (inode == null) return -(int)Errno.EBADF;
                if (inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

                var mask = unchecked((int)arg);
                const int ValidDnMask = 0x3F; // DN_ACCESS..DN_ATTRIB
                const int DnMultishot = unchecked((int)0x80000000);
                if ((mask & ~(ValidDnMask | DnMultishot)) != 0)
                    return -(int)Errno.EINVAL;
                if ((file.Flags & FileFlags.O_ACCMODE) == FileFlags.O_WRONLY)
                    return -(int)Errno.EINVAL;

                var ownerTask = engine.Owner as FiberTask;
                file.DnotifyMask = mask;
                file.DnotifyFd = targetFd;
                file.DnotifyPid = ownerTask?.Process.TGID ?? CurrentProcess?.TGID ?? 0;
                file.DnotifyScheduler = ownerTask?.CommonKernel ?? CurrentTask?.CommonKernel;
                if (mask == 0)
                    inode.RemoveDnotifyWatcher(file);
                else
                    inode.AddDnotifyWatcher(file);
                return 0;
            }
            case F_GETPIPE_SZ:
                return file.IoInode is PipeInode getPipe
                    ? getPipe.GetPipeSize()
                    : -(int)Errno.EBADF;
            case F_SETPIPE_SZ:
            {
                if (file.IoInode is not PipeInode setPipe)
                    return -(int)Errno.EBADF;
                var task = engine.Owner as FiberTask;
                var isPrivileged = task?.Process == null || task.Process.HasEffectiveCapability(Process.CapabilitySysResource);
                return setPipe.SetPipeSize(unchecked((int)arg), isPrivileged);
            }
            case F_ADD_SEALS:
                return AddSeals(file, (uint)arg);
            case F_GET_SEALS:
                return GetSeals(file);
            default:
                return -(int)Errno.EINVAL;
        }

        static int SetFdFlags(SyscallManager sm, int fd, uint arg)
        {
            sm.SetFdCloseOnExec(fd, (arg & FD_CLOEXEC) != 0);
            return 0;
        }

        static int SetStatusFlags(LinuxFile file, ulong arg)
        {
            // Linux: F_SETFL only updates a subset of status flags.
            var settableMask = FileFlags.O_APPEND | FileFlags.O_NONBLOCK | (FileFlags)O_ASYNC;
            var newStatusBits = (FileFlags)(int)arg & settableMask;
            file.Flags = (file.Flags & ~settableMask) | newStatusBits;
            if (file.IoInode is PipeInode pipe) pipe.RefreshAsyncReader(file);
            return 0;
        }

        static int SetOwn(LinuxFile file, int pid)
        {
            if (pid < 0)
            {
                if (pid == int.MinValue)
                    return -(int)Errno.EINVAL;
                file.AsyncOwnerType = 2; // F_OWNER_PGRP
                file.AsyncOwnerId = -pid;
            }
            else
            {
                file.AsyncOwnerType = 1; // F_OWNER_PID
                file.AsyncOwnerId = pid;
            }
            return 0;
        }

        static int GetOwn(LinuxFile file)
        {
            return file.AsyncOwnerType == 2 ? -file.AsyncOwnerId : file.AsyncOwnerId;
        }

        static int SetSig(LinuxFile file, int sig)
        {
            if (sig < 0 || sig > 64)
                return -(int)Errno.EINVAL;
            file.AsyncSignal = sig;
            return 0;
        }

        static int SetOwnEx(Engine engine, LinuxFile file, ulong arg)
        {
            if (arg == 0) return -(int)Errno.EFAULT;
            Span<byte> buf = stackalloc byte[8];
            if (!engine.CopyFromUser(arg, buf))
                return -(int)Errno.EFAULT;

            var type = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(0, 4));
            var pid = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(4, 4));
            if (type is < 0 or > 2 || pid < 0)
                return -(int)Errno.EINVAL;

            file.AsyncOwnerType = type;
            file.AsyncOwnerId = pid;
            return 0;
        }

        static int GetOwnEx(Engine engine, LinuxFile file, ulong arg)
        {
            if (arg == 0) return -(int)Errno.EFAULT;
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(0, 4), file.AsyncOwnerType);
            BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(4, 4), file.AsyncOwnerId);
            return engine.CopyToUser(arg, buf) ? 0 : -(int)Errno.EFAULT;
        }

        async ValueTask<int> HandleRecordLockAsync(Engine eng, LinuxFile targetFile, uint lockCmd, ulong lockArg)
        {
            if (lockArg == 0)
                return -(int)Errno.EFAULT;
            if (targetFile.OpenedInode == null)
                return -(int)Errno.EBADF;

            var isOfd = lockCmd is F_OFD_GETLK or F_OFD_SETLK or F_OFD_SETLKW;
            var isAArch64 = eng.IsAArch64;
            var is64 = isAArch64 || isOfd || lockCmd is F_GETLK64 or F_SETLK64 or F_SETLKW64;
            var isGet = lockCmd is F_GETLK or F_GETLK64 or F_OFD_GETLK;
            var isBlocking = lockCmd is F_SETLKW or F_SETLKW64 or F_OFD_SETLKW;

            var longStart = isAArch64 ? 8 : 4;
            Span<byte> raw = stackalloc byte[isAArch64 ? 32 : is64 ? 24 : 16];
            if (!eng.CopyFromUser(lockArg, raw))
                return -(int)Errno.EFAULT;

            var lType = BinaryPrimitives.ReadInt16LittleEndian(raw.Slice(0, 2));
            var lWhence = BinaryPrimitives.ReadInt16LittleEndian(raw.Slice(2, 2));
            long lStart;
            long lLen;
            int lPid;
            if (is64)
            {
                lStart = BinaryPrimitives.ReadInt64LittleEndian(raw.Slice(longStart, 8));
                lLen = BinaryPrimitives.ReadInt64LittleEndian(raw.Slice(longStart + 8, 8));
                lPid = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(longStart + 16, 4));
            }
            else
            {
                lStart = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(4, 4));
                lLen = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(8, 4));
                lPid = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(12, 4));
            }

            if (lType is < FileLockState.F_RDLCK or > FileLockState.F_UNLCK)
                return -(int)Errno.EINVAL;
            if (isGet && lType == FileLockState.F_UNLCK)
                return -(int)Errno.EINVAL;
            if (isOfd && lPid != 0)
                return -(int)Errno.EINVAL;

            long baseOffset;
            switch (lWhence)
            {
                case 0: // SEEK_SET
                    baseOffset = 0;
                    break;
                case 1: // SEEK_CUR
                    baseOffset = targetFile.Position;
                    break;
                case 2: // SEEK_END
                    baseOffset = (long)targetFile.OpenedInode.Size;
                    break;
                default:
                    return -(int)Errno.EINVAL;
            }

            long startPos;
            try
            {
                startPos = checked(baseOffset + lStart);
            }
            catch (OverflowException)
            {
                return -(int)Errno.EOVERFLOW;
            }

            long rangeStart;
            long rangeEnd;
            if (lLen > 0)
            {
                rangeStart = startPos;
                try
                {
                    rangeEnd = checked(startPos + lLen - 1);
                }
                catch (OverflowException)
                {
                    return -(int)Errno.EOVERFLOW;
                }
            }
            else if (lLen == 0)
            {
                rangeStart = startPos;
                rangeEnd = long.MaxValue;
            }
            else
            {
                try
                {
                    rangeStart = checked(startPos + lLen);
                    rangeEnd = checked(startPos - 1);
                }
                catch (OverflowException)
                {
                    return -(int)Errno.EOVERFLOW;
                }
            }

            if (rangeStart < 0)
                return -(int)Errno.EINVAL;
            if (!is64 && (rangeStart > int.MaxValue || (rangeEnd != long.MaxValue && rangeEnd > int.MaxValue)))
                return -(int)Errno.EOVERFLOW;

            var task = eng.Owner as FiberTask;
            var process = task?.Process ?? CurrentProcess;
            object ownerKey = isOfd ? targetFile : (object?)process ?? targetFile;
            var ownerPid = isOfd ? -1 : process?.TGID ?? 0;
            var lockState = targetFile.OpenedInode.GetLockTargetInode().GetOrCreateFileLockState();

            if (isGet)
            {
                var query = lockState.GetLock(ownerKey, lType, rangeStart, rangeEnd);
                if (!query.HasConflict)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(raw.Slice(0, 2), FileLockState.F_UNLCK);
                }
                else
                {
                    BinaryPrimitives.WriteInt16LittleEndian(raw.Slice(0, 2), query.Type);
                    BinaryPrimitives.WriteInt16LittleEndian(raw.Slice(2, 2), 0); // SEEK_SET
                    if (is64)
                    {
                        BinaryPrimitives.WriteInt64LittleEndian(raw.Slice(longStart, 8), query.Start);
                        BinaryPrimitives.WriteInt64LittleEndian(raw.Slice(longStart + 8, 8), query.Length);
                        BinaryPrimitives.WriteInt32LittleEndian(raw.Slice(longStart + 16, 4), query.Pid);
                    }
                    else
                    {
                        if (query.Start > int.MaxValue || query.Length > int.MaxValue)
                            return -(int)Errno.EOVERFLOW;
                        BinaryPrimitives.WriteInt32LittleEndian(raw.Slice(4, 4), (int)query.Start);
                        BinaryPrimitives.WriteInt32LittleEndian(raw.Slice(8, 4), (int)query.Length);
                        BinaryPrimitives.WriteInt32LittleEndian(raw.Slice(12, 4), query.Pid);
                    }
                }

                return eng.CopyToUser(lockArg, raw) ? 0 : -(int)Errno.EFAULT;
            }

            var accessMode = targetFile.Flags & FileFlags.O_ACCMODE;
            if (lType == FileLockState.F_RDLCK && accessMode == FileFlags.O_WRONLY)
                return -(int)Errno.EBADF;
            if (lType == FileLockState.F_WRLCK && accessMode == FileFlags.O_RDONLY)
                return -(int)Errno.EBADF;

            return await lockState.SetLockAsync(ownerKey, ownerPid, lType, rangeStart, rangeEnd, isBlocking, task);
        }

        static int AddSeals(LinuxFile file, uint arg)
        {
            if ((file.Flags & FileFlags.O_RDWR) == 0) return -(int)Errno.EPERM;
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
