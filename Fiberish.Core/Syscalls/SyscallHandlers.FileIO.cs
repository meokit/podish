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

    private static ValueTask<(int, long?)> DoSendfile(SyscallManager sm, Engine engine, int outFd, int inFd,
        long? offset, int count)
    {
        if (!sm.FDs.TryGetValue(inFd, out var inFile) || !sm.FDs.TryGetValue(outFd, out var outFile))
            return ValueTask.FromResult<(int, long?)>((-(int)Errno.EBADF, null));
        var lease = new FileIoLease(inFile, outFile);
        try { return lease.Complete(DoSendfileCore(engine, inFile, outFile, offset, count), engine.Owner as FiberTask); }
        finally { lease.Dispose(); }
    }

    private static async ValueTask<(int, long?)> DoSendfileCore(Engine engine, LinuxFile inFile, LinuxFile outFile,
        long? offset, int count)
    {
        if (inFile.IsPathOnly || outFile.IsPathOnly) return (-(int)Errno.EBADF, null);
        var task = engine.Owner as FiberTask;

        // Verify modes
        const int O_ACCMODE = 3;
        if (((int)inFile.Flags & O_ACCMODE) == (int)FileFlags.O_WRONLY) return (-(int)Errno.EBADF, null);
        if (((int)outFile.Flags & O_ACCMODE) == (int)FileFlags.O_RDONLY) return (-(int)Errno.EBADF, null);
        if (inFile.IoInode is PipeInode)
        {
            if (offset.HasValue) return (-(int)Errno.ESPIPE, null);
            if (outFile.IoInode is not PipeInode || ReferenceEquals(inFile.IoInode, outFile.IoInode))
                return (-(int)Errno.EINVAL, null);
        }
        if (count == 0) return (0, offset);
        var prepare = await outFile.IoInode!.PrepareWriteAsync(task, outFile);
        if (prepare < 0) return (prepare, null);

        // Use a buffer
        var bufLen = Math.Min(count, 32768);
        var asyncRead = inFile.IoInode!.SupportsAsyncRead;
        var buffer = asyncRead ? new byte[bufLen] : ArrayPool<byte>.Shared.Rent(bufLen);
        var rented = asyncRead ? default : new TaskIoBuffer<byte>(buffer);
        var totalWritten = 0;

        try
        {
            var remaining = count;
            var readOffset = offset ?? inFile.Position;

            while (remaining > 0)
            {
                var toRead = Math.Min(remaining, bufLen);
                var bytesRead = inFile.IoInode is PipeInode inputPipe
                    ? inputPipe.Peek(buffer.AsSpan(0, toRead))
                    : await inFile.IoInode!.ReadToHostAsync(task, inFile, buffer.AsMemory(0, toRead), readOffset);

                if (asyncRead && !offset.HasValue && inFile.Position != readOffset && bytesRead >= 0)
                {
                    if (totalWritten > 0) break;
                    readOffset = inFile.Position;
                    continue;
                }

                if (bytesRead <= 0)
                {
                    if (bytesRead == 0) // EOF
                        break;
                    if (bytesRead == -(int)Errno.EAGAIN)
                    {
                        if (totalWritten > 0) break;
                        if ((inFile.Flags & FileFlags.O_NONBLOCK) != 0) return (-(int)Errno.EAGAIN, null);
                        if (task == null) return (-(int)Errno.EAGAIN, null);
                        rented.TrackPending(task);
                        if (await new IOAwaitable(inFile, true, task) == AwaitResult.Interrupted)
                            return (-(int)Errno.ERESTARTSYS, null);
                        continue;
                    }

                    if (totalWritten > 0)
                        break;
                    return (bytesRead, null); // Return error code
                }

                // Write to out_fd
                var toWrite = outFile.IoInode is PipeInode outputPipe
                    ? Math.Min(bytesRead, Math.Max(1, outputPipe.GetWritableBytes())) : bytesRead;
                var bytesWritten =
                    outFile.IoInode!.WriteFromHost(task, outFile, buffer.AsSpan(0, toWrite), outFile.Position);

                if (bytesWritten < 0)
                {
                    if (bytesWritten == -(int)Errno.EAGAIN)
                    {
                        if (totalWritten > 0) break;
                        if ((outFile.Flags & FileFlags.O_NONBLOCK) != 0) return (-(int)Errno.EAGAIN, null);
                        if (task != null)
                        {
                            rented.TrackPending(task);
                            if (await new IOAwaitable(outFile, false, task) == AwaitResult.Interrupted)
                                return (-(int)Errno.ERESTARTSYS, null);

                            // Re-read/peek after suspension: input was not consumed,
                            // and another task may have changed a shared source.
                            continue;
                        }
                    }

                    if (bytesWritten == -(int)Errno.EPIPE) task?.PostSignal((int)Signal.SIGPIPE);
                    if (totalWritten > 0) break;

                    return (bytesWritten, null);
                }

                if (bytesWritten > 0)
                {
                    if (inFile.IoInode is PipeInode source)
                        source.ConsumeSpliced(bytesWritten);
                    else if (!offset.HasValue)
                        inFile.Position += bytesWritten;
                    if (outFile.IoInode is not PipeInode) outFile.Position += bytesWritten;
                }

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
            rented.Dispose();
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
            var allocatedReadFd = AllocFD(rFile);
            if (allocatedReadFd < 0)
                return allocatedReadFd;
            rFd = allocatedReadFd;
            // pipe.AddReader(); // Handled by File ctor -> Inode.Open

            // Writer
            var wDentry = new Dentry(FsName.FromString("pipe:[write]"), pipe, null, MemfdSuperBlock);
            var wFile = new LinuxFile(wDentry, FileFlags.O_WRONLY, AnonMount);
            var allocatedWriteFd = AllocFD(wFile);
            if (allocatedWriteFd < 0)
            {
                FreeFD(rFd.Value);
                return allocatedWriteFd;
            }
            wFd = allocatedWriteFd;
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
        catch (PipeQuotaExceededException)
        {
            if (rFd.HasValue) FreeFD(rFd.Value);
            if (wFd.HasValue) FreeFD(wFd.Value);
            return -(int)Errno.ENFILE;
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
        var flags = (FileFlags)(engine.IsAArch64 ? TranslateAArch64OpenFlags(a2) : a2);

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
            var allocatedReadFd = AllocFD(rFile);
            if (allocatedReadFd < 0)
                return allocatedReadFd;
            rFd = allocatedReadFd;

            // Writer
            var wFlags = FileFlags.O_WRONLY | baseFlags;
            var wDentry = new Dentry(FsName.FromString("pipe:[write]"), pipe, null, MemfdSuperBlock);
            var wFile = new LinuxFile(wDentry, wFlags, AnonMount);
            var allocatedWriteFd = AllocFD(wFile);
            if (allocatedWriteFd < 0)
            {
                FreeFD(rFd.Value);
                return allocatedWriteFd;
            }
            wFd = allocatedWriteFd;

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
        catch (PipeQuotaExceededException)
        {
            if (rFd.HasValue) FreeFD(rFd.Value);
            if (wFd.HasValue) FreeFD(wFd.Value);
            return -(int)Errno.ENFILE;
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

    private static ValueTask<int> ImplOpen(SyscallManager sm, RentedUserBytes path, uint flags, uint mode,
        PathLocation startLoc = default)
    {
        return ImplOpen(sm, path.UnsafeBuffer, path.Length, flags, mode, startLoc);
    }

    private static ValueTask<int> ImplOpen(SyscallManager sm, string path, uint flags, uint mode, PathLocation startLoc = default)
    {
        var pathBytes = FsEncoding.EncodeUtf8(path);
        return ImplOpen(sm, pathBytes, pathBytes.Length, flags, mode, startLoc);
    }

    private static ValueTask<int> ImplOpen(SyscallManager sm, byte[] pathBytes, int pathLength, uint flags, uint mode,
        PathLocation startLoc = default)
    {
        var rc = ImplOpenCore(sm, pathBytes, pathLength, flags, mode, startLoc, out var pending);
        return pending ?? ValueTask.FromResult(rc);
    }

    private static int ImplOpenCore(SyscallManager sm, byte[] pathBytes, int pathLength, uint flags, uint mode,
        PathLocation startLoc, out ValueTask<int>? pending)
    {
        pending = null;
        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace("[Open] Path='{Path}' Flags={Flags} Mode={Mode}",
                FsEncoding.DecodeUtf8Lossy(pathBytes.AsSpan(0, pathLength)), flags, mode);
        const uint O_TMPFILE_MASK = 0x400000;
        var isPathOnly = ((FileFlags)flags & FileFlags.O_PATH) != 0;
        if (isPathOnly)
            flags &= (uint)(FileFlags.O_PATH | FileFlags.O_DIRECTORY | FileFlags.O_NOFOLLOW | FileFlags.O_CLOEXEC);
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
                        {
                            pending = ImplOpen(sm, createPath, createPath.Length, flags, mode, startLoc);
                            return 0;
                        }
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
                    if (mount != null && mount.IsReadOnly && dentry.Inode?.Type != InodeType.Fifo)
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
            if (!isPathOnly && dentry.Inode is MappingBackedInode { Type: InodeType.Fifo } && task != null)
            {
                pending = sm.OpenNamedFifo(dentry, (FileFlags)openFlags, mount ?? sm.RootMount!, task);
                return 0;
            }
            var f = new LinuxFile(dentry, (FileFlags)openFlags, mount ?? sm.RootMount!);
            if (task != null && f.OpenedInode is ITaskContextBoundInode taskBoundInode)
                taskBoundInode.BindTaskContext(f, task);
            if (createdHere && dentry.Parent?.Inode != null)
                InotifyEvents.Raise(dentry.Parent.Inode, LinuxConstants.IN_CREATE, dentry.Name.Bytes);
            InotifyEvents.RaiseChild(dentry, f.OpenedInode, LinuxConstants.IN_OPEN);
            return sm.AllocFD(f);
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

        return ImplOpen(this, path, a2, a3);
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

        var flags = engine.IsAArch64 ? TranslateAArch64OpenFlags(a3) : a3;
        return ImplOpen(this, path, flags, a4, startLoc);
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

        Span<byte> howBuf = stackalloc byte[24];
        if (!engine.CopyFromUser(howPtr, howBuf)) return ValueTask.FromResult(-(int)Errno.EFAULT);

        var flags = BinaryPrimitives.ReadUInt64LittleEndian(howBuf[..8]);
        var mode = BinaryPrimitives.ReadUInt64LittleEndian(howBuf.Slice(8, 8));

        PathLocation startAt = default;
        if (dirfd != -100 && !path.IsAbsolute)
        {
            var fdir = GetFD(dirfd);
            if (fdir == null) return ValueTask.FromResult(-(int)Errno.EBADF);
            if (fdir.OpenedInode?.Type != InodeType.Directory)
                return ValueTask.FromResult(-(int)Errno.ENOTDIR);
            startAt = fdir.LivePath;
        }

        if (engine.IsAArch64)
        {
            if (flags > uint.MaxValue || mode > uint.MaxValue) return ValueTask.FromResult(-(int)Errno.EINVAL);
            flags = TranslateAArch64OpenFlags((uint)flags);
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

        if (newfd < 0 || (engine.Owner is FiberTask owner &&
                         owner.Process.TryGetResourceLimit(LinuxConstants.RLIMIT_NOFILE, out var limit) &&
                         (ulong)newfd >= limit.Soft))
            return -(int)Errno.EBADF;

        var f = GetFD(oldfd);
        if (f == null) return -(int)Errno.EBADF;

        if (CurrentFdTable.IsReserved(newfd)) return -(int)Errno.EBUSY;
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

    private static ValueTask<int> DoReadV(SyscallManager sm, Engine engine, int fd, Iovec[] iovs, int iovCnt,
        long offset, int flags)
    {
        var file = sm.GetFD(fd);
        if (file == null) return ValueTask.FromResult(-(int)Errno.EBADF);
        var lease = new FileIoLease(file);
        try { return lease.Complete(DoReadVCore(engine, file, iovs, iovCnt, offset, flags), engine.Owner as FiberTask); }
        finally { lease.Dispose(); }
    }

    private static async ValueTask<int> DoReadVCore(Engine engine, LinuxFile f, Iovec[] iovs, int iovCnt,
        long offset, int flags)
    {
        if (f.IsPathOnly) return -(int)Errno.EBADF;
        var task = engine.Owner as FiberTask;
        const int O_ACCMODE = 3;
        if (((int)f.Flags & O_ACCMODE) == (int)FileFlags.O_WRONLY)
            return -(int)Errno.EBADF;

        if (f.OpenedInode == null)
            return -(int)Errno.EINVAL;
        if (f.OpenedInode.Type == InodeType.Directory)
            return -(int)Errno.EISDIR;

        var readRc = await f.IoInode!.ReadV(engine, f, task, new ArraySegment<Iovec>(iovs, 0, iovCnt), offset,
            flags);
        if (readRc > 0)
            InotifyEvents.RaiseChild(f.Dentry, f.OpenedInode, LinuxConstants.IN_ACCESS);
        return readRc;
    }

    private static ValueTask<int> DoWriteV(SyscallManager sm, Engine engine, int fd, Iovec[] iovs, int iovCnt,
        long offset, int flags)
    {
        var file = sm.GetFD(fd);
        if (file == null) return ValueTask.FromResult(-(int)Errno.EBADF);
        var lease = new FileIoLease(file);
        try { return lease.Complete(DoWriteVCore(sm, engine, file, iovs, iovCnt, offset, flags), engine.Owner as FiberTask); }
        finally { lease.Dispose(); }
    }

    private static async ValueTask<int> DoWriteVCore(SyscallManager sm, Engine engine, LinuxFile f, Iovec[] iovs, int iovCnt,
        long offset, int flags)
    {
        var task = engine.Owner as FiberTask;

        // Check mount read-only status (like Linux mnt_want_write)
        var wantWrite = f.WantWrite();
        if (wantWrite < 0) return wantWrite;

        if (f.OpenedInode == null)
            return -(int)Errno.EINVAL;

        if (new ArraySegment<Iovec>(iovs, 0, iovCnt).Any(iov => iov.Len != 0))
        {
            var prepare = await f.IoInode!.PrepareWriteAsync(task, f);
            if (prepare < 0) return prepare;
        }

        var writeStartOffset = offset == -1
            ? (f.Flags & FileFlags.O_APPEND) != 0 ? (long)f.OpenedInode.Size : f.Position
            : offset;
        var writeIovs = new ArraySegment<Iovec>(iovs, 0, iovCnt);
        Iovec[]? limitedIovs = null;

        // Only a non-empty write at/above the limit raises SIGXFSZ. A write that
        // straddles the limit succeeds for the prefix, including across iovecs.
        if (f.OpenedInode.Type == InodeType.File && task?.Process != null &&
            task.Process.TryGetResourceLimit(LinuxConstants.RLIMIT_FSIZE, out var fsizeLimit) &&
            fsizeLimit.Soft != ulong.MaxValue)
        {
            var limit = (long)Math.Min(fsizeLimit.Soft, (ulong)long.MaxValue);
            long requested = 0;
            foreach (var iov in writeIovs)
                requested += iov.Len;
            if (requested > 0 && writeStartOffset >= limit)
            {
                task.PostSignal((int)Signal.SIGXFSZ);
                return -(int)Errno.EFBIG;
            }
            if (requested > 0 && writeStartOffset >= 0 && requested > limit - writeStartOffset)
            {
                limitedIovs = ArrayPool<Iovec>.Shared.Rent(iovCnt);
                var remaining = limit - writeStartOffset;
                for (var i = 0; i < iovCnt; i++)
                {
                    limitedIovs[i] = iovs[i];
                    limitedIovs[i].Len = (uint)Math.Min(iovs[i].Len, remaining);
                    remaining -= limitedIovs[i].Len;
                }
                writeIovs = new ArraySegment<Iovec>(limitedIovs, 0, iovCnt);
            }
        }

        var sizeBeforeWrite = (long)f.OpenedInode.Size;
        var rentedLimited = limitedIovs == null ? default : new TaskIoBuffer<Iovec>(limitedIovs);
        int rc;
        try
        {
            var operation = f.IoInode!.WriteV(engine, f, task, writeIovs, offset, flags);
            if (limitedIovs != null && !operation.IsCompleted) rentedLimited.TrackPending(task);
            rc = await operation;
        }
        finally
        {
            rentedLimited.Dispose();
        }

        if (rc > 0 && f.OpenedInode is Inode inode)
        {
            InotifyEvents.RaiseChild(f.Dentry, inode, LinuxConstants.IN_MODIFY);
            var actualWriteOffset = offset == -1 && f.Position >= rc ? f.Position - rc : writeStartOffset;
            ProcessAddressSpaceSync.NotifyFileContentChanged(sm.Mem, engine, inode, actualWriteOffset, rc);

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
        if (file?.OpenedInode == null || file.IsPathOnly) return -(int)Errno.EBADF;
        if (offset < 0 && !(allowCurrentPosition && offset == -1)) return -(int)Errno.EINVAL;
        if (offset != -1 && file.OpenedInode.Type is InodeType.Fifo or InodeType.Socket)
            return -(int)Errno.ESPIPE;
        return 0;
    }

    private static async ValueTask<(Iovec[]? Iovs, int Error)> ReadFileIovecsAsync(Engine engine, uint iovAddr,
        int iovCnt)
    {
        if (iovCnt < 0 || iovCnt > 1024) return (null, -(int)Errno.EINVAL);

        var wordSize = GuestWordSize(engine);
        var byteCount = iovCnt * 2 * wordSize;
        var iovecBytes = ArrayPool<byte>.Shared.Rent(byteCount);
        var rented = new TaskIoBuffer<byte>(iovecBytes);
        try
        {
            var error = await rented.Track(
                engine.CopyUserIoAsync(iovAddr, iovecBytes.AsMemory(0, byteCount), write: false),
                engine.Owner as FiberTask);
            if (error != 0) return (null, error);
            return ParseFileIovecs(iovecBytes, iovCnt, wordSize);
        }
        finally
        {
            rented.Dispose();
        }
    }

    private static (Iovec[]? Iovs, int Error) ParseFileIovecs(byte[] iovecBytes, int iovCnt, int wordSize)
    {
        var iovs = ArrayPool<Iovec>.Shared.Rent(iovCnt);
        const uint MaxRwCount = 0x7FFFF000; // Linux's INT_MAX rounded down to a guest page.
        uint total = 0;
        for (var i = 0; i < iovCnt; i++)
        {
            var entry = iovecBytes.AsSpan(i * 2 * wordSize, 2 * wordSize);
            var address = ReadGuestWord(entry, wordSize);
            var length = ReadGuestWord(entry[wordSize..], wordSize);
            if (length > (wordSize == 8 ? (ulong)long.MaxValue : int.MaxValue))
            {
                ArrayPool<Iovec>.Shared.Return(iovs);
                return (null, -(int)Errno.EINVAL);
            }

            var count = (uint)Math.Min(length, MaxRwCount - total);
            if (address > uint.MaxValue || address + count > (ulong)uint.MaxValue + 1)
            {
                ArrayPool<Iovec>.Shared.Return(iovs);
                return (null, -(int)Errno.EFAULT);
            }

            iovs[i] = new Iovec { BaseAddr = (uint)address, Len = count };
            total += iovs[i].Len;
        }
        return (iovs, 0);
    }

    internal async ValueTask<int> SysRead(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var iovs = ArrayPool<Iovec>.Shared.Rent(1);
        var rented = new TaskIoBuffer<Iovec>(iovs);
        iovs[0] = new Iovec { BaseAddr = a2, Len = a3 };
        try
        {
            return await rented.Track(DoReadV(this, engine, (int)a1, iovs, 1, -1, 0), engine.Owner as FiberTask);
        }
        finally
        {
            rented.Dispose();
        }
    }

    internal async ValueTask<int> SysWrite(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var iovs = ArrayPool<Iovec>.Shared.Rent(1);
        var rented = new TaskIoBuffer<Iovec>(iovs);
        iovs[0] = new Iovec { BaseAddr = a2, Len = a3 };
        try
        {
            return await rented.Track(DoWriteV(this, engine, (int)a1, iovs, 1, -1, 0), engine.Owner as FiberTask);
        }
        finally
        {
            rented.Dispose();
        }
    }

    private async ValueTask<int> SysPRead(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var offset = a4 | ((long)a5 << 32);
        var error = ValidatePositionedIo((int)a1, offset);
        if (error != 0) return error;
        var iovs = ArrayPool<Iovec>.Shared.Rent(1);
        var rented = new TaskIoBuffer<Iovec>(iovs);
        iovs[0] = new Iovec { BaseAddr = a2, Len = a3 };
        try
        {
            return await rented.Track(DoReadV(this, engine, (int)a1, iovs, 1, offset, 0), engine.Owner as FiberTask);
        }
        finally
        {
            rented.Dispose();
        }
    }

    private async ValueTask<int> SysPWrite(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var offset = a4 | ((long)a5 << 32);
        var error = ValidatePositionedIo((int)a1, offset);
        if (error != 0) return error;
        var iovs = ArrayPool<Iovec>.Shared.Rent(1);
        var rented = new TaskIoBuffer<Iovec>(iovs);
        iovs[0] = new Iovec { BaseAddr = a2, Len = a3 };
        try
        {
            return await rented.Track(DoWriteV(this, engine, (int)a1, iovs, 1, offset, 0), engine.Owner as FiberTask);
        }
        finally
        {
            rented.Dispose();
        }
    }

    private async ValueTask<int> SysReadV(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var (iovs, error) = await ReadFileIovecsAsync(engine, a2, (int)a3);
        if (iovs == null) return error;
        var rented = new TaskIoBuffer<Iovec>(iovs);
        try
        {
            return await rented.Track(DoReadV(this, engine, (int)a1, iovs, (int)a3, -1, 0), engine.Owner as FiberTask);
        }
        finally
        {
            rented.Dispose();
        }
    }

    private async ValueTask<int> SysWriteV(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var (iovs, error) = await ReadFileIovecsAsync(engine, a2, (int)a3);
        if (iovs == null) return error;
        var rented = new TaskIoBuffer<Iovec>(iovs);
        try
        {
            return await rented.Track(DoWriteV(this, engine, (int)a1, iovs, (int)a3, -1, 0), engine.Owner as FiberTask);
        }
        finally
        {
            rented.Dispose();
        }
    }

    private async ValueTask<int> SysPReadV(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var offset = a4 | ((long)a5 << 32);
        var positionError = ValidatePositionedIo((int)a1, offset);
        if (positionError != 0) return positionError;
        var (iovs, error) = await ReadFileIovecsAsync(engine, a2, (int)a3);
        if (iovs == null) return error;
        var rented = new TaskIoBuffer<Iovec>(iovs);
        try
        {
            return await rented.Track(DoReadV(this, engine, (int)a1, iovs, (int)a3, offset, 0), engine.Owner as FiberTask);
        }
        finally
        {
            rented.Dispose();
        }
    }

    private async ValueTask<int> SysPWriteV(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var offset = a4 | ((long)a5 << 32);
        var positionError = ValidatePositionedIo((int)a1, offset);
        if (positionError != 0) return positionError;
        var (iovs, error) = await ReadFileIovecsAsync(engine, a2, (int)a3);
        if (iovs == null) return error;
        var rented = new TaskIoBuffer<Iovec>(iovs);
        try
        {
            return await rented.Track(DoWriteV(this, engine, (int)a1, iovs, (int)a3, offset, 0), engine.Owner as FiberTask);
        }
        finally
        {
            rented.Dispose();
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
        var (iovs, error) = await ReadFileIovecsAsync(engine, a2, (int)a3);
        if (iovs == null) return error;
        var rented = new TaskIoBuffer<Iovec>(iovs);
        try
        {
            return await rented.Track(DoReadV(this, engine, (int)a1, iovs, (int)a3, offset, flags), engine.Owner as FiberTask);
        }
        finally
        {
            rented.Dispose();
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
        var (iovs, error) = await ReadFileIovecsAsync(engine, a2, (int)a3);
        if (iovs == null) return error;
        var rented = new TaskIoBuffer<Iovec>(iovs);
        try
        {
            return await rented.Track(DoWriteV(this, engine, (int)a1, iovs, (int)a3, offset, flags), engine.Owner as FiberTask);
        }
        finally
        {
            rented.Dispose();
        }
    }

    private ValueTask<int> SysLseek(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
        => ValueTask.FromResult((int)SeekFile((int)a1, (int)a2, (int)a3));

    private ValueTask<int> SysLlseek(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var position = SeekFile((int)a1, ((long)a2 << 32) | a3, (int)a5);
        if (position < 0) return ValueTask.FromResult((int)position);
        return ValueTask.FromResult(engine.CopyToUser(a4, BitConverter.GetBytes(position)) ? 0 : -(int)Errno.EFAULT);
    }

    private long SeekFile(int fd, long offset, int whence)
    {
        var file = GetIoFD(fd);
        if (file?.OpenedInode is not { } inode) return -(int)Errno.EBADF;
        if (inode.Type is InodeType.Fifo or InodeType.Socket) return -(int)Errno.ESPIPE;

        long position;
        try
        {
            position = whence switch
            {
                0 => offset,
                1 => checked(file.Position + offset),
                2 => checked((long)inode.Size + offset),
                _ => -1
            };
        }
        catch (OverflowException)
        {
            return -(int)Errno.EINVAL;
        }

        if (position < 0) return -(int)Errno.EINVAL;
        file.Position = position;
        return position;
    }

    private async ValueTask<int> SysClose(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // IN_CLOSE happens when the last reference to the open file description goes
        // away, which LinuxFile.Close reports; closing one dup'd descriptor must not.
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
            var inode = file.IoInode!;
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
