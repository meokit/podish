using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
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

    private static async ValueTask<int> SysSetSockOpt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int fd      = (int)a1;
        int level   = (int)a2;
        int optname = (int)a3;
        uint optval = a4;
        int optlen  = (int)a5;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        // Read the option value bytes from guest memory
        var buf = new byte[Math.Max(optlen, 4)];
        if (optlen > 0 && !task.CPU.CopyFromUser(optval, buf.AsSpan(0, optlen)))
            return -(int)Errno.EFAULT;

        if (file.OpenedInode is HostSocketInode hostSock)
        {
            var sock = hostSock.NativeSocket!;
            try
            {
                if (level == LinuxConstants.SOL_SOCKET)
                {
                    switch (optname)
                    {
                        case LinuxConstants.SO_REUSEADDR:
                            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress,
                                BinaryPrimitives.ReadInt32LittleEndian(buf) != 0);
                            return 0;
                        case LinuxConstants.SO_KEEPALIVE:
                            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive,
                                BinaryPrimitives.ReadInt32LittleEndian(buf) != 0);
                            return 0;
                        case LinuxConstants.SO_OOBINLINE:
                            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.OutOfBandInline,
                                BinaryPrimitives.ReadInt32LittleEndian(buf) != 0);
                            return 0;
                        case LinuxConstants.SO_SNDBUF:
                            sock.SendBufferSize = BinaryPrimitives.ReadInt32LittleEndian(buf);
                            return 0;
                        case LinuxConstants.SO_RCVBUF:
                            sock.ReceiveBufferSize = BinaryPrimitives.ReadInt32LittleEndian(buf);
                            return 0;
                        case LinuxConstants.SO_LINGER:
                            if (optlen < 8)
                                return -(int)Errno.EINVAL;
                            var lingerOn = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4)) != 0;
                            var lingerSec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));
                            if (lingerSec < 0)
                                lingerSec = 0;
                            sock.LingerState = new LingerOption(lingerOn, lingerSec);
                            return 0;
                        case LinuxConstants.SO_REUSEPORT:
                            // .NET exposes this on Linux/macOS via SocketOptionName.ReuseAddress
                            return 0;
                        case LinuxConstants.SO_RCVTIMEO:
                            if (optlen >= 8)
                            {
                                long sec  = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
                                long usec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));
                                sock.ReceiveTimeout = (int)(sec * 1000 + usec / 1000);
                            }
                            return 0;
                        case LinuxConstants.SO_SNDTIMEO:
                            if (optlen >= 8)
                            {
                                long sec  = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
                                long usec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));
                                sock.SendTimeout = (int)(sec * 1000 + usec / 1000);
                            }
                            return 0;
                        default:
                            return -(int)Errno.ENOPROTOOPT;
                    }
                }

                if (level == LinuxConstants.IPPROTO_TCP)
                {
                    switch (optname)
                    {
                        case LinuxConstants.TCP_NODELAY:
                            sock.NoDelay = BinaryPrimitives.ReadInt32LittleEndian(buf) != 0;
                            return 0;
                        case LinuxConstants.TCP_KEEPIDLE:
                        case LinuxConstants.TCP_KEEPINTVL:
                        case LinuxConstants.TCP_KEEPCNT:
                            return 0;
                        default:
                            return -(int)Errno.ENOPROTOOPT;
                    }
                }

                if (level == LinuxConstants.IPPROTO_IPV6)
                {
                    if (optname == LinuxConstants.IPV6_V6ONLY)
                    {
                        sock.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only,
                            BinaryPrimitives.ReadInt32LittleEndian(buf) != 0);
                        return 0;
                    }
                    return -(int)Errno.ENOPROTOOPT;
                }

                if (level == LinuxConstants.IPPROTO_ICMPV6)
                {
                    if (optname == LinuxConstants.ICMPV6_FILTER)
                        return 0;
                    return -(int)Errno.ENOPROTOOPT;
                }

                return -(int)Errno.ENOPROTOOPT;
            }
            catch (SocketException ex)
            {
                return -LinuxToWindowsSocketError(ex.SocketErrorCode);
            }
        }

        if (file.OpenedInode is NetstackSocketInode netSock)
        {
            return netSock.SetSocketOption(level, optname, buf.AsSpan(0, optlen));
        }

        return -(int)Errno.ENOPROTOOPT;
    }

    private static async ValueTask<int> SysGetSockOpt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int fd      = (int)a1;
        int level   = (int)a2;
        int optname = (int)a3;
        uint optval = a4;
        uint optlenPtr = a5;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        // Read user-supplied optlen
        var lenBuf = new byte[4];
        if (!task.CPU.CopyFromUser(optlenPtr, lenBuf)) return -(int)Errno.EFAULT;
        int optlen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);

        var outBuf = new byte[Math.Max(optlen, 8)];
        int written = 4; // default: return a single int

        if (file.OpenedInode is HostSocketInode hostSock)
        {
            var sock = hostSock.NativeSocket!;
            try
            {
                    if (level == LinuxConstants.SOL_SOCKET)
                    {
                        switch (optname)
                        {
                        case LinuxConstants.SO_REUSEADDR:
                            BinaryPrimitives.WriteInt32LittleEndian(outBuf,
                                sock.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress) is int v1 ? v1 : 0);
                            break;
                        case LinuxConstants.SO_KEEPALIVE:
                            BinaryPrimitives.WriteInt32LittleEndian(outBuf,
                                sock.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive) is int v2 ? v2 : 0);
                            break;
                        case LinuxConstants.SO_ERROR:
                            var cachedSoError = hostSock.ConsumeCachedSocketError();
                            if (cachedSoError != 0)
                            {
                                BinaryPrimitives.WriteInt32LittleEndian(outBuf, cachedSoError);
                                break;
                            }
                            var soErrorObj = sock.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);
                            var soError = soErrorObj switch
                            {
                                int i => (SocketError)i,
                                SocketError se => se,
                                _ => SocketError.Success
                            };
                            var linuxErr = soError == SocketError.Success ? 0 : LinuxToWindowsSocketError(soError);
                            BinaryPrimitives.WriteInt32LittleEndian(outBuf, linuxErr);
                            break;
                        case LinuxConstants.SO_SNDBUF:
                            BinaryPrimitives.WriteInt32LittleEndian(outBuf, sock.SendBufferSize);
                            break;
                        case LinuxConstants.SO_RCVBUF:
                            BinaryPrimitives.WriteInt32LittleEndian(outBuf, sock.ReceiveBufferSize);
                            break;
                        case LinuxConstants.SO_LINGER:
                            var linger = sock.LingerState ?? new LingerOption(false, 0);
                            BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(0, 4), linger.Enabled ? 1 : 0);
                            BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(4, 4), linger.LingerTime);
                            written = 8;
                            break;
                        case LinuxConstants.SO_TYPE:
                            BinaryPrimitives.WriteInt32LittleEndian(outBuf,
                                hostSock.LinuxSocketType switch
                                {
                                    SocketType.Stream => LinuxConstants.SOCK_STREAM,
                                    SocketType.Dgram => LinuxConstants.SOCK_DGRAM,
                                    SocketType.Raw => LinuxConstants.SOCK_RAW,
                                    SocketType.Seqpacket => LinuxConstants.SOCK_SEQPACKET,
                                    _ => 0
                                });
                            break;
                        default:
                            return -(int)Errno.ENOPROTOOPT;
                    }
                }
                else if (level == LinuxConstants.IPPROTO_TCP)
                {
                    switch (optname)
                    {
                        case LinuxConstants.TCP_NODELAY:
                            BinaryPrimitives.WriteInt32LittleEndian(outBuf, sock.NoDelay ? 1 : 0);
                            break;
                        default:
                            return -(int)Errno.ENOPROTOOPT;
                    }
                }
                else
                {
                    return -(int)Errno.ENOPROTOOPT;
                }
            }
            catch (SocketException ex)
            {
                return -LinuxToWindowsSocketError(ex.SocketErrorCode);
            }
        }
        else
        {
            if (file.OpenedInode is NetstackSocketInode netSock)
            {
                var rc = netSock.GetSocketOption(level, optname, outBuf, out written);
                if (rc != 0) return rc;
            }
            else
            {
                return -(int)Errno.ENOPROTOOPT;
            }
        }

        written = Math.Min(written, optlen);
        if (!task.CPU.CopyToUser(optval, outBuf.AsSpan(0, written))) return -(int)Errno.EFAULT;
        BinaryPrimitives.WriteInt32LittleEndian(lenBuf, written);
        if (!task.CPU.CopyToUser(optlenPtr, lenBuf)) return -(int)Errno.EFAULT;

        return 0;
    }

    // ── sendmmsg ─────────────────────────────────────────────────────────────
    //
    // int sendmmsg(int sockfd, struct mmsghdr *msgvec, unsigned int vlen, int flags)
    //
    // struct mmsghdr { struct msghdr msg_hdr; unsigned int msg_len; }
    // We reuse SysSendMsg for each element and write back msg_len.

    private static async ValueTask<int> SysSendMMsg(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int fd       = (int)a1;
        uint msgvec  = a2;
        int vlen     = (int)a3;
        int flags    = (int)a4;

        if (vlen <= 0) return 0;

        // struct mmsghdr is 28+4 = 32 bytes on i386 (msghdr=28 + msg_len u32)
        const int mmsgHdrSize = 32;

        int sent = 0;
        for (int i = 0; i < vlen; i++)
        {
            uint mmsgPtr = msgvec + (uint)(i * mmsgHdrSize);
            // SysSendMsg reads a struct msghdr at msgPtr.
            // We pass mmsgPtr directly — it starts with struct msghdr (28 bytes).
            int result = await SysSendMsg(state, (uint)fd, mmsgPtr, (uint)flags, 0, 0, 0);
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

    private static async ValueTask<int> SysRecvMMsg(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int fd       = (int)a1;
        uint msgvec  = a2;
        int vlen     = (int)a3;
        int flags    = (int)a4;
        // a5 = timeout ptr — we ignore it for now and treat as non-blocking batch

        if (vlen <= 0) return 0;

        const int mmsgHdrSize = 32;

        int received = 0;
        for (int i = 0; i < vlen; i++)
        {
            uint mmsgPtr = msgvec + (uint)(i * mmsgHdrSize);
            int result = await SysRecvMsg(state, (uint)fd, mmsgPtr, (uint)flags, 0, 0, 0);
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

    private static async ValueTask<int> SysSplice(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int fdIn    = (int)a1;
        uint offIn  = a2; // loff_t* — pointer to guest offset, or 0 for current position
        int fdOut   = (int)a3;
        uint offOut = a4;
        int len     = (int)a5;
        int flags   = (int)a6; // SPLICE_F_NONBLOCK=0x2, SPLICE_F_MORE=0x4, etc.

        if (len <= 0) return 0;

        var fileIn  = sm.GetFD(fdIn);
        var fileOut = sm.GetFD(fdOut);
        if (fileIn == null || fileOut == null) return -(int)Errno.EBADF;

        // At least one FD must be a pipe (enforced by Linux; we relax here for compatibility)
        bool inIsPipe  = fileIn.OpenedInode  is PipeInode;
        bool outIsPipe = fileOut.OpenedInode is PipeInode;
        if (!inIsPipe && !outIsPipe) return -(int)Errno.EINVAL;
        if (inIsPipe && offIn != 0) return -(int)Errno.ESPIPE;
        if (outIsPipe && offOut != 0) return -(int)Errno.ESPIPE;

        // Resolve read offset
        long readOffset = fileIn.Position;
        if (offIn != 0)
        {
            var offBytes = new byte[8];
            if (!task.CPU.CopyFromUser(offIn, offBytes)) return -(int)Errno.EFAULT;
            readOffset = BitConverter.ToInt64(offBytes);
        }

        // Resolve write offset
        long writeOffset = fileOut.Position;
        if (offOut != 0)
        {
            var offBytes = new byte[8];
            if (!task.CPU.CopyFromUser(offOut, offBytes)) return -(int)Errno.EFAULT;
            writeOffset = BitConverter.ToInt64(offBytes);
        }

        int bufSize = Math.Min(len, 65536);
        var buf = ArrayPool<byte>.Shared.Rent(bufSize);
        int totalTransferred = 0;

        try
        {
            int remaining = len;
            while (remaining > 0)
            {
                int toRead = Math.Min(remaining, bufSize);
                int bytesRead = fileIn.OpenedInode!.Read(fileIn, buf.AsSpan(0, toRead), readOffset);

                if (bytesRead == 0) break; // EOF
                if (bytesRead == -(int)Errno.EAGAIN)
                {
                    if (totalTransferred > 0) break;
                    // If non-blocking and no data → EAGAIN
                    if ((flags & 2) != 0 || (fileIn.Flags & FileFlags.O_NONBLOCK) != 0)
                        return -(int)Errno.EAGAIN;
                    // Otherwise wait for data (pipe read-ready)
                    await fileIn.OpenedInode.WaitForRead(fileIn);
                    continue;
                }
                if (bytesRead < 0) return bytesRead;

                int writeConsumed = 0;
                while (writeConsumed < bytesRead)
                {
                    int bytesWritten = fileOut.OpenedInode!.Write(fileOut,
                        buf.AsSpan(writeConsumed, bytesRead - writeConsumed), writeOffset);

                    if (bytesWritten == -(int)Errno.EPIPE)
                    {
                        if (sm.Engine.Owner is FiberTask fiberTask) fiberTask.PostSignal((int)Signal.SIGPIPE);
                        if (totalTransferred > 0) break;
                        return bytesWritten;
                    }

                    if (bytesWritten == -(int)Errno.EAGAIN)
                    {
                        if (totalTransferred > 0) break;
                        if ((flags & 2) != 0 || (fileOut.Flags & FileFlags.O_NONBLOCK) != 0)
                            return -(int)Errno.EAGAIN;
                        await fileOut.OpenedInode.WaitForWrite(fileOut);
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

                readOffset   += bytesRead;
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

    private static async ValueTask<int> SysTee(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int fdIn  = (int)a1;
        int fdOut = (int)a2;
        int len   = (int)a3;
        int flags = (int)a4;

        if (len <= 0) return 0;

        var fileIn  = sm.GetFD(fdIn);
        var fileOut = sm.GetFD(fdOut);
        if (fileIn == null || fileOut == null) return -(int)Errno.EBADF;

        if (fileIn.OpenedInode  is not PipeInode pipeIn) return -(int)Errno.EINVAL;
        if (fileOut.OpenedInode is not PipeInode) return -(int)Errno.EINVAL;
        if (fdIn == fdOut) return -(int)Errno.EINVAL;

        int bufSize = Math.Min(len, 65536);
        var buf = ArrayPool<byte>.Shared.Rent(bufSize);

        try
        {
            // tee() is a single-shot operation: peek available data (up to len)
            // from fd_in without consuming, write it to fd_out, return count.
            while (true)
            {
                int bytesRead = pipeIn.Peek(buf.AsSpan(0, bufSize));
                if (bytesRead == 0) return 0; // EOF
                if (bytesRead == -(int)Errno.EAGAIN)
                {
                    if ((flags & 2) != 0 || (fileIn.Flags & FileFlags.O_NONBLOCK) != 0)
                        return -(int)Errno.EAGAIN;
                    await fileIn.OpenedInode.WaitForRead(fileIn);
                    continue; // retry after data arrives
                }
                if (bytesRead < 0) return bytesRead;

                int bytesWritten = fileOut.OpenedInode!.Write(fileOut, buf.AsSpan(0, bytesRead), fileOut.Position);
                if (bytesWritten == -(int)Errno.EAGAIN)
                {
                    if ((flags & 2) != 0 || (fileOut.Flags & FileFlags.O_NONBLOCK) != 0)
                        return -(int)Errno.EAGAIN;
                    await fileOut.OpenedInode.WaitForWrite(fileOut);
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
