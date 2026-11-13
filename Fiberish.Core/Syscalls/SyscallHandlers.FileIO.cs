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

        if (result >= 0 && offsetPtr != 0 && newOffset.HasValue)
            if (!engine.CopyToUser(offsetPtr, BitConverter.GetBytes((uint)newOffset.Value)))
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
            var allocatedR = AllocFD(rFile);
            if (allocatedR < 0)
            {
                rFile.Close();
                return allocatedR;
            }
            rFd = allocatedR;

            // Writer
            var wDentry = new Dentry(FsName.FromString("pipe:[write]"), pipe, null, MemfdSuperBlock);
            var wFile = new LinuxFile(wDentry, FileFlags.O_WRONLY, AnonMount);
            var allocatedW = AllocFD(wFile);
            if (allocatedW < 0)
            {
                wFile.Close();
                FreeFD(rFd.Value);
                return allocatedW;
            }
            wFd = allocatedW;

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

        const FileFlags SupportedFlags = FileFlags.O_CLOEXEC | FileFlags.O_NONBLOCK | FileFlags.O_DIRECT;
        if ((flags & ~SupportedFlags) != 0)
            return -(int)Errno.EINVAL;

        int? rFd = null;
        int? wFd = null;

        try
        {
            var pipe = new PipeInode(((FiberTask)engine.Owner!).CommonKernel,
                packetMode: (flags & FileFlags.O_DIRECT) != 0);
            pipe.SuperBlock = MemfdSuperBlock;

            // Build file flags for reader and writer
            // O_NONBLOCK is stored in LinuxFile.Flags and checked by read/write syscalls
            var baseFlags = flags & SupportedFlags;

            // Reader
            var rFlags = FileFlags.O_RDONLY | (baseFlags & ~FileFlags.O_DIRECT);
            var rDentry = new Dentry(FsName.FromString("pipe:[read]"), pipe, null, MemfdSuperBlock);
            var rFile = new LinuxFile(rDentry, rFlags, AnonMount);
            var allocatedR = AllocFD(rFile);
            if (allocatedR < 0)
            {
                rFile.Close();
                return allocatedR;
            }
            rFd = allocatedR;

            // Writer
            var wFlags = FileFlags.O_WRONLY | baseFlags;
            var wDentry = new Dentry(FsName.FromString("pipe:[write]"), pipe, null, MemfdSuperBlock);
            var wFile = new LinuxFile(wDentry, wFlags, AnonMount);
            var allocatedW = AllocFD(wFile);
            if (allocatedW < 0)
            {
                wFile.Close();
                FreeFD(rFd.Value);
                return allocatedW;
            }
            wFd = allocatedW;

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


        var flags = a2;
        var knownFlags = MFD_CLOEXEC | MFD_ALLOW_SEALING | MFD_NOEXEC_SEAL | MFD_EXEC;
        if ((flags & ~knownFlags) != 0) return -(int)Errno.EINVAL;
        if ((flags & MFD_EXEC) != 0 && (flags & MFD_NOEXEC_SEAL) != 0) return -(int)Errno.EINVAL;

        if (a1 == 0) return -(int)Errno.EFAULT;
        // memfd names are byte strings of at most NAME_MAX - strlen("memfd:")
        // bytes. Read only through the terminator, including across page edges.
        var nameBytes = new byte[250];
        var nameLength = -1;
        for (var consumed = 0; consumed < nameBytes.Length;)
        {
            var address = a1 + (uint)consumed;
            var chunk = Math.Min(nameBytes.Length - consumed,
                LinuxConstants.PageSize - (int)(address & LinuxConstants.PageOffsetMask));
            var span = nameBytes.AsSpan(consumed, chunk);
            if (!engine.CopyFromUser(address, span)) return -(int)Errno.EFAULT;
            var terminator = span.IndexOf((byte)0);
            if (terminator >= 0)
            {
                nameLength = consumed + terminator;
                break;
            }
            consumed += chunk;
        }
        if (nameLength < 0) return -(int)Errno.EINVAL;
        var name = FsEncoding.DecodeUtf8Lossy(nameBytes.AsSpan(0, nameLength));

        try
        {
            var t = engine.Owner as FiberTask;
            var uid = t?.Process.EUID ?? 0;
            var gid = t?.Process.EGID ?? 0;

            var display = string.IsNullOrEmpty(name) ? "anon" : name;

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
            return MapSyscallExceptionToErrno(ex);
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
        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace("[Open] Path='{Path}' Flags={Flags} Mode={Mode}",
                FsEncoding.DecodeUtf8Lossy(pathBytes.AsSpan(0, pathLength)), flags, mode);
        const uint O_TMPFILE_MASK = 0x400000;
        var isPathOnly = ((FileFlags)flags & FileFlags.O_PATH) != 0;
        var createdHere = false;
        var accessMode = flags & 3u;
        var wantDirectory = ((FileFlags)flags & FileFlags.O_DIRECTORY) != 0;
        var wantCreate = !isPathOnly && ((FileFlags)flags & FileFlags.O_CREAT) != 0;
        var wantExcl = !isPathOnly && ((FileFlags)flags & FileFlags.O_EXCL) != 0;
        var wantTrunc = !isPathOnly && ((FileFlags)flags & FileFlags.O_TRUNC) != 0;
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

            var tmpName = $".tmpfile.{Guid.NewGuid():N}";
            var anonDentry = new Dentry(FsName.FromString(tmpName), null, dentry, dentry.SuperBlock);
            var create = DacPolicy.ComputeCreationMetadata(sm.CurrentTask?.Process, dentry.Inode, (int)mode, false);
            var createRc = dentry.Inode.Create(anonDentry, create.Mode, create.Uid, create.Gid);
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

                dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
                var create = DacPolicy.ComputeCreationMetadata(sm.CurrentTask?.Process, parentDentry.Inode,
                    (int)mode, false);
                var createRc2 = parentDentry.Inode.Create(dentry, create.Mode, create.Uid, create.Gid);
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
            if (noFollow && !isPathOnly && dentry.Inode?.Type == InodeType.Symlink)
                return -(int)Errno.ELOOP;

            if (wantDirectory && dentry.Inode?.Type != InodeType.Directory)
                return -(int)Errno.ENOTDIR;

            // File exists - check for O_EXCL
            if (wantCreate && wantExcl)
                return -(int)Errno.EEXIST;

            if (!isPathOnly)
            {
                if (dentry.Inode?.Type == InodeType.Directory && (accessMode != 0 || wantTrunc || wantCreate))
                    return -(int)Errno.EISDIR;
                if (dentry.Inode?.Type == InodeType.Socket)
                    return -(int)Errno.ENXIO;
                if (dentry.Inode?.Type == InodeType.Fifo &&
                    accessMode == (int)FileFlags.O_WRONLY &&
                    ((FileFlags)flags & FileFlags.O_NONBLOCK) != 0)
                    return -(int)Errno.ENXIO;

                // open(2): O_NOATIME requires ownership or CAP_FOWNER.
                var noAtime = ((FileFlags)flags & FileFlags.O_NOATIME) != 0;
                if (noAtime && dentry.Inode != null)
                {
                    var process = task?.Process;
                    if (process == null || (process.EUID != 0 && process.FSUID != dentry.Inode.Uid))
                        return -(int)Errno.EPERM;
                }

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
        }

        try
        {
            if (!isPathOnly && !createdHere &&
                (flags & (uint)FileFlags.O_TRUNC) != 0 &&
                dentry?.Inode?.Type == InodeType.File)
            {
                var truncateRc = dentry.Inode.Truncate(0, sm.CreateFileMutationContext(sm.CurrentSyscallEngine));
                if (truncateRc < 0) return truncateRc;
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
            var allocatedFd = sm.AllocFD(f);
            if (allocatedFd < 0)
            {
                f.Close();
                return allocatedFd;
            }
            return allocatedFd;
        }
        catch (Exception ex)
        {
            Logger.LogInformation(ex, "[Open] final open failed path='{Path}' flags={Flags} mode={Mode}",
                FsEncoding.DecodeUtf8Lossy(pathBytes.AsSpan(0, pathLength)), flags, mode);
            return MapSyscallExceptionToErrno(ex);
        }
    }

    private ValueTask<int> SysOpen(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var pathErr = ReadPathArgumentBytes(a1, out var path);
        if (pathErr != 0) return ValueTask.FromResult(pathErr);
        using var _ = path;

        return ValueTask.FromResult(ImplOpen(this, path, a2, a3));
    }

    private ValueTask<int> SysOpenAt(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var dfd = (int)a1;
        var pathErr = ReadPathArgumentBytes(a2, out var path);
        if (pathErr != 0) return ValueTask.FromResult(pathErr);
        using var _ = path;

        var startLoc = PathLocation.None;
        if (dfd == -100) // AT_FDCWD
        {
            startLoc = CurrentWorkingDirectory;
        }
        else
        {
            var f = GetFD(dfd);
            if (f == null) return ValueTask.FromResult(-(int)Errno.EBADF);
            if (!path.IsAbsolute && f.OpenedInode?.Type != InodeType.Directory)
                return ValueTask.FromResult(-(int)Errno.ENOTDIR);
            startLoc = f.LivePath;
        }

        return ValueTask.FromResult(ImplOpen(this, path, a3, a4, startLoc));
    }

    private ValueTask<int> SysOpenAt2(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var dirfd = (int)a1;
        var pathErr = ReadPathArgumentBytes(a2, out var path);
        if (pathErr != 0) return ValueTask.FromResult(pathErr);
        using var _ = path;

        var howPtr = a3;
        var howSize = a4;

        if (howSize < 24) return ValueTask.FromResult(-(int)Errno.EINVAL);

        var howBuf = new byte[24];
        if (!engine.CopyFromUser(howPtr, howBuf)) return ValueTask.FromResult(-(int)Errno.EFAULT);

        var flags = BinaryPrimitives.ReadUInt64LittleEndian(howBuf.AsSpan(0, 8));
        var mode = BinaryPrimitives.ReadUInt64LittleEndian(howBuf.AsSpan(8, 8));

        PathLocation startAt = default;
        if (dirfd != -100 && !path.IsAbsolute)
        {
            var fdir = GetFD(dirfd);
            if (fdir == null) return ValueTask.FromResult(-(int)Errno.EBADF);
            if (fdir.OpenedInode?.Type != InodeType.Directory)
                return ValueTask.FromResult(-(int)Errno.ENOTDIR);
            startAt = fdir.LivePath;
        }

        return ValueTask.FromResult(ImplOpen(this, path, (uint)flags, (uint)mode, startAt));
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

        if (newfd < 0 || (engine.Owner is FiberTask owner &&
                         owner.Process.TryGetResourceLimit(LinuxConstants.RLIMIT_NOFILE, out var limit) &&
                         (ulong)newfd >= limit.Soft))
            return -(int)Errno.EBADF;

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
        if (f.OpenedInode.Type == InodeType.Directory)
            return -(int)Errno.EISDIR;

        return await f.OpenedInode.ReadV(engine, f, task, new ArraySegment<Iovec>(iovs, 0, iovCnt), offset, flags);
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

        // RLIMIT_FSIZE: writes entirely past the limit fail with EFBIG and raise SIGXFSZ.
        if (f.OpenedInode.Type == InodeType.File && task?.Process != null &&
            task.Process.TryGetResourceLimit(LinuxConstants.RLIMIT_FSIZE, out var fsizeLimit) &&
            fsizeLimit.Soft != ulong.MaxValue)
        {
            var limit = (long)Math.Min(fsizeLimit.Soft, (ulong)long.MaxValue);
            if (writeStartOffset >= limit)
            {
                task.PostSignal((int)Signal.SIGXFSZ);
                return -(int)Errno.EFBIG;
            }
        }

        var sizeBeforeWrite = (long)f.OpenedInode.Size;
        var rc = await f.OpenedInode.WriteV(engine, f, task, new ArraySegment<Iovec>(iovs, 0, iovCnt), offset, flags);

        if (rc > 0 && f.OpenedInode is Inode inode)
        {
            ProcessAddressSpaceSync.NotifyFileContentChanged(sm.Mem, engine, inode, writeStartOffset, rc);

            var sizeAfterWrite = (long)inode.Size;
            if (sizeAfterWrite < sizeBeforeWrite)
                ProcessAddressSpaceSync.NotifyInodeTruncated(inode, sizeAfterWrite,
                    sm.CreateFileMutationContext(engine));
        }

        return rc;
    }

    private int ValidatePositionedIo(int fd, long offset, bool allowCurrentPosition = false)
    {
        var file = GetFD(fd);
        if (file?.OpenedInode == null) return -(int)Errno.EBADF;
        if (offset < 0 && !(allowCurrentPosition && offset == -1)) return -(int)Errno.EINVAL;
        if (offset != -1 && file.OpenedInode.Type is InodeType.Fifo or InodeType.Socket)
            return -(int)Errno.ESPIPE;
        return 0;
    }

    private static Iovec[]? ReadIovecs(SyscallManager sm, Engine engine, uint iovAddr, int iovCnt, out int error)
    {
        error = -(int)Errno.EINVAL;
        if (iovCnt < 0 || iovCnt > 1024) return null;
        var iovs = ArrayPool<Iovec>.Shared.Rent(iovCnt);
        var iovecBytes = ArrayPool<byte>.Shared.Rent(iovCnt * 8);
        var success = false;

        try
        {
            error = -(int)Errno.EFAULT;
            if (!engine.CopyFromUser(iovAddr, iovecBytes.AsSpan(0, iovCnt * 8))) return null;

            const uint MaxRwCount = 0x7FFFF000; // Linux's INT_MAX rounded down to a guest page.
            uint total = 0;
            for (var i = 0; i < iovCnt; i++)
            {
                var length = BinaryPrimitives.ReadUInt32LittleEndian(iovecBytes.AsSpan(i * 8 + 4, 4));
                if (length > int.MaxValue)
                {
                    error = -(int)Errno.EINVAL;
                    return null;
                }
                iovs[i] = new Iovec
                {
                    BaseAddr = BinaryPrimitives.ReadUInt32LittleEndian(iovecBytes.AsSpan(i * 8, 4)),
                    Len = Math.Min(length, MaxRwCount - total)
                };
                total += iovs[i].Len;
            }
            error = 0;
            success = true;
            return iovs;
        }
        catch
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(iovecBytes);
            if (!success) ArrayPool<Iovec>.Shared.Return(iovs);
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
        var error = ValidatePositionedIo((int)a1, offset);
        if (error != 0) return error;
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
        var error = ValidatePositionedIo((int)a1, offset);
        if (error != 0) return error;
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
        var iovs = ReadIovecs(this, engine, a2, (int)a3, out var error);
        if (iovs == null) return error;
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
        var iovs = ReadIovecs(this, engine, a2, (int)a3, out var error);
        if (iovs == null) return error;
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
        var positionError = ValidatePositionedIo((int)a1, offset);
        if (positionError != 0) return positionError;
        var iovs = ReadIovecs(this, engine, a2, (int)a3, out var error);
        if (iovs == null) return error;
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
        var positionError = ValidatePositionedIo((int)a1, offset);
        if (positionError != 0) return positionError;
        var iovs = ReadIovecs(this, engine, a2, (int)a3, out var error);
        if (iovs == null) return error;
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
        const int KnownRwfFlags = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x20;
        if ((flags & ~KnownRwfFlags) != 0) return -(int)Errno.EOPNOTSUPP;
        var positionError = ValidatePositionedIo((int)a1, offset, true);
        if (positionError != 0) return positionError;
        var iovs = ReadIovecs(this, engine, a2, (int)a3, out var error);
        if (iovs == null) return error;
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
        const int KnownRwfFlags = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x20;
        if ((flags & ~KnownRwfFlags) != 0) return -(int)Errno.EOPNOTSUPP;
        var positionError = ValidatePositionedIo((int)a1, offset, true);
        if (positionError != 0) return positionError;
        var iovs = ReadIovecs(this, engine, a2, (int)a3, out var error);
        if (iovs == null) return error;
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
