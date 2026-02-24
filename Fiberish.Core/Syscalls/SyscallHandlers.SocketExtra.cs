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
    // For host sockets we delegate to the .NET Socket API where possible.
    // For Unix sockets and most unknown options we silently accept or return
    // plausible defaults, matching glibc's tolerance for emulated environments.

    private static async ValueTask<int> SysSetSockOpt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int fd      = (int)a1;
        int level   = (int)a2; // SOL_SOCKET=1, IPPROTO_TCP=6, IPPROTO_IPV6=41 …
        int optname = (int)a3;
        uint optval = a4;
        int optlen  = (int)a5;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        // Read the option value bytes from guest memory
        var buf = new byte[Math.Max(optlen, 4)];
        if (optlen > 0 && !task.CPU.CopyFromUser(optval, buf.AsSpan(0, optlen)))
            return -(int)Errno.EFAULT;

        if (file.Dentry.Inode is HostSocketInode hostSock)
        {
            var sock = hostSock.NativeSocket;
            try
            {
                // SOL_SOCKET = 1
                if (level == 1)
                {
                    switch (optname)
                    {
                        case 2: // SO_REUSEADDR
                            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress,
                                BinaryPrimitives.ReadInt32LittleEndian(buf) != 0);
                            return 0;
                        case 6: // SO_KEEPALIVE
                            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive,
                                BinaryPrimitives.ReadInt32LittleEndian(buf) != 0);
                            return 0;
                        case 7: // SO_OOBINLINE
                            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.OutOfBandInline,
                                BinaryPrimitives.ReadInt32LittleEndian(buf) != 0);
                            return 0;
                        case 8: // SO_SNDBUF
                            sock.SendBufferSize = BinaryPrimitives.ReadInt32LittleEndian(buf);
                            return 0;
                        case 9: // SO_RCVBUF
                            sock.ReceiveBufferSize = BinaryPrimitives.ReadInt32LittleEndian(buf);
                            return 0;
                        case 15: // SO_REUSEPORT
                            // .NET exposes this on Linux/macOS via SocketOptionName.ReuseAddress
                            // On Windows, reuse-port isn't available → silently ignore
                            return 0;
                        case 20: // SO_RCVTIMEO
                            if (optlen >= 8)
                            {
                                long sec  = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
                                long usec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));
                                sock.ReceiveTimeout = (int)(sec * 1000 + usec / 1000);
                            }
                            return 0;
                        case 21: // SO_SNDTIMEO
                            if (optlen >= 8)
                            {
                                long sec  = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
                                long usec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));
                                sock.SendTimeout = (int)(sec * 1000 + usec / 1000);
                            }
                            return 0;
                        default:
                            // Accept unknown SOL_SOCKET options silently
                            return 0;
                    }
                }

                // IPPROTO_TCP = 6
                if (level == 6)
                {
                    switch (optname)
                    {
                        case 1: // TCP_NODELAY
                            sock.NoDelay = BinaryPrimitives.ReadInt32LittleEndian(buf) != 0;
                            return 0;
                        case 8: // TCP_KEEPIDLE (Linux-specific)
                        case 9: // TCP_KEEPINTVL
                        case 10: // TCP_KEEPCNT
                            // Best-effort: silently accept; .NET doesn't expose all knobs
                            return 0;
                        default:
                            return 0;
                    }
                }

                // IPPROTO_IPV6 = 41
                if (level == 41)
                {
                    if (optname == 26) // IPV6_V6ONLY
                    {
                        sock.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only,
                            BinaryPrimitives.ReadInt32LittleEndian(buf) != 0);
                    }
                    return 0;
                }

                // Unknown level → accept silently (common pattern for emulators)
                return 0;
            }
            catch (SocketException ex)
            {
                return -LinuxToWindowsSocketError(ex.SocketErrorCode);
            }
        }

        // Unix sockets / pipes: accept all options silently
        return 0;
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

        var outBuf = new byte[Math.Max(optlen, 4)];
        int written = 4; // default: return a single int

        if (file.Dentry.Inode is HostSocketInode hostSock)
        {
            var sock = hostSock.NativeSocket;
            try
            {
                if (level == 1) // SOL_SOCKET
                {
                    switch (optname)
                    {
                        case 2: // SO_REUSEADDR
                            BinaryPrimitives.WriteInt32LittleEndian(outBuf,
                                sock.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress) is int v1 ? v1 : 0);
                            break;
                        case 6: // SO_KEEPALIVE
                            BinaryPrimitives.WriteInt32LittleEndian(outBuf,
                                sock.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive) is int v2 ? v2 : 0);
                            break;
                        case 4: // SO_ERROR
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
                        case 8: // SO_SNDBUF
                            BinaryPrimitives.WriteInt32LittleEndian(outBuf, sock.SendBufferSize);
                            break;
                        case 9: // SO_RCVBUF
                            BinaryPrimitives.WriteInt32LittleEndian(outBuf, sock.ReceiveBufferSize);
                            break;
                        case 3: // SO_TYPE
                            BinaryPrimitives.WriteInt32LittleEndian(outBuf,
                                sock.SocketType == System.Net.Sockets.SocketType.Stream ? 1 : 2);
                            break;
                        default:
                            BinaryPrimitives.WriteInt32LittleEndian(outBuf, 0);
                            break;
                    }
                }
                else if (level == 6) // IPPROTO_TCP
                {
                    switch (optname)
                    {
                        case 1: // TCP_NODELAY
                            BinaryPrimitives.WriteInt32LittleEndian(outBuf, sock.NoDelay ? 1 : 0);
                            break;
                        default:
                            BinaryPrimitives.WriteInt32LittleEndian(outBuf, 0);
                            break;
                    }
                }
                else
                {
                    BinaryPrimitives.WriteInt32LittleEndian(outBuf, 0);
                }
            }
            catch (SocketException ex)
            {
                return -LinuxToWindowsSocketError(ex.SocketErrorCode);
            }
        }
        else
        {
            // Unix socket / pipe: return 0 for everything
            BinaryPrimitives.WriteInt32LittleEndian(outBuf, 0);
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
        bool inIsPipe  = fileIn.Dentry.Inode  is PipeInode;
        bool outIsPipe = fileOut.Dentry.Inode is PipeInode;
        if (!inIsPipe && !outIsPipe) return -(int)Errno.EINVAL;

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
                int bytesRead = fileIn.Dentry.Inode!.Read(fileIn, buf.AsSpan(0, toRead), readOffset);

                if (bytesRead == 0) break; // EOF
                if (bytesRead == -(int)Errno.EAGAIN)
                {
                    if (totalTransferred > 0) break;
                    // If non-blocking and no data → EAGAIN
                    if ((flags & 2) != 0 || (fileIn.Flags & FileFlags.O_NONBLOCK) != 0)
                        return -(int)Errno.EAGAIN;
                    // Otherwise wait for data (pipe read-ready)
                    await fileIn.Dentry.Inode.WaitForRead(fileIn);
                    continue;
                }
                if (bytesRead < 0) return bytesRead;

                int bytesWritten = fileOut.Dentry.Inode!.Write(fileOut, buf.AsSpan(0, bytesRead), writeOffset);
                if (bytesWritten < 0)
                {
                    if (totalTransferred > 0) break;
                    return bytesWritten;
                }

                readOffset   += bytesRead;
                writeOffset  += bytesWritten;
                totalTransferred += bytesWritten;
                remaining    -= bytesWritten;
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

        if (fileIn.Dentry.Inode  is not PipeInode pipeIn) return -(int)Errno.EINVAL;
        if (fileOut.Dentry.Inode is not PipeInode) return -(int)Errno.EINVAL;
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
                    await fileIn.Dentry.Inode.WaitForRead(fileIn);
                    continue; // retry after data arrives
                }
                if (bytesRead < 0) return bytesRead;

                int bytesWritten = fileOut.Dentry.Inode!.Write(fileOut, buf.AsSpan(0, bytesRead), fileOut.Position);
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
