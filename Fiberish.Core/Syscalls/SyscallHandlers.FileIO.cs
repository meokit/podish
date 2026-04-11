using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fiberish.Auth.Permission;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998 // Async method lacks await operators
    internal async ValueTask<int> SysSendfile(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // ssize_t sendfile(int out_fd, int in_fd, off_t *offset, size_t count);

        var outFd = (int)a1;
        var inFd = (int)a2;
        var offsetPtr = a3;
        var count = (int)a4;

        long? offset = null;
        if (offsetPtr != 0)
        {
            var offsetBytes = new byte[4];
            if (!engine.CopyFromUser(offsetPtr, offsetBytes)) return -(int)Errno.EFAULT;
            offset = BitConverter.ToInt32(offsetBytes);
        }

        var (result, newOffset) = await DoSendfile(this, engine, outFd, inFd, offset, count);

        if (result >= 0 && offsetPtr != 0 && offset.HasValue)
            if (!engine.CopyToUser(offsetPtr, BitConverter.GetBytes((uint)offset.Value)))
                return -(int)Errno.EFAULT;

        return result;
    }

    internal async ValueTask<int> SysSendfile64(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        // ssize_t sendfile64(int out_fd, int in_fd, off64_t *offset, size_t count);

        var outFd = (int)a1;
        var inFd = (int)a2;
        var offsetPtr = a3;
        var count = (int)a4;

        long? offset = null;
        if (offsetPtr != 0)
        {
            var offsetBytes = new byte[8];
            if (!engine.CopyFromUser(offsetPtr, offsetBytes)) return -(int)Errno.EFAULT;
            offset = BitConverter.ToInt64(offsetBytes);
        }

        var (result, newOffset) = await DoSendfile(this, engine, outFd, inFd, offset, count);

        if (result >= 0 && offsetPtr != 0 && newOffset.HasValue)
            if (!engine.CopyToUser(offsetPtr, BitConverter.GetBytes(newOffset.Value)))
                return -(int)Errno.EFAULT;

        return result;
    }

    private static async ValueTask<(int, long?)> DoSendfile(SyscallManager sm, Engine engine, int outFd, int inFd,
        long? offset, int count)
    {
        if (!sm.FDs.TryGetValue(inFd, out var inFile) || !sm.FDs.TryGetValue(outFd, out var outFile))
            return (-(int)Errno.EBADF, null);
        if (inFile == null || outFile == null) return (-(int)Errno.EBADF, null);
        var task = engine.Owner as FiberTask;

        // Verify modes
        const int O_ACCMODE = 3;
        if (((int)inFile.Flags & O_ACCMODE) == (int)FileFlags.O_WRONLY) return (-(int)Errno.EBADF, null);
        if (((int)outFile.Flags & O_ACCMODE) == (int)FileFlags.O_RDONLY) return (-(int)Errno.EBADF, null);

        // Use a buffer
        var bufLen = Math.Min(count, 32768);
        var buffer = ArrayPool<byte>.Shared.Rent(bufLen);
        var totalWritten = 0;

        try
        {
            var remaining = count;
            var readOffset = offset ?? inFile.Position;

            while (remaining > 0)
            {
                var toRead = Math.Min(remaining, bufLen);
                var bytesRead = inFile.OpenedInode!.ReadToHost(task, inFile, buffer.AsSpan(0, toRead), readOffset);

                if (bytesRead <= 0)
                {
                    if (bytesRead == 0) // EOF
                        break;
                    if (bytesRead == -(int)Errno.EAGAIN)
                    {
                        if (totalWritten > 0) break;
                        if ((inFile.Flags & FileFlags.O_NONBLOCK) != 0) return (-(int)Errno.EAGAIN, null);
                        if (task == null) return (-(int)Errno.EAGAIN, null);
                        if (await new IOAwaitable(inFile, true, task) == AwaitResult.Interrupted)
                            return (-(int)Errno.ERESTARTSYS, null);
                        continue;
                    }

                    if (totalWritten > 0)
                        break;
                    return (bytesRead, null); // Return error code
                }

                if (!offset.HasValue) inFile.Position += bytesRead;

                // Write to out_fd
                var bytesWritten =
                    outFile.OpenedInode!.WriteFromHost(task, outFile, buffer.AsSpan(0, bytesRead), outFile.Position);

                if (bytesWritten < 0)
                {
                    if (bytesWritten == -(int)Errno.EAGAIN)
                    {
                        if (totalWritten > 0) break;
                        if ((outFile.Flags & FileFlags.O_NONBLOCK) != 0) return (-(int)Errno.EAGAIN, null);
                        if (task != null)
                        {
                            if (await new IOAwaitable(outFile, false, task) == AwaitResult.Interrupted)
                                // We read data but were interrupted before writing.
                                // If we don't handle this perfectly, bytes are technically lost. For now, return ERESTARTSYS.
                                return (-(int)Errno.ERESTARTSYS, null);

                            // Unwind the read loop since we need to retry write, but we already advanced the read position!
                            // This gets complex. But wait, if we got EAGAIN on write, we've already consumed read.
                            // In real Linux, sendfile might block on write, so we should just continue writing what we read.
                            // For simplicity, we just rewind the input position and break/retry.
                            if (!offset.HasValue) inFile.Position -= bytesRead;
                            continue;
                        }
                    }

                    if (totalWritten > 0) break;

                    return (bytesWritten, null);
                }

                if (bytesWritten > 0) outFile.Position += bytesWritten;

                totalWritten += bytesWritten;
                readOffset += bytesWritten;
                remaining -= bytesWritten;

                if (bytesWritten < bytesRead) break;
            }

            if (offset.HasValue)
                offset = readOffset;

            return (totalWritten, offset);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SysSendfile64 failed");
            return (-(int)Errno.EIO, null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<int> SysPipe(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fdsAddr = a1;
        int? rFd = null;
        int? wFd = null;

        try
        {
            var pipe = new PipeInode(((FiberTask)engine.Owner!).CommonKernel);
            pipe.SuperBlock = MemfdSuperBlock;

            // Reader
            var rDentry = new Dentry(FsName.FromString("pipe:[read]"), pipe, null, MemfdSuperBlock);
            var rFile = new LinuxFile(rDentry, FileFlags.O_RDONLY, AnonMount);
            rFd = AllocFD(rFile);
            // pipe.AddReader(); // Handled by File ctor -> Inode.Open

            // Writer
            var wDentry = new Dentry(FsName.FromString("pipe:[write]"), pipe, null, MemfdSuperBlock);
            var wFile = new LinuxFile(wDentry, FileFlags.O_WRONLY, AnonMount);
            wFd = AllocFD(wFile);
            // pipe.AddWriter(); // Handled by File ctor -> Inode.Open

            // Write FDs to user memory
            var fds = new[] { rFd.Value, wFd.Value };
            if (!engine.CopyToUser(fdsAddr, MemoryMarshal.AsBytes(fds.AsSpan())))
            {
                if (rFd.HasValue) FreeFD(rFd.Value);
                if (wFd.HasValue) FreeFD(wFd.Value);
                return -(int)Errno.EFAULT;
            }

            return 0;
        }
        catch (OutOfMemoryException)
        {
            if (rFd.HasValue) FreeFD(rFd.Value);
            if (wFd.HasValue) FreeFD(wFd.Value);
            return -(int)Errno.ENOMEM;
        }
        catch (Exception ex)
        {
            if (rFd.HasValue) FreeFD(rFd.Value);
            if (wFd.HasValue) FreeFD(wFd.Value);
            Logger.LogError(ex, "SysPipe failed");
            return -(int)Errno.ENFILE;
        }
    }

    private async ValueTask<int> SysPipe2(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fdsAddr = a1;
        var flags = (FileFlags)a2;

        // Validate flags - only O_CLOEXEC and O_NONBLOCK are supported
        // O_DIRECT (0x4000) is not currently supported
        const FileFlags SupportedFlags = FileFlags.O_CLOEXEC | FileFlags.O_NONBLOCK;
        if ((flags & ~SupportedFlags) != 0)
            return -(int)Errno.EINVAL;

        int? rFd = null;
        int? wFd = null;

        try
        {
            var pipe = new PipeInode(((FiberTask)engine.Owner!).CommonKernel);
            pipe.SuperBlock = MemfdSuperBlock;

            // Build file flags for reader and writer
            // O_NONBLOCK is stored in LinuxFile.Flags and checked by read/write syscalls
            var baseFlags = flags & SupportedFlags;

            // Reader
            var rFlags = FileFlags.O_RDONLY | baseFlags;
            var rDentry = new Dentry(FsName.FromString("pipe:[read]"), pipe, null, MemfdSuperBlock);
            var rFile = new LinuxFile(rDentry, rFlags, AnonMount);
            rFd = AllocFD(rFile);

            // Writer
            var wFlags = FileFlags.O_WRONLY | baseFlags;
            var wDentry = new Dentry(FsName.FromString("pipe:[write]"), pipe, null, MemfdSuperBlock);
            var wFile = new LinuxFile(wDentry, wFlags, AnonMount);
            wFd = AllocFD(wFile);

            // Write FDs to user memory
            var fds = new[] { rFd.Value, wFd.Value };
            if (!engine.CopyToUser(fdsAddr, MemoryMarshal.AsBytes(fds.AsSpan())))
            {
                // Rollback on EFAULT
                FreeFD(rFd.Value);
                FreeFD(wFd.Value);
                return -(int)Errno.EFAULT;
            }

            return 0;
        }
        catch (OutOfMemoryException)
        {
            // Rollback on OOM
            if (rFd.HasValue) FreeFD(rFd.Value);
            if (wFd.HasValue) FreeFD(wFd.Value);
            return -(int)Errno.ENOMEM;
        }
        catch (Exception ex)
        {
            // Rollback on any other error
            if (rFd.HasValue) FreeFD(rFd.Value);
            if (wFd.HasValue) FreeFD(wFd.Value);
            Logger.LogError(ex, "SysPipe2 failed");
            return -(int)Errno.ENFILE;
        }
    }

    private async ValueTask<int> SysCreat(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // creat(path, mode) is open(path, O_CREAT|O_WRONLY|O_TRUNC, mode)
        return await SysOpen(engine, a1, (uint)(FileFlags.O_CREAT | FileFlags.O_WRONLY | FileFlags.O_TRUNC), a2, a4, a5,
            a6);
    }

    private async ValueTask<int> SysMemfdCreate(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        const uint MFD_CLOEXEC = 0x0001;
        const uint MFD_ALLOW_SEALING = 0x0002;
        const uint MFD_NOEXEC_SEAL = 0x0008;
        const uint MFD_EXEC = 0x0010;


        var name = engine.ReadStringSafe(a1);
        if (name == null) return -(int)Errno.EFAULT;

        var flags = a2;
        var knownFlags = MFD_CLOEXEC | MFD_ALLOW_SEALING | MFD_NOEXEC_SEAL | MFD_EXEC;
        if ((flags & ~knownFlags) != 0) return -(int)Errno.EINVAL;
        if ((flags & MFD_EXEC) != 0 && (flags & MFD_NOEXEC_SEAL) != 0) return -(int)Errno.EINVAL;

        try
        {
            var t = engine.Owner as FiberTask;
            var uid = t?.Process.EUID ?? 0;
            var gid = t?.Process.EGID ?? 0;

            var display = string.IsNullOrEmpty(name) ? "memfd:anon" : $"memfd:{name}";

            var fdFlags = FileFlags.O_RDWR;
            if ((flags & MFD_CLOEXEC) != 0) fdFlags |= FileFlags.O_CLOEXEC;
            var mode = (flags & MFD_EXEC) != 0 ? 0x1C0 : 0x180; // 0700 vs 0600
            var file = MemoryContext.CreateMemfdFile(display, fdFlags, mode, uid, gid,
                (flags & MFD_ALLOW_SEALING) != 0,
                (flags & MFD_EXEC) != 0,
                (flags & MFD_NOEXEC_SEAL) != 0,
                AnonMount);
            return AllocFD(file);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SysMemfdCreate failed");
            return -(int)Errno.ENOMEM;
        }
    }

    private static int ImplOpen(SyscallManager sm, RentedUserBytes path, uint flags, uint mode,
        PathLocation startLoc = default)
    {
        return ImplOpen(sm, path.UnsafeBuffer, path.Length, flags, mode, startLoc);
    }

    private static int ImplOpen(SyscallManager sm, string path, uint flags, uint mode, PathLocation startLoc = default)
    {
        var pathBytes = FsEncoding.EncodeUtf8(path);
        return ImplOpen(sm, pathBytes, pathBytes.Length, flags, mode, startLoc);
    }

    private static int ImplOpen(SyscallManager sm, byte[] pathBytes, int pathLength, uint flags, uint mode,
        PathLocation startLoc = default)
    {
        Logger.LogInformation("[Open] Path='{Path}' Flags={Flags} Mode={Mode}",
            FsEncoding.DecodeUtf8Lossy(pathBytes.AsSpan(0, pathLength)), flags, mode);
        const uint O_TMPFILE_MASK = 0x400000;
        var createdHere = false;
        var accessMode = flags & 3u;
        var wantDirectory = ((FileFlags)flags & FileFlags.O_DIRECTORY) != 0;
        var wantCreate = ((FileFlags)flags & FileFlags.O_CREAT) != 0;
        var wantExcl = ((FileFlags)flags & FileFlags.O_EXCL) != 0;
        var wantTrunc = ((FileFlags)flags & FileFlags.O_TRUNC) != 0;
        var noFollow = ((FileFlags)flags & FileFlags.O_NOFOLLOW) != 0;
        var task = sm.CurrentTask;

        var lookupFlags = noFollow ? LookupFlags.None : LookupFlags.FollowSymlink;
        var lookupData =
            sm.PathWalker.PathWalkWithData(pathBytes, pathLength, startLoc.IsValid ? startLoc : null, lookupFlags);
        if (lookupData.HasError && lookupData.ErrorCode != -(int)Errno.ENOENT)
            return lookupData.ErrorCode;

        Dentry? dentry;
        Mount? mount;
        if (lookupData.HasError)
        {
            dentry = null;
            mount = null;
        }
        else
        {
            dentry = lookupData.Path.Dentry;
            mount = lookupData.Path.Mount;
        }

        if ((flags & O_TMPFILE_MASK) != 0)
        {
            if (dentry == null || dentry.Inode == null || dentry.Inode.Type != InodeType.Directory)
                return -(int)Errno.ENOTDIR;

            if (mount != null && mount.IsReadOnly) return -(int)Errno.EROFS;

            var t = sm.CurrentTask;
            var uid = t?.Process.EUID ?? 0;
            var gid = t?.Process.EGID ?? 0;

            var tmpName = $".tmpfile.{Guid.NewGuid():N}";
            var anonDentry = new Dentry(FsName.FromString(tmpName), null, dentry, dentry.SuperBlock);
            var finalMode = DacPolicy.ApplyUmask((int)mode, t?.Process.Umask ?? 0);
            var createRc = dentry.Inode.Create(anonDentry, finalMode, uid, gid);
            if (createRc < 0)
                return createRc;

            var openFlags = flags & ~O_TMPFILE_MASK;
            var f = new LinuxFile(anonDentry, (FileFlags)openFlags, mount ?? sm.RootMount!)
            {
                IsTmpFile = true
            };
            return sm.AllocFD(f);
        }

        if (wantDirectory && wantCreate)
            return -(int)Errno.EINVAL;

        if (dentry == null)
        {
            if (wantCreate)
            {
                var noFollowData =
                    sm.PathWalker.PathWalkWithData(pathBytes, pathLength, startLoc.IsValid ? startLoc : null,
                        LookupFlags.None);
                if (noFollowData.HasError && noFollowData.ErrorCode != -(int)Errno.ENOENT)
                    return noFollowData.ErrorCode;

                if (noFollowData.Path.Dentry?.Inode?.Type == InodeType.Symlink)
                {
                    if (wantExcl)
                        return -(int)Errno.EEXIST;

                    byte[]? targetBytes;
                    if (noFollowData.Path.Dentry.Inode.Readlink(out targetBytes) == 0 && targetBytes is { Length: > 0 })
                    {
                        byte[] createPath;
                        if (targetBytes[0] == (byte)'/')
                        {
                            createPath = targetBytes;
                        }
                        else
                        {
                            var symlinkLastSlash = Array.LastIndexOf(pathBytes, (byte)'/', pathLength - 1, pathLength);
                            var symlinkParentLength = symlinkLastSlash switch
                            {
                                < 0 => 0,
                                0 => 1,
                                _ => symlinkLastSlash
                            };
                            if (symlinkParentLength == 0)
                            {
                                createPath = targetBytes;
                            }
                            else
                            {
                                var trimmedParentLength = symlinkParentLength;
                                while (trimmedParentLength > 1 &&
                                       pathBytes[trimmedParentLength - 1] == (byte)'/')
                                    trimmedParentLength--;
                                createPath = new byte[trimmedParentLength + 1 + targetBytes.Length];
                                Array.Copy(pathBytes, 0, createPath, 0, trimmedParentLength);
                                createPath[trimmedParentLength] = (byte)'/';
                                Array.Copy(targetBytes, 0, createPath, trimmedParentLength + 1, targetBytes.Length);
                            }
                        }

                        if (!pathBytes.AsSpan(0, pathLength).SequenceEqual(createPath))
                            return ImplOpen(sm, createPath, createPath.Length, flags, mode, startLoc);
                    }
                }

                var (parentLoc, name, createErr) =
                    sm.PathWalker.PathWalkForCreate(pathBytes, pathLength, startLoc.IsValid ? startLoc : null);
                if (createErr != 0)
                    return createErr;

                var parentDentry = parentLoc.Dentry;
                var parentMount = parentLoc.Mount;
                if (parentDentry == null || parentDentry.Inode == null)
                    return -(int)Errno.ENOENT;

                // Check if parent mount is read-only (for create operation)
                if (parentMount != null && parentMount.IsReadOnly) return -(int)Errno.EROFS;

                var t = sm.CurrentTask;
                var uid = t?.Process.EUID ?? 0;
                var gid = t?.Process.EGID ?? 0;

                dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
                var createMode = DacPolicy.ApplyUmask((int)mode, t?.Process.Umask ?? 0);
                var createRc2 = parentDentry.Inode.Create(dentry, createMode, uid, gid);
                if (createRc2 < 0)
                    return createRc2;
                createdHere = true;
                mount = parentMount;
            }
            else
            {
                return -(int)Errno.ENOENT;
            }
        }
        else
        {
            if (noFollow && dentry.Inode?.Type == InodeType.Symlink)
                return -(int)Errno.ELOOP;

            if (wantDirectory && dentry.Inode?.Type != InodeType.Directory)
                return -(int)Errno.ENOTDIR;

            // File exists - check for O_EXCL
            if (wantCreate && wantExcl)
                return -(int)Errno.EEXIST;

            if (dentry.Inode?.Type == InodeType.Directory && (accessMode != 0 || wantTrunc || wantCreate))
                return -(int)Errno.EISDIR;
            if (dentry.Inode?.Type == InodeType.Socket)
                return -(int)Errno.ENXIO;
            if (dentry.Inode?.Type == InodeType.Fifo &&
                accessMode == (int)FileFlags.O_WRONLY &&
                ((FileFlags)flags & FileFlags.O_NONBLOCK) != 0)
                return -(int)Errno.ENXIO;

            if (task?.Process != null && dentry.Inode != null)
            {
                var requested = AccessMode.None;
                if (accessMode == 0)
                    requested |= AccessMode.MayRead;
                else if (accessMode == 1)
                    requested |= AccessMode.MayWrite;
                else if (accessMode == 2)
                    requested |= AccessMode.MayRead | AccessMode.MayWrite;
                if (wantTrunc)
                    requested |= AccessMode.MayWrite;

                var accessRc = DacPolicy.CheckPathAccess(task.Process, dentry.Inode, requested, true);
                if (accessRc < 0)
                    return accessRc;
            }

            // Check mount read-only for write operations on existing file
            if (accessMode != 0 || wantTrunc) // O_WRONLY or O_RDWR or O_TRUNC
                if (mount != null && mount.IsReadOnly)
                    return -(int)Errno.EROFS;
        }

        try
        {
            if (!createdHere &&
                (flags & (uint)FileFlags.O_TRUNC) != 0 &&
                dentry?.Inode?.Type == InodeType.File)
            {
                var truncateRc = dentry.Inode.Truncate(0);
                if (truncateRc < 0) return truncateRc;
                ProcessAddressSpaceSync.NotifyInodeTruncated(sm.Mem, sm.CurrentSyscallEngine, dentry.Inode, 0);
                flags &= ~(uint)FileFlags.O_TRUNC;
            }

            // If we already created the inode above, opening with O_CREAT|O_EXCL can
            // retrigger create semantics in backend Open() and fail spuriously.
            var openFlags = createdHere
                ? flags & ~(uint)(FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC)
                : flags;
            if (dentry == null) return -(int)Errno.ENOENT;
            var f = new LinuxFile(dentry, (FileFlags)openFlags, mount ?? sm.RootMount!);
            if (task != null && f.OpenedInode is ITaskContextBoundInode taskBoundInode)
                taskBoundInode.BindTaskContext(f, task);
            return sm.AllocFD(f);
        }
        catch (Exception ex)
        {
            Logger.LogInformation(ex, "[Open] final open failed path='{Path}' flags={Flags} mode={Mode}",
                FsEncoding.DecodeUtf8Lossy(pathBytes.AsSpan(0, pathLength)), flags, mode);
            return MapSyscallExceptionToErrno(ex);
        }
    }

    private async ValueTask<int> SysOpen(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var pathErr = ReadPathArgumentBytes(a1, out var path);
        if (pathErr != 0) return pathErr;
        using var _ = path;

        return ImplOpen(this, path, a2, a3);
    }

    private async ValueTask<int> SysOpenAt(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var dfd = (int)a1;
        var pathErr = ReadPathArgumentBytes(a2, out var path);
        if (pathErr != 0) return pathErr;
        using var _ = path;

        var startLoc = PathLocation.None;
        if (dfd == -100) // AT_FDCWD
        {
            startLoc = CurrentWorkingDirectory;
        }
        else
        {
            var f = GetFD(dfd);
            if (f != null) startLoc = f.LivePath;
            else return -(int)Errno.EBADF;
        }

        return ImplOpen(this, path, a3, a4, startLoc);
    }

    private async ValueTask<int> SysOpenAt2(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var dirfd = (int)a1;
        var pathErr = ReadPathArgumentBytes(a2, out var path);
        if (pathErr != 0) return pathErr;
        using var _ = path;

        var howPtr = a3;
        var howSize = a4;

        if (howSize < 24) return -(int)Errno.EINVAL;

        var howBuf = new byte[24];
        if (!engine.CopyFromUser(howPtr, howBuf)) return -(int)Errno.EFAULT;

        var flags = BinaryPrimitives.ReadUInt64LittleEndian(howBuf.AsSpan(0, 8));
        var mode = BinaryPrimitives.ReadUInt64LittleEndian(howBuf.AsSpan(8, 8));

        PathLocation startAt = default;
        if (dirfd != -100 && !path.IsAbsolute)
        {
            var fdir = GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.LivePath;
        }

        return ImplOpen(this, path, (uint)flags, (uint)mode, startAt);
    }

    private async ValueTask<int> SysDup(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var oldfd = (int)a1;
        var f = GetFD(oldfd);
        if (f == null) return -(int)Errno.EBADF;

        return DupFD(f);
    }

    private async ValueTask<int> SysDup2(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var oldfd = (int)a1;
        var newfd = (int)a2;

        if (oldfd == newfd)
        {
            if (GetFD(oldfd) == null) return -(int)Errno.EBADF;
            return newfd;
        }

        var f = GetFD(oldfd);
        if (f == null) return -(int)Errno.EBADF;

        FreeFD(newfd);
        FDs[newfd] = f;
        f.Get();
        SetFdCloseOnExec(newfd, false);
        return newfd;
    }

    private async ValueTask<int> SysDup3(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var oldfd = (int)a1;
        var newfd = (int)a2;
        var flags = (int)a3;
        const int O_CLOEXEC = (int)FileFlags.O_CLOEXEC;

        if ((flags & ~O_CLOEXEC) != 0) return -(int)Errno.EINVAL;
        if (oldfd == newfd) return -(int)Errno.EINVAL;

        var rc = await SysDup2(engine, a1, a2, 0, 0, 0, 0);
        if (rc < 0) return rc;
        SetFdCloseOnExec(newfd, (flags & O_CLOEXEC) != 0);
        return rc;
    }

    private static async ValueTask<int> DoReadV(SyscallManager sm, Engine engine, int fd, Iovec[] iovs, int iovCnt,
        long offset, int flags)
    {
        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;
        var task = engine.Owner as FiberTask;
        const int O_ACCMODE = 3;
        if (((int)f.Flags & O_ACCMODE) == (int)FileFlags.O_WRONLY)
            return -(int)Errno.EBADF;

        if (f.OpenedInode == null)
            return -(int)Errno.EINVAL;

        var iovList = new ArraySegment<Iovec>(iovs, 0, iovCnt);
        return await f.OpenedInode.ReadV(engine, f, task, iovList, offset, flags);
    }

    private static async ValueTask<int> DoWriteV(SyscallManager sm, Engine engine, int fd, Iovec[] iovs, int iovCnt,
        long offset, int flags)
    {
        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;
        var task = engine.Owner as FiberTask;

        // Check mount read-only status (like Linux mnt_want_write)
        var wantWrite = f.WantWrite();
        if (wantWrite < 0) return wantWrite;

        if (f.OpenedInode == null)
            return -(int)Errno.EINVAL;

        var writeStartOffset = offset == -1 ? f.Position : offset;
        var iovList = new ArraySegment<Iovec>(iovs, 0, iovCnt);
        var rc = await f.OpenedInode.WriteV(engine, f, task, iovList, offset, flags);

        if (rc > 0 && f.OpenedInode is Inode inode)
        {
            ProcessAddressSpaceSync.NotifyFileContentChanged(sm.Mem, engine, inode, writeStartOffset, rc);

            // Truncation check happens natively on writing but if we need ProcessAddressSpaceSync.NotifyInodeTruncated:
            var sizeAfterWrite = (long)inode.Size;
            ProcessAddressSpaceSync.NotifyInodeTruncated(sm.Mem, engine, inode, sizeAfterWrite);
        }

        return rc;
    }

    private static Iovec[]? ReadIovecs(SyscallManager sm, Engine engine, uint iovAddr, int iovCnt)
    {
        if (iovCnt < 0 || iovCnt > 1024) return null;
        var iovs = ArrayPool<Iovec>.Shared.Rent(iovCnt);
        var iovecBytes = ArrayPool<byte>.Shared.Rent(iovCnt * 8);

        try
        {
            if (!engine.CopyFromUser(iovAddr, iovecBytes.AsSpan(0, iovCnt * 8))) return null;

            for (var i = 0; i < iovCnt; i++)
                iovs[i] = new Iovec
                {
                    BaseAddr = BinaryPrimitives.ReadUInt32LittleEndian(iovecBytes.AsSpan(i * 8, 4)),
                    Len = BinaryPrimitives.ReadUInt32LittleEndian(iovecBytes.AsSpan(i * 8 + 4, 4))
                };
            return iovs;
        }
        catch
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(iovecBytes);
        }
    }

    internal async ValueTask<int> SysRead(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var iovs = ArrayPool<Iovec>.Shared.Rent(1);
        iovs[0] = new Iovec { BaseAddr = a2, Len = a3 };
        try
        {
            return await DoReadV(this, engine, (int)a1, iovs, 1, -1, 0);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    internal async ValueTask<int> SysWrite(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var iovs = ArrayPool<Iovec>.Shared.Rent(1);
        iovs[0] = new Iovec { BaseAddr = a2, Len = a3 };
        try
        {
            return await DoWriteV(this, engine, (int)a1, iovs, 1, -1, 0);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private async ValueTask<int> SysPRead(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var offset = a4 | ((long)a5 << 32);
        var iovs = ArrayPool<Iovec>.Shared.Rent(1);
        iovs[0] = new Iovec { BaseAddr = a2, Len = a3 };
        try
        {
            return await DoReadV(this, engine, (int)a1, iovs, 1, offset, 0);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private async ValueTask<int> SysPWrite(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var offset = a4 | ((long)a5 << 32);
        var iovs = ArrayPool<Iovec>.Shared.Rent(1);
        iovs[0] = new Iovec { BaseAddr = a2, Len = a3 };
        try
        {
            return await DoWriteV(this, engine, (int)a1, iovs, 1, offset, 0);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private async ValueTask<int> SysReadV(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var iovs = ReadIovecs(this, engine, a2, (int)a3);
        if (iovs == null) return -(int)Errno.EFAULT; // Simplification, could be EINVAL
        try
        {
            return await DoReadV(this, engine, (int)a1, iovs, (int)a3, -1, 0);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private async ValueTask<int> SysWriteV(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var iovs = ReadIovecs(this, engine, a2, (int)a3);
        if (iovs == null) return -(int)Errno.EFAULT;
        try
        {
            return await DoWriteV(this, engine, (int)a1, iovs, (int)a3, -1, 0);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private async ValueTask<int> SysPReadV(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var offset = a4 | ((long)a5 << 32);
        var iovs = ReadIovecs(this, engine, a2, (int)a3);
        if (iovs == null) return -(int)Errno.EFAULT;
        try
        {
            return await DoReadV(this, engine, (int)a1, iovs, (int)a3, offset, 0);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private async ValueTask<int> SysPWriteV(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var offset = a4 | ((long)a5 << 32);
        var iovs = ReadIovecs(this, engine, a2, (int)a3);
        if (iovs == null) return -(int)Errno.EFAULT;
        try
        {
            return await DoWriteV(this, engine, (int)a1, iovs, (int)a3, offset, 0);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private async ValueTask<int> SysPReadV2(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var offset = a4 | ((long)a5 << 32);
        var flags = (int)a6;
        var iovs = ReadIovecs(this, engine, a2, (int)a3);
        if (iovs == null) return -(int)Errno.EFAULT;
        try
        {
            return await DoReadV(this, engine, (int)a1, iovs, (int)a3, offset, flags);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private async ValueTask<int> SysPWriteV2(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var offset = a4 | ((long)a5 << 32);
        var flags = (int)a6;
        var iovs = ReadIovecs(this, engine, a2, (int)a3);
        if (iovs == null) return -(int)Errno.EFAULT;
        try
        {
            return await DoWriteV(this, engine, (int)a1, iovs, (int)a3, offset, flags);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private async ValueTask<int> SysLseek(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fd = (int)a1;
        var offset = (long)(int)a2; // signed offset
        var whence = (int)a3;

        var f = GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;
        var inode = f.OpenedInode;
        if (inode == null) return -(int)Errno.EBADF;
        if (inode.Type is InodeType.Fifo or InodeType.Socket) return -(int)Errno.ESPIPE;

        var newPos = whence switch
        {
            0 => offset, // SEEK_SET
            1 => f.Position + offset, // SEEK_CUR
            2 => (long)inode.Size + offset, // SEEK_END
            _ => -1
        };

        if (newPos < 0) return -(int)Errno.EINVAL;
        f.Position = newPos;
        return (int)newPos;
    }

    private async ValueTask<int> SysLlseek(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fd = (int)a1;
        var offset = ((long)a2 << 32) | a3;
        var resultPtr = a4;
        var whence = (int)a5;

        var f = GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;
        var inode = f.OpenedInode;
        if (inode == null) return -(int)Errno.EBADF;
        if (inode.Type is InodeType.Fifo or InodeType.Socket) return -(int)Errno.ESPIPE;

        var newPos = whence switch
        {
            0 => offset, // SEEK_SET
            1 => f.Position + offset, // SEEK_CUR
            2 => (long)inode.Size + offset, // SEEK_END
            _ => -1
        };

        if (newPos < 0) return -(int)Errno.EINVAL;
        f.Position = newPos;

        if (!engine.CopyToUser(resultPtr, BitConverter.GetBytes(newPos)))
            return -(int)Errno.EFAULT;

        return 0;
    }

    private async ValueTask<int> SysClose(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        if (!TryFreeFD((int)a1)) return -(int)Errno.EBADF;
        return 0;
    }


    // --- IO Awaiter ---
    public readonly struct IOAwaitable
    {
        private readonly LinuxFile _file;
        private readonly bool _forRead;
        private readonly FiberTask _task;

        public IOAwaitable(LinuxFile file, bool forRead, FiberTask task)
        {
            _file = file;
            _forRead = forRead;
            _task = task;
        }

        public IOAwaiter GetAwaiter()
        {
            return new IOAwaiter(_file, _forRead, _task);
        }
    }

    public readonly struct IOAwaiter : INotifyCompletion
    {
        private readonly LinuxFile _file;
        private readonly bool _forRead;
        private readonly FiberTask _task;
        private readonly FiberTask.WaitToken _token;

        public IOAwaiter(LinuxFile file, bool forRead, FiberTask task)
        {
            _file = file;
            _forRead = forRead;
            _task = task;
            _token = task.BeginWaitToken();
        }

        public bool IsCompleted => false;

        public void OnCompleted(Action continuation)
        {
            var file = _file;
            var forRead = _forRead;
            var task = _task;
            var token = _token;
            Logger.LogTrace("[IOAwaiter] OnCompleted fd wait start forRead={ForRead} flags=0x{Flags:X}", forRead,
                (int)file.Flags);

            if (!task.TryEnterAsyncOperation(token, out var operation) || operation == null)
                return;

            var wakeHandler = new IoWaitOperation(task, token, continuation, operation, forRead);
            var inode = file.OpenedInode!;
            var registration = inode is ITaskWaitSource taskWaitSource
                ? taskWaitSource.RegisterWaitHandle(file, task, wakeHandler.OnIoReady,
                    (short)(forRead ? 0x0001 : 0x0004))
                : inode.RegisterWaitHandle(file, wakeHandler.OnIoReady, (short)(forRead ? 0x0001 : 0x0004));

            if (!wakeHandler.TryRegister(registration))
            {
                Logger.LogTrace("[IOAwaiter] RegisterWait returned false; invoking continuation now");
                wakeHandler.OnIoReady();
                return;
            }

            // ArmSignalSafetyNet: registers the continuation AND atomically re-checks for
            // signals that arrived before BeginWaitToken was called (TOCTOU-safe).
            Logger.LogTrace("[IOAwaiter] RegisterWait armed forRead={ForRead}, arming safety net", forRead);
            task.ArmInterruptingSignalSafetyNet(token, wakeHandler.OnSignal);
        }

        public AwaitResult GetResult()
        {
            var reason = _task.CompleteWaitToken(_token);
            if (reason != WakeReason.IO && reason != WakeReason.None) return AwaitResult.Interrupted;
            Logger.LogTrace("[IOAwaiter] Wait completed as IO");
            return AwaitResult.Completed;
        }

        private sealed class IoWaitOperation
        {
            private readonly bool _forRead;
            private readonly TaskAsyncOperationHandle _operation;

            public IoWaitOperation(FiberTask task, FiberTask.WaitToken token, Action continuation,
                TaskAsyncOperationHandle operation, bool forRead)
            {
                _operation = operation;
                _forRead = forRead;
                _operation.TryInitialize(continuation);
            }

            public bool TryRegister(IDisposable? registration)
            {
                return _operation.TryAddRegistration(TaskAsyncRegistration.From(registration));
            }

            public void OnIoReady()
            {
                Logger.LogTrace("[IOAwaiter] RegisterWait callback fired forRead={ForRead}", _forRead);
                _operation.TryComplete(WakeReason.IO);
            }

            public void OnSignal()
            {
                _operation.TryComplete(WakeReason.Signal);
            }
        }
    }

    private async ValueTask<int> SysReadahead(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0; // Success (hint)
    }

    private async ValueTask<int> SysFadvise64(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0; // Success (hint)
    }
}
