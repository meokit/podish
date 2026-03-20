using System.Buffers;
using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

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
            return ops.SetSocketOption(file, task, level, optname, optval);

        if (file.OpenedInode is NetlinkRouteSocketInode)
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
            if (rc < 0) return rc;

            if (written > 0 && optvalPtr != 0)
                if (!task.CPU.CopyToUser(optvalPtr, optval.AsSpan(0, written)))
                    return -(int)Errno.EFAULT;

            BinaryPrimitives.WriteInt32LittleEndian(lenBuf, written);
            if (!task.CPU.CopyToUser(optlenPtr, lenBuf)) return -(int)Errno.EFAULT;
            return 0;
        }

        if (file.OpenedInode is NetlinkRouteSocketInode)
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
    // Our implementation performs a buffered copy via kernel buffer,
    // which is semantically correct even if not zero-copy.

    private async ValueTask<int> SysSplice(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var fdIn = (int)a1;
        var offIn = a2; // loff_t* — pointer to guest offset, or 0 for current position
        var fdOut = (int)a3;
        var offOut = a4;
        var len = (int)a5;
        var flags = (int)a6; // SPLICE_F_NONBLOCK=0x2, SPLICE_F_MORE=0x4, etc.

        if (len <= 0) return 0;

        var fileIn = GetFD(fdIn);
        var fileOut = GetFD(fdOut);
        if (fileIn == null || fileOut == null) return -(int)Errno.EBADF;

        // At least one FD must be a pipe (enforced by Linux; we relax here for compatibility)
        var inIsPipe = fileIn.OpenedInode is PipeInode;
        var outIsPipe = fileOut.OpenedInode is PipeInode;
        if (!inIsPipe && !outIsPipe) return -(int)Errno.EINVAL;
        if (inIsPipe && offIn != 0) return -(int)Errno.ESPIPE;
        if (outIsPipe && offOut != 0) return -(int)Errno.ESPIPE;

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
        var totalTransferred = 0;

        try
        {
            var remaining = len;
            while (remaining > 0)
            {
                var toRead = Math.Min(remaining, bufSize);
                var bytesRead = fileIn.OpenedInode!.Read(fileIn, buf.AsSpan(0, toRead), readOffset);

                if (bytesRead == 0) break; // EOF
                if (bytesRead == -(int)Errno.EAGAIN)
                {
                    if (totalTransferred > 0) break;
                    // If non-blocking and no data → EAGAIN
                    if ((flags & 2) != 0 || (fileIn.Flags & FileFlags.O_NONBLOCK) != 0)
                        return -(int)Errno.EAGAIN;
                    // Otherwise wait for data (pipe read-ready)
                    await fileIn.OpenedInode.WaitForRead(fileIn, task);
                    continue;
                }

                if (bytesRead < 0) return bytesRead;

                var writeConsumed = 0;
                while (writeConsumed < bytesRead)
                {
                    var bytesWritten = fileOut.OpenedInode!.Write(fileOut,
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
                        await fileOut.OpenedInode.WaitForWrite(fileOut, task);
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
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    // ── tee ──────────────────────────────────────────────────────────────────
    //
    // ssize_t tee(int fd_in, int fd_out, size_t len, unsigned int flags)
    //
    // Duplicates data between two pipe file descriptors *without* consuming
    // the data from fd_in (peek semantics).

    private async ValueTask<int> SysTee(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var fdIn = (int)a1;
        var fdOut = (int)a2;
        var len = (int)a3;
        var flags = (int)a4;

        if (len <= 0) return 0;

        var fileIn = GetFD(fdIn);
        var fileOut = GetFD(fdOut);
        if (fileIn == null || fileOut == null) return -(int)Errno.EBADF;

        if (fileIn.OpenedInode is not PipeInode pipeIn) return -(int)Errno.EINVAL;
        if (fileOut.OpenedInode is not PipeInode) return -(int)Errno.EINVAL;
        if (fdIn == fdOut) return -(int)Errno.EINVAL;

        var bufSize = Math.Min(len, 65536);
        var buf = ArrayPool<byte>.Shared.Rent(bufSize);

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
                    await fileIn.OpenedInode.WaitForRead(fileIn, task);
                    continue; // retry after data arrives
                }

                if (bytesRead < 0) return bytesRead;

                var bytesWritten = fileOut.OpenedInode!.Write(fileOut, buf.AsSpan(0, bytesRead), fileOut.Position);
                if (bytesWritten == -(int)Errno.EAGAIN)
                {
                    if ((flags & 2) != 0 || (fileOut.Flags & FileFlags.O_NONBLOCK) != 0)
                        return -(int)Errno.EAGAIN;
                    await fileOut.OpenedInode.WaitForWrite(fileOut, task);
                    continue;
                }

                if (bytesWritten < 0) return bytesWritten;

                return bytesWritten;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

#pragma warning restore CS1998
}