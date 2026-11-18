using System.Buffers;
using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998

    // ── setsockopt / getsockopt ──────────────────────────────────────────────
    //
    // For host sockets we only accept the small option subset we actually map.
    // Returning success for unsupported options makes socket behavior impossible
    // to reason about, especially for raw sockets.

    private async ValueTask<int> SysSetSockOpt(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var level = (int)a2;
        var optname = (int)a3;
        var optvalPtr = a4;
        var optlen = (int)a5;

        var file = GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        if (optvalPtr == 0 && optlen > 0) return -(int)Errno.EFAULT;
        if (optlen < 0) return -(int)Errno.EINVAL;

        var optval = new byte[optlen];
        if (optlen > 0 && !task.CPU.CopyFromUser(optvalPtr, optval)) return -(int)Errno.EFAULT;

        if (file.TryGetSocketOptionOps(out var ops))
        {
            var rc = ops.SetSocketOption(file, task, level, optname, optval);
            Logger.LogTrace("[SysSetSockOpt] fd={Fd} inode={Inode} level={Level} optname={Optname} len={Len} rc={Rc}",
                fd, file.OpenedInode?.GetType().Name, level, optname, optlen, rc);
            return rc;
        }

        if (file.OpenedInode is NetlinkSocketInode)
            return 0; // Netlink stub

        return -(int)Errno.ENOTSOCK;
    }

    private async ValueTask<int> SysGetSockOpt(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var level = (int)a2;
        var optname = (int)a3;
        var optvalPtr = a4;
        var optlenPtr = a5;

        var file = GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        if (optlenPtr == 0) return -(int)Errno.EFAULT;

        Span<byte> lenBuf = stackalloc byte[4];
        if (!task.CPU.CopyFromUser(optlenPtr, lenBuf)) return -(int)Errno.EFAULT;
        var optlen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        if (optlen < 0) return -(int)Errno.EINVAL;

        if (optvalPtr == 0 && optlen > 0) return -(int)Errno.EFAULT;

        var optval = new byte[optlen];

        if (file.TryGetSocketOptionOps(out var ops))
        {
            var rc = ops.GetSocketOption(file, task, level, optname, optval, out var written);
            Logger.LogTrace(
                "[SysGetSockOpt] fd={Fd} inode={Inode} level={Level} optname={Optname} requestedLen={Len} rc={Rc} written={Written}",
                fd, file.OpenedInode?.GetType().Name, level, optname, optlen, rc, written);
            if (rc < 0) return rc;

            if (written > 0 && optvalPtr != 0)
                if (!task.CPU.CopyToUser(optvalPtr, optval.AsSpan(0, written)))
                    return -(int)Errno.EFAULT;

            BinaryPrimitives.WriteInt32LittleEndian(lenBuf, written);
            if (!task.CPU.CopyToUser(optlenPtr, lenBuf)) return -(int)Errno.EFAULT;
            return 0;
        }

        if (file.OpenedInode is NetlinkSocketInode)
            return -(int)Errno.ENOPROTOOPT;

        return -(int)Errno.ENOTSOCK;
    }

    // ── sendmmsg ─────────────────────────────────────────────────────────────
    //
    // int sendmmsg(int sockfd, struct mmsghdr *msgvec, unsigned int vlen, int flags)
    //
    // struct mmsghdr { struct msghdr msg_hdr; unsigned int msg_len; }
    // We reuse SysSendMsg for each element and write back msg_len.

    private async ValueTask<int> SysSendMMsg(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var msgvec = a2;
        var vlen = (int)a3;
        var flags = (int)a4;

        if (vlen <= 0) return 0;

        // struct mmsghdr is 28+4 = 32 bytes on i386 (msghdr=28 + msg_len u32)
        const int mmsgHdrSize = 32;

        var sent = 0;
        for (var i = 0; i < vlen; i++)
        {
            var mmsgPtr = msgvec + (uint)(i * mmsgHdrSize);
            // SysSendMsg reads a struct msghdr at msgPtr.
            // We pass mmsgPtr directly — it starts with struct msghdr (28 bytes).
            var result = await SysSendMsg(engine, (uint)fd, mmsgPtr, (uint)flags, 0, 0, 0);
            if (result < 0)
            {
                if (sent == 0) return result; // no messages sent yet → propagate error
                break;
            }

            // Write back msg_len (offset 28 in mmsghdr)
            var lenBuf = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, (uint)result);
            task.CPU.CopyToUser(mmsgPtr + 28, lenBuf);

            sent++;
        }

        return sent;
    }

    // ── recvmmsg ─────────────────────────────────────────────────────────────
    //
    // int recvmmsg(int sockfd, struct mmsghdr *msgvec, unsigned int vlen,
    //              int flags, struct timespec *timeout)
    //
    // We receive up to vlen messages via SysRecvMsg, writing msg_len per entry.

    private async ValueTask<int> SysRecvMMsg(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var msgvec = a2;
        var vlen = (int)a3;
        var flags = (int)a4;
        // a5 = timeout ptr — we ignore it for now and treat as non-blocking batch

        if (vlen <= 0) return 0;

        const int mmsgHdrSize = 32;

        var received = 0;
        for (var i = 0; i < vlen; i++)
        {
            var mmsgPtr = msgvec + (uint)(i * mmsgHdrSize);
            var result = await SysRecvMsg(engine, (uint)fd, mmsgPtr, (uint)flags, 0, 0, 0);
            if (result < 0)
            {
                // EAGAIN on the first message with MSG_DONTWAIT or non-blocking → return 0 messages
                if (received == 0 && ((flags & 0x40) != 0 || result == -(int)Errno.EAGAIN))
                    return result;
                if (received == 0) return result;
                break;
            }

            // Write msg_len at offset 28
            var lenBuf = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, (uint)result);
            task.CPU.CopyToUser(mmsgPtr + 28, lenBuf);

            received++;

            // In non-blocking mode (MSG_DONTWAIT) or MSG_WAITFORONE, stop after first message
            if ((flags & 0x40) != 0 || (flags & 0x10000) != 0) break;
        }

        return received;
    }

    // ── splice ───────────────────────────────────────────────────────────────
    //
    // ssize_t splice(int fd_in, loff_t *off_in, int fd_out, loff_t *off_out,
    //                size_t len, unsigned int flags)
    //
    // Moves data between file descriptors without copying to user space.
    // At least one of fd_in/fd_out must be a pipe.
    // Buffered emulation; pipe-to-pipe transfers must not consume input until
    // output has accepted it (including nonblocking/backpressure failure).

    private ValueTask<int> SysSplice(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return ValueTask.FromResult(-(int)Errno.EPERM);

        var fdIn = (int)a1;
        var offIn = a2; // loff_t* — pointer to guest offset, or 0 for current position
        var fdOut = (int)a3;
        var offOut = a4;
        var len = (int)a5;
        var flags = (int)a6; // SPLICE_F_NONBLOCK=0x2, SPLICE_F_MORE=0x4, etc.

        if (len <= 0) return ValueTask.FromResult(0);

        var fileIn = GetFD(fdIn);
        var fileOut = GetFD(fdOut);
        if (fileIn == null || fileOut == null) return ValueTask.FromResult(-(int)Errno.EBADF);
        var lease = new FileIoLease(fileIn, fileOut);
        try { return lease.Complete(DoSplice(engine, task, fileIn, fileOut, offIn, offOut, len, flags), task); }
        finally { lease.Dispose(); }
    }

    private static async ValueTask<int> DoSplice(Engine engine, FiberTask task, LinuxFile fileIn,
        LinuxFile fileOut, uint offIn, uint offOut, int len, int flags)
    {
        // At least one FD must be a pipe (enforced by Linux; we relax here for compatibility)
        var inIsPipe = fileIn.IoInode is PipeInode;
        var outIsPipe = fileOut.IoInode is PipeInode;
        if (!inIsPipe && !outIsPipe) return -(int)Errno.EINVAL;
        if (inIsPipe && offIn != 0) return -(int)Errno.ESPIPE;
        if (outIsPipe && offOut != 0) return -(int)Errno.ESPIPE;

        // Only regular files, sockets and character devices support splice on the
        // file side; inotify/eventfd/epoll descriptors must fail fast, not block.
        if (!inIsPipe &&
            fileIn.OpenedInode!.Type is not (InodeType.File or InodeType.Socket or InodeType.CharDev))
            return -(int)Errno.EINVAL;
        if (!outIsPipe &&
            fileOut.OpenedInode!.Type is not (InodeType.File or InodeType.Socket or InodeType.CharDev))
            return -(int)Errno.EINVAL;

        // Unconnected sockets cannot be spliced.
        if (fileIn.OpenedInode is UnixSocketInode inSock && !inSock.IsConnected)
            return -(int)Errno.EINVAL;
        if (fileOut.OpenedInode is UnixSocketInode outSock && !outSock.IsConnected)
            return -(int)Errno.EINVAL;

        // splice(2) requires fd_in to be readable and fd_out to be writable.
        if ((fileIn.Flags & (FileFlags.O_PATH | FileFlags.O_ACCMODE)) == FileFlags.O_PATH)
            return -(int)Errno.EBADF;
        if ((fileOut.Flags & (FileFlags.O_PATH | FileFlags.O_ACCMODE)) == FileFlags.O_PATH)
            return -(int)Errno.EBADF;
        if ((fileIn.Flags & FileFlags.O_ACCMODE) == FileFlags.O_WRONLY) return -(int)Errno.EBADF;
        if ((fileOut.Flags & FileFlags.O_ACCMODE) == FileFlags.O_RDONLY) return -(int)Errno.EBADF;
        if ((fileOut.Flags & FileFlags.O_APPEND) != 0) return -(int)Errno.EINVAL;

        if (fileIn.IoInode is PipeInode sourcePipe && fileOut.IoInode is PipeInode targetPipe)
        {
            if (ReferenceEquals(sourcePipe, targetPipe)) return -(int)Errno.EINVAL;
            return await SplicePipes(task, fileIn, sourcePipe, fileOut, targetPipe, len, flags);
        }

        // Resolve read offset
        var readOffset = fileIn.Position;
        if (offIn != 0)
        {
            var offBytes = new byte[8];
            if (!task.CPU.CopyFromUser(offIn, offBytes)) return -(int)Errno.EFAULT;
            readOffset = BitConverter.ToInt64(offBytes);
        }

        // Resolve write offset
        var writeOffset = fileOut.Position;
        if (offOut != 0)
        {
            var offBytes = new byte[8];
            if (!task.CPU.CopyFromUser(offOut, offBytes)) return -(int)Errno.EFAULT;
            writeOffset = BitConverter.ToInt64(offBytes);
        }

        var bufSize = Math.Min(len, 65536);
        var buf = ArrayPool<byte>.Shared.Rent(bufSize);
        var rented = new TaskIoBuffer<byte>(buf);
        var totalTransferred = 0;

        try
        {
            var remaining = len;
            while (remaining > 0)
            {
                var toRead = Math.Min(remaining, bufSize);
                var bytesRead = fileIn.IoInode!.ReadToHost(task, fileIn, buf.AsSpan(0, toRead), readOffset);

                if (bytesRead == 0) break; // EOF
                if (bytesRead == -(int)Errno.EAGAIN)
                {
                    if (totalTransferred > 0) break;
                    // If non-blocking and no data → EAGAIN
                    if ((flags & 2) != 0 || (fileIn.Flags & FileFlags.O_NONBLOCK) != 0)
                        return -(int)Errno.EAGAIN;
                    // Otherwise wait for data (pipe read-ready)
                    var waitResult = await rented.Track(fileIn.IoInode.WaitForRead(fileIn, task), task);
                    if (waitResult == AwaitResult.Interrupted)
                        return totalTransferred > 0 ? totalTransferred : -(int)Errno.ERESTARTSYS;
                    continue;
                }

                if (bytesRead < 0) return bytesRead;

                var writeConsumed = 0;
                while (writeConsumed < bytesRead)
                {
                    var bytesWritten = fileOut.IoInode!.WriteFromHost(task, fileOut,
                        buf.AsSpan(writeConsumed, bytesRead - writeConsumed), writeOffset);

                    if (bytesWritten == -(int)Errno.EPIPE)
                    {
                        if (engine.Owner is FiberTask fiberTask) fiberTask.PostSignal((int)Signal.SIGPIPE);
                        if (totalTransferred > 0) break;
                        return bytesWritten;
                    }

                    if (bytesWritten == -(int)Errno.EAGAIN)
                    {
                        if (totalTransferred > 0) break;
                        if ((flags & 2) != 0 || (fileOut.Flags & FileFlags.O_NONBLOCK) != 0)
                            return -(int)Errno.EAGAIN;
                        var waitResult = await rented.Track(fileOut.IoInode.WaitForWrite(fileOut, task), task);
                        if (waitResult == AwaitResult.Interrupted)
                            return totalTransferred > 0 ? totalTransferred : -(int)Errno.ERESTARTSYS;
                        continue;
                    }

                    if (bytesWritten < 0)
                    {
                        if (totalTransferred > 0) break;
                        return bytesWritten;
                    }

                    writeConsumed += bytesWritten;
                    writeOffset += bytesWritten;
                    totalTransferred += bytesWritten;
                    remaining -= bytesWritten;
                }

                readOffset += bytesRead;
            }

            // Update guest offsets if ptrs were provided
            if (offIn != 0)
                task.CPU.CopyToUser(offIn, BitConverter.GetBytes(readOffset));
            else
                fileIn.Position = readOffset;

            if (offOut != 0)
                task.CPU.CopyToUser(offOut, BitConverter.GetBytes(writeOffset));
            else
                fileOut.Position = writeOffset;

            return totalTransferred;
        }
        finally
        {
            rented.Dispose();
        }
    }

    private static async ValueTask<int> SplicePipes(FiberTask task, LinuxFile input, PipeInode source,
        LinuxFile output, PipeInode target, int length, int flags)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(length, 65536));
        var rented = new TaskIoBuffer<byte>(buffer);
        try
        {
            while (true)
            {
                var available = source.Peek(buffer.AsSpan(0, Math.Min(length, 65536)));
                if (available == -(int)Errno.EAGAIN)
                {
                    if ((flags & SpliceFNonBlock) != 0 || (input.Flags & FileFlags.O_NONBLOCK) != 0)
                        return available;
                    if (await rented.Track(source.WaitForRead(input, task), task) == AwaitResult.Interrupted)
                        return -(int)Errno.ERESTARTSYS;
                    continue;
                }
                if (available <= 0) return available;

                // Splice may make partial progress below PIPE_BUF; it is not an
                // atomic user write. Peek, write and consume never suspend.
                var amount = Math.Min(available, Math.Max(1, target.GetWritableBytes()));
                var written = target.WriteFromHost(task, output, buffer.AsSpan(0, amount), 0);
                if (written == -(int)Errno.EAGAIN)
                {
                    if ((flags & SpliceFNonBlock) != 0 || (output.Flags & FileFlags.O_NONBLOCK) != 0)
                        return written;
                    if (await rented.Track(target.WaitForWrite(output, task), task) == AwaitResult.Interrupted)
                        return -(int)Errno.ERESTARTSYS;
                    // Another reader may have consumed the source during wait.
                    continue;
                }
                if (written == -(int)Errno.EPIPE) task.PostSignal((int)Signal.SIGPIPE);
                if (written <= 0) return written;
                source.ConsumeSpliced(written);
                return written;
            }
        }
        finally
        {
            rented.Dispose();
        }
    }

    // ── tee ──────────────────────────────────────────────────────────────────
    //
    // ssize_t tee(int fd_in, int fd_out, size_t len, unsigned int flags)
    //
    // Duplicates data between two pipe file descriptors *without* consuming
    // the data from fd_in (peek semantics).

    private ValueTask<int> SysTee(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return ValueTask.FromResult(-(int)Errno.EPERM);

        var fdIn = (int)a1;
        var fdOut = (int)a2;
        var len = (int)a3;
        var flags = (int)a4;

        if (len <= 0) return ValueTask.FromResult(0);

        var fileIn = GetFD(fdIn);
        var fileOut = GetFD(fdOut);
        if (fileIn == null || fileOut == null) return ValueTask.FromResult(-(int)Errno.EBADF);
        var lease = new FileIoLease(fileIn, fileOut);
        try { return lease.Complete(DoTee(task, fileIn, fileOut, len, flags), task); }
        finally { lease.Dispose(); }
    }

    private static async ValueTask<int> DoTee(FiberTask task, LinuxFile fileIn, LinuxFile fileOut, int len, int flags)
    {
        if (fileIn.IoInode is not PipeInode pipeIn) return -(int)Errno.EINVAL;
        if (fileOut.IoInode is not PipeInode pipeOut) return -(int)Errno.EINVAL;
        if (ReferenceEquals(pipeIn, pipeOut)) return -(int)Errno.EINVAL;

        var bufSize = Math.Min(len, 65536);
        var buf = ArrayPool<byte>.Shared.Rent(bufSize);
        var rented = new TaskIoBuffer<byte>(buf);

        try
        {
            // tee() is a single-shot operation: peek available data (up to len)
            // from fd_in without consuming, write it to fd_out, return count.
            while (true)
            {
                var bytesRead = pipeIn.Peek(buf.AsSpan(0, bufSize));
                if (bytesRead == 0) return 0; // EOF
                if (bytesRead == -(int)Errno.EAGAIN)
                {
                    if ((flags & 2) != 0 || (fileIn.Flags & FileFlags.O_NONBLOCK) != 0)
                        return -(int)Errno.EAGAIN;
                    var waitResult = await rented.Track(fileIn.IoInode.WaitForRead(fileIn, task), task);
                    if (waitResult == AwaitResult.Interrupted)
                        return -(int)Errno.ERESTARTSYS;
                    continue; // retry after data arrives
                }

                if (bytesRead < 0) return bytesRead;

                var bytesWritten =
                    fileOut.IoInode!.WriteFromHost(task, fileOut, buf.AsSpan(0, bytesRead), fileOut.Position);
                if (bytesWritten == -(int)Errno.EAGAIN)
                {
                    if ((flags & 2) != 0 || (fileOut.Flags & FileFlags.O_NONBLOCK) != 0)
                        return -(int)Errno.EAGAIN;
                    var waitResult = await rented.Track(fileOut.IoInode.WaitForWrite(fileOut, task), task);
                    if (waitResult == AwaitResult.Interrupted)
                        return -(int)Errno.ERESTARTSYS;
                    continue;
                }

                if (bytesWritten < 0) return bytesWritten;

                return bytesWritten;
            }
        }
        finally
        {
            rented.Dispose();
        }
    }

#pragma warning restore CS1998
}
