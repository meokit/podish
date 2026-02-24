using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fiberish.Auth.Permission;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998 // Async method lacks await operators
    internal static async ValueTask<int> SysSendfile(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // ssize_t sendfile(int out_fd, int in_fd, off_t *offset, size_t count);
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var outFd = (int)a1;
        var inFd = (int)a2;
        var offsetPtr = a3;
        var count = (int)a4;

        long? offset = null;
        if (offsetPtr != 0)
        {
            var offsetBytes = new byte[4];
            if (!sm.Engine.CopyFromUser(offsetPtr, offsetBytes)) return -(int)Errno.EFAULT;
            offset = BitConverter.ToInt32(offsetBytes);
        }

        var (result, newOffset) = await DoSendfile(sm, outFd, inFd, offset, count);

        if (result >= 0 && offsetPtr != 0 && offset.HasValue)
            if (!sm.Engine.CopyToUser(offsetPtr, BitConverter.GetBytes((uint)offset.Value)))
                return -(int)Errno.EFAULT;

        return result;
    }

    internal static async ValueTask<int> SysSendfile64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        // ssize_t sendfile64(int out_fd, int in_fd, off64_t *offset, size_t count);
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var outFd = (int)a1;
        var inFd = (int)a2;
        var offsetPtr = a3;
        var count = (int)a4;

        long? offset = null;
        if (offsetPtr != 0)
        {
            var offsetBytes = new byte[8];
            if (!sm.Engine.CopyFromUser(offsetPtr, offsetBytes)) return -(int)Errno.EFAULT;
            offset = BitConverter.ToInt64(offsetBytes);
        }

        var (result, newOffset) = await DoSendfile(sm, outFd, inFd, offset, count);

        if (result >= 0 && offsetPtr != 0 && newOffset.HasValue)
            if (!sm.Engine.CopyToUser(offsetPtr, BitConverter.GetBytes(newOffset.Value)))
                return -(int)Errno.EFAULT;

        return result;
    }

    private static async ValueTask<(int, long?)> DoSendfile(SyscallManager sm, int outFd, int inFd, long? offset,
        int count)
    {
        if (!sm.FDs.TryGetValue(inFd, out var inFile) || !sm.FDs.TryGetValue(outFd, out var outFile))
            return (-(int)Errno.EBADF, null);
        if (inFile == null || outFile == null) return (-(int)Errno.EBADF, null);

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
                var bytesRead = inFile.Dentry.Inode!.Read(inFile, buffer.AsSpan(0, toRead), readOffset);

                if (bytesRead <= 0)
                {
                    if (bytesRead == 0) // EOF
                        break;
                    if (bytesRead == -(int)Errno.EAGAIN)
                    {
                        if (totalWritten > 0) break;
                        if ((inFile.Flags & FileFlags.O_NONBLOCK) != 0) return (-(int)Errno.EAGAIN, null);
                        if (sm.Engine.Owner is not FiberTask fiberTask) return (-(int)Errno.EAGAIN, null);
                        if (await new IOAwaiter(inFile, true, fiberTask) == AwaitResult.Interrupted)
                            return (-(int)Errno.ERESTARTSYS, null);
                        continue;
                    }

                    if (totalWritten > 0)
                        break;
                    return (bytesRead, null); // Return error code
                }

                if (!offset.HasValue) inFile.Position += bytesRead;

                // Write to out_fd
                var bytesWritten = outFile.Dentry.Inode!.Write(outFile, buffer.AsSpan(0, bytesRead), outFile.Position);

                if (bytesWritten < 0)
                {
                    if (bytesWritten == -(int)Errno.EAGAIN)
                    {
                        if (totalWritten > 0) break;
                        if ((outFile.Flags & FileFlags.O_NONBLOCK) != 0) return (-(int)Errno.EAGAIN, null);
                        if (sm.Engine.Owner is FiberTask fiberTask)
                        {
                            if (await new IOAwaiter(outFile, false, fiberTask) == AwaitResult.Interrupted)
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

    private static async ValueTask<int> SysPipe(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var fdsAddr = a1;

        try
        {
            var pipe = new PipeInode();

            // Reader
            var rDentry = new Dentry("pipe:[read]", pipe, sm.Root, sm.Root.SuperBlock);
            var rFile = new VFS.LinuxFile(rDentry, FileFlags.O_RDONLY);
            var rFd = sm.AllocFD(rFile);
            // pipe.AddReader(); // Handled by File ctor -> Inode.Open

            // Writer
            var wDentry = new Dentry("pipe:[write]", pipe, sm.Root, sm.Root.SuperBlock);
            var wFile = new VFS.LinuxFile(wDentry, FileFlags.O_WRONLY);
            var wFd = sm.AllocFD(wFile);
            // pipe.AddWriter(); // Handled by File ctor -> Inode.Open

            // Write FDs to user memory
            // Write FDs to user memory
            var fds = new[] { rFd, wFd };
            if (!sm.Engine.CopyToUser(fdsAddr, MemoryMarshal.AsBytes(fds.AsSpan())))
                return -(int)Errno.EFAULT;

            return 0;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SysPipe failed");
            return -(int)Errno.ENFILE;
        }
    }

    private static async ValueTask<int> SysCreat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // creat(path, mode) is open(path, O_CREAT|O_WRONLY|O_TRUNC, mode)
        return await SysOpen(state, a1, (uint)(FileFlags.O_CREAT | FileFlags.O_WRONLY | FileFlags.O_TRUNC), a2, a4, a5,
            a6);
    }

    private static async ValueTask<int> SysMemfdCreate(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        const uint MFD_CLOEXEC = 0x0001;
        const uint MFD_ALLOW_SEALING = 0x0002;

        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var name = sm.Engine.ReadStringSafe(a1);
        if (name == null) return -(int)Errno.EFAULT;

        var flags = a2;
        var knownFlags = MFD_CLOEXEC | MFD_ALLOW_SEALING;
        if ((flags & ~knownFlags) != 0) return -(int)Errno.EINVAL;

        try
        {
            var inode = sm.MemfdSuperBlock.AllocInode();
            inode.Type = InodeType.File;
            inode.Mode = 0x180; // 0600

            var t = sm.Engine.Owner as FiberTask;
            inode.Uid = t?.Process.EUID ?? 0;
            inode.Gid = t?.Process.EGID ?? 0;

            var display = string.IsNullOrEmpty(name) ? "memfd:anon" : $"memfd:{name}";
            var dentry = new Dentry(display, inode, sm.MemfdSuperBlock.Root, sm.MemfdSuperBlock);

            var fdFlags = FileFlags.O_RDWR;
            if ((flags & MFD_CLOEXEC) != 0) fdFlags |= FileFlags.O_CLOEXEC;
            var file = new VFS.LinuxFile(dentry, fdFlags);
            return sm.AllocFD(file);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SysMemfdCreate failed");
            return -(int)Errno.ENOMEM;
        }
    }

    private static int ImplOpen(SyscallManager sm, string path, uint flags, uint mode, Dentry? startAt = null)
    {
        Logger.LogInformation($"[Open] Path='{path}' Flags={flags} Mode={mode}");
        var createdHere = false;
        var dentry = sm.PathWalk(path, startAt);
        if (dentry == null)
        {
            if ((flags & (uint)FileFlags.O_CREAT) != 0)
            {
                var lastSlash = path.LastIndexOf('/');
                var parentPath = lastSlash == -1 ? "" : lastSlash == 0 ? "/" : path[..lastSlash];
                var name = lastSlash == -1 ? path : path[(lastSlash + 1)..];

                var parentDentry = sm.PathWalk(parentPath == "" ? "." : parentPath, startAt);
                if (parentDentry == null || parentDentry.Inode == null) return -(int)Errno.ENOENT;

                var t = sm.Engine.Owner as FiberTask;
                var uid = t?.Process.EUID ?? 0;
                var gid = t?.Process.EGID ?? 0;

                try
                {
                    dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
                    var finalMode = DacPolicy.ApplyUmask((int)mode, t?.Process.Umask ?? 0);
                    parentDentry.Inode.Create(dentry, finalMode, uid, gid);
                    createdHere = true;
                }
                catch (InvalidOperationException)
                {
                    // Hostfs uses InvalidOperationException("Exists") for collisions.
                    return -(int)Errno.EEXIST;
                }
                catch
                {
                    return -(int)Errno.EACCES;
                }
            }
            else
            {
                return -(int)Errno.ENOENT;
            }
        }
        else
        {
            // File exists - check for O_EXCL
            if ((flags & (uint)FileFlags.O_CREAT) != 0 && (flags & (uint)FileFlags.O_EXCL) != 0)
                return -(int)Errno.EEXIST;
        }

        try
        {
            // If we already created the inode above, opening with O_CREAT|O_EXCL can
            // retrigger create semantics in backend Open() and fail spuriously.
            var openFlags = createdHere
                ? flags & ~(uint)(FileFlags.O_CREAT | FileFlags.O_EXCL | FileFlags.O_TRUNC)
                : flags;
            var f = new VFS.LinuxFile(dentry, (FileFlags)openFlags);
            return sm.AllocFD(f);
        }
        catch
        {
            return -1;
        }
    }

    private static async ValueTask<int> SysOpen(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;

        var path = sm.Engine.ReadStringSafe(a1);
        if (path == null) return -(int)Errno.EFAULT;

        return ImplOpen(sm, path, a2, a3);
    }

    private static async ValueTask<int> SysOpenAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var dirfd = (int)a1;
        var path = sm.Engine.ReadStringSafe(a2);
        if (path == null) return -(int)Errno.EFAULT;

        var flags = a3;
        var mode = a4;

        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        return ImplOpen(sm, path, flags, mode, startAt);
    }

    private static async ValueTask<int> SysOpenAt2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var dirfd = (int)a1;
        var path = sm.Engine.ReadStringSafe(a2);
        if (path == null) return -(int)Errno.EFAULT;

        var howPtr = a3;
        var howSize = a4;

        if (howSize < 24) return -(int)Errno.EINVAL;

        var howBuf = new byte[24];
        if (!sm.Engine.CopyFromUser(howPtr, howBuf)) return -(int)Errno.EFAULT;

        var flags = BinaryPrimitives.ReadUInt64LittleEndian(howBuf.AsSpan(0, 8));
        var mode = BinaryPrimitives.ReadUInt64LittleEndian(howBuf.AsSpan(8, 8));

        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        return ImplOpen(sm, path, (uint)flags, (uint)mode, startAt);
    }

    private static async ValueTask<int> SysDup(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var oldfd = (int)a1;
        var f = sm.GetFD(oldfd);
        if (f == null) return -(int)Errno.EBADF;

        f.Get();
        return sm.AllocFD(f);
    }

    private static async ValueTask<int> SysDup2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var oldfd = (int)a1;
        var newfd = (int)a2;

        if (oldfd == newfd)
        {
            if (sm.GetFD(oldfd) == null) return -(int)Errno.EBADF;
            return newfd;
        }

        var f = sm.GetFD(oldfd);
        if (f == null) return -(int)Errno.EBADF;

        sm.FreeFD(newfd);
        sm.FDs[newfd] = f;
        f.Get();
        return newfd;
    }

    private static async ValueTask<int> SysDup3(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // For now ignore flags like O_CLOEXEC
        return await SysDup2(state, a1, a2, a3, a4, a5, a6);
    }


    private struct Iovec
    {
        public uint BaseAddr;
        public uint Len;
    }

    private static async ValueTask<int> DoReadV(SyscallManager sm, int fd, Iovec[] iovs, int iovCnt, long offset,
        int flags)
    {
        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        var updatePosition = offset == -1;
        var currentOffset = updatePosition ? f.Position : offset;
        var totalRead = 0;

        for (var i = 0; i < iovCnt; i++)
        {
            var iov = iovs[i];
            if (iov.Len == 0) continue;

            var buf = ArrayPool<byte>.Shared.Rent((int)iov.Len);
            try
            {
                while (true)
                {
                    var n = f.Dentry.Inode!.Read(f, buf.AsSpan(0, (int)iov.Len), currentOffset);

                    if (n == -(int)Errno.EAGAIN)
                    {
                        if ((f.Flags & FileFlags.O_NONBLOCK) != 0 || (flags & 0x00000008) != 0 /* RWF_NOWAIT */)
                            return totalRead > 0 ? totalRead : -(int)Errno.EAGAIN;

                        if (sm.Engine.Owner is not FiberTask fiberTask)
                            return totalRead > 0 ? totalRead : -(int)Errno.EAGAIN;

                        if (await new IOAwaiter(f, true, fiberTask) == AwaitResult.Interrupted)
                            return totalRead > 0 ? totalRead : -(int)Errno.ERESTARTSYS;
                        continue;
                    }

                    if (n > 0)
                    {
                        if (!sm.Engine.CopyToUser(iov.BaseAddr, buf.AsSpan(0, n))) return -(int)Errno.EFAULT;
                        totalRead += n;
                        currentOffset += n;
                        if (n < iov.Len)
                        {
                            if (updatePosition) f.Position = currentOffset;
                            return totalRead;
                        }

                        break;
                    }

                    if (n == 0) // EOF
                    {
                        if (updatePosition) f.Position = currentOffset;
                        return totalRead;
                    }

                    return totalRead > 0 ? totalRead : n;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        if (updatePosition) f.Position = currentOffset;

        return totalRead;
    }

    private static async ValueTask<int> DoWriteV(SyscallManager sm, int fd, Iovec[] iovs, int iovCnt, long offset,
        int flags)
    {
        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        var updatePosition = offset == -1;
        var append = (f.Flags & FileFlags.O_APPEND) != 0;
        var currentOffset = updatePosition
            ? (append ? (long)(f.Dentry.Inode?.Size ?? 0) : f.Position)
            : offset;
        var totalWritten = 0;

        for (var i = 0; i < iovCnt; i++)
        {
            var iov = iovs[i];
            if (iov.Len == 0) continue;

            var data = ArrayPool<byte>.Shared.Rent((int)iov.Len);
            try
            {
                if (!sm.Engine.CopyFromUser(iov.BaseAddr, data.AsSpan(0, (int)iov.Len))) return -(int)Errno.EFAULT;

                while (true)
                {
                    var n = f.Dentry.Inode!.Write(f, data.AsSpan(0, (int)iov.Len), currentOffset);

                    if (n == -(int)Errno.EPIPE)
                    {
                        if (sm.Engine.Owner is FiberTask fiberTask) fiberTask.PostSignal((int)Signal.SIGPIPE);
                        return n;
                    }

                    if (n == -(int)Errno.EAGAIN)
                    {
                        if ((f.Flags & FileFlags.O_NONBLOCK) != 0 || (flags & 0x00000008) != 0 /* RWF_NOWAIT */)
                            return totalWritten > 0 ? totalWritten : -(int)Errno.EAGAIN;

                        if (sm.Engine.Owner is not FiberTask fiberTask)
                            return totalWritten > 0 ? totalWritten : -(int)Errno.EAGAIN;

                        if (await new IOAwaiter(f, false, fiberTask) == AwaitResult.Interrupted)
                            return totalWritten > 0 ? totalWritten : -(int)Errno.ERESTARTSYS;
                        continue;
                    }

                    if (n > 0)
                    {
                        totalWritten += n;
                        currentOffset += n;
                        if (n < iov.Len)
                        {
                            if (updatePosition) f.Position = currentOffset;
                            return totalWritten;
                        }

                        break;
                    }

                    return totalWritten > 0 ? totalWritten : n;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(data);
            }
        }

        if (updatePosition) f.Position = currentOffset;

        return totalWritten;
    }

    private static Iovec[]? ReadIovecs(SyscallManager sm, uint iovAddr, int iovCnt)
    {
        if (iovCnt < 0 || iovCnt > 1024) return null;
        var iovs = ArrayPool<Iovec>.Shared.Rent(iovCnt);
        var iovecBytes = ArrayPool<byte>.Shared.Rent(iovCnt * 8);

        try
        {
            if (!sm.Engine.CopyFromUser(iovAddr, iovecBytes.AsSpan(0, iovCnt * 8))) return null;

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

    internal static async ValueTask<int> SysRead(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var iovs = ArrayPool<Iovec>.Shared.Rent(1);
        iovs[0] = new Iovec { BaseAddr = a2, Len = a3 };
        try
        {
            return await DoReadV(sm, (int)a1, iovs, 1, -1, 0);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    internal static async ValueTask<int> SysWrite(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var iovs = ArrayPool<Iovec>.Shared.Rent(1);
        iovs[0] = new Iovec { BaseAddr = a2, Len = a3 };
        try
        {
            return await DoWriteV(sm, (int)a1, iovs, 1, -1, 0);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private static async ValueTask<int> SysPRead(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var offset = a4 | ((long)a5 << 32);
        var iovs = ArrayPool<Iovec>.Shared.Rent(1);
        iovs[0] = new Iovec { BaseAddr = a2, Len = a3 };
        try
        {
            return await DoReadV(sm, (int)a1, iovs, 1, offset, 0);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private static async ValueTask<int> SysPWrite(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var offset = a4 | ((long)a5 << 32);
        var iovs = ArrayPool<Iovec>.Shared.Rent(1);
        iovs[0] = new Iovec { BaseAddr = a2, Len = a3 };
        try
        {
            return await DoWriteV(sm, (int)a1, iovs, 1, offset, 0);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private static async ValueTask<int> SysReadV(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var iovs = ReadIovecs(sm, a2, (int)a3);
        if (iovs == null) return -(int)Errno.EFAULT; // Simplification, could be EINVAL
        try
        {
            return await DoReadV(sm, (int)a1, iovs, (int)a3, -1, 0);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private static async ValueTask<int> SysWriteV(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var iovs = ReadIovecs(sm, a2, (int)a3);
        if (iovs == null) return -(int)Errno.EFAULT;
        try
        {
            return await DoWriteV(sm, (int)a1, iovs, (int)a3, -1, 0);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private static async ValueTask<int> SysPReadV(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var offset = a4 | ((long)a5 << 32);
        var iovs = ReadIovecs(sm, a2, (int)a3);
        if (iovs == null) return -(int)Errno.EFAULT;
        try
        {
            return await DoReadV(sm, (int)a1, iovs, (int)a3, offset, 0);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private static async ValueTask<int> SysPWriteV(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var offset = a4 | ((long)a5 << 32);
        var iovs = ReadIovecs(sm, a2, (int)a3);
        if (iovs == null) return -(int)Errno.EFAULT;
        try
        {
            return await DoWriteV(sm, (int)a1, iovs, (int)a3, offset, 0);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private static async ValueTask<int> SysPReadV2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var offset = a4 | ((long)a5 << 32);
        var flags = (int)a6;
        var iovs = ReadIovecs(sm, a2, (int)a3);
        if (iovs == null) return -(int)Errno.EFAULT;
        try
        {
            return await DoReadV(sm, (int)a1, iovs, (int)a3, offset, flags);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private static async ValueTask<int> SysPWriteV2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var offset = a4 | ((long)a5 << 32);
        var flags = (int)a6;
        var iovs = ReadIovecs(sm, a2, (int)a3);
        if (iovs == null) return -(int)Errno.EFAULT;
        try
        {
            return await DoWriteV(sm, (int)a1, iovs, (int)a3, offset, flags);
        }
        finally
        {
            ArrayPool<Iovec>.Shared.Return(iovs);
        }
    }

    private static async ValueTask<int> SysLseek(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        var fd = (int)a1;
        var offset = (long)(int)a2; // signed offset
        var whence = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        var newPos = whence switch
        {
            0 => offset, // SEEK_SET
            1 => f.Position + offset, // SEEK_CUR
            2 => (long)f.Dentry.Inode!.Size + offset, // SEEK_END
            _ => -1
        };

        if (newPos < 0) return -(int)Errno.EINVAL;
        f.Position = newPos;
        return (int)newPos;
    }

    private static async ValueTask<int> SysLlseek(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        var fd = (int)a1;
        var offset = ((long)a2 << 32) | a3;
        var resultPtr = a4;
        var whence = (int)a5;

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        var newPos = whence switch
        {
            0 => offset, // SEEK_SET
            1 => f.Position + offset, // SEEK_CUR
            2 => (long)f.Dentry.Inode!.Size + offset, // SEEK_END
            _ => -1
        };

        if (newPos < 0) return -(int)Errno.EINVAL;
        f.Position = newPos;

        if (!sm.Engine.CopyToUser(resultPtr, BitConverter.GetBytes(newPos)))
            return -(int)Errno.EFAULT;

        return 0;
    }

    private static async ValueTask<int> SysClose(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        sm.FreeFD((int)a1);
        return 0;
    }


    // --- IO Awaiter ---
    public class IOAwaiter : INotifyCompletion
    {
        private readonly VFS.LinuxFile _file;
        private readonly bool _forRead;
        private readonly FiberTask _task;

        public IOAwaiter(VFS.LinuxFile file, bool forRead, FiberTask task)
        {
            _file = file;
            _forRead = forRead;
            _task = task;
        }

        public bool IsCompleted => false;

        public void OnCompleted(Action continuation)
        {
            if (_task.HasUnblockedPendingSignal())
            {
                _task.WakeReason = WakeReason.Signal;
                KernelScheduler.Current?.Schedule(continuation, _task);
                return;
            }

            var runOnce = new RunOnceAction(continuation, _task);

            var registered = _file.Dentry.Inode!.RegisterWait(_file, () =>
            {
                _task.WakeReason = WakeReason.IO;
                runOnce.Invoke();
            }, (short)(_forRead ? 0x0001 : 0x0004));

            if (!registered)
            {
                _task.WakeReason = WakeReason.IO;
                runOnce.Invoke();
            }
        }

        public AwaitResult GetResult()
        {
            if (_task.WakeReason != WakeReason.IO && _task.WakeReason != WakeReason.None)
            {
                _task.WakeReason = WakeReason.None;
                return AwaitResult.Interrupted;
            }

            _task.WakeReason = WakeReason.None;
            return AwaitResult.Completed;
        }

        public IOAwaiter GetAwaiter()
        {
            return this;
        }

        private class RunOnceAction(Action action, FiberTask task)
        {
            private int _called;

            public void Invoke()
            {
                if (Interlocked.Exchange(ref _called, 1) == 0)
                {
                    task.Continuation = action;
                    KernelScheduler.Current?.Schedule(task);
                }
            }
        }
    }

    private static async ValueTask<int> SysReadahead(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0; // Success (hint)
    }

    private static async ValueTask<int> SysFadvise64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0; // Success (hint)
    }
}
