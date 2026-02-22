using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998

    private static async ValueTask<int> SysSocket(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int domain = (int)a1;
        int type = (int)a2; // May contain SOCK_NONBLOCK / SOCK_CLOEXEC
        int protocol = (int)a3;

        int realType = type & 0xf;
        AddressFamily af;
        SocketType sockType;
        ProtocolType proto;

        // Linux AF_INET = 2, AF_INET6 = 10, AF_UNIX = 1
        if (domain == 2) af = AddressFamily.InterNetwork;
        else if (domain == 10) af = AddressFamily.InterNetworkV6;
        else if (domain == 1) return -(int)Errno.EAFNOSUPPORT; // TODO: UNIX Domain Sockets
        else return -(int)Errno.EAFNOSUPPORT;

        if (realType == 1) sockType = SocketType.Stream; // SOCK_STREAM
        else if (realType == 2) sockType = SocketType.Dgram; // SOCK_DGRAM
        else return -(int)Errno.EINVAL;

        if (protocol == 0)
        {
            proto = sockType == SocketType.Stream ? ProtocolType.Tcp : ProtocolType.Udp;
        }
        else if (protocol == 6 /* IPPROTO_TCP */) proto = ProtocolType.Tcp;
        else if (protocol == 17 /* IPPROTO_UDP */) proto = ProtocolType.Udp;
        else return -(int)Errno.EPROTONOSUPPORT;

        try
        {
            var inode = new HostSocketInode(0, sm.MemfdSuperBlock, af, sockType, proto);
            var fileFlags = FileFlags.O_RDWR;
            
            // Linux socket type flags: SOCK_NONBLOCK=04000 (0x800), SOCK_CLOEXEC=02000000 (0x80000)
            if ((type & 0x800) != 0) fileFlags |= FileFlags.O_NONBLOCK;
            if ((type & 0x80000) != 0) fileFlags |= FileFlags.O_CLOEXEC;

            var dentry = new Dentry($"socket:[{inode.Ino}]", inode, null, sm.MemfdSuperBlock);
            var file = new Fiberish.VFS.LinuxFile(dentry, fileFlags);
            
            return sm.AllocFD(file);
        }
        catch (SocketException ex)
        {
            return -LinuxToWindowsSocketError(ex.SocketErrorCode);
        }
    }

    private static async ValueTask<int> SysConnect(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int fd = (int)a1;
        uint addrPtr = a2;
        int addrLen = (int)a3;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;
        if (file.Dentry.Inode is not HostSocketInode sockInode) return -(int)Errno.ENOTSOCK;

        var endpoint = ReadSockaddr(sm.Engine, addrPtr, addrLen);
        if (endpoint == null) return -(int)Errno.EINVAL;

        try
        {
            // System.Net.Sockets.Socket.Connect blocks natively if we just call it.
            // But since our socket is non-blocking, it will throw WouldBlock and connect in the background!
            sockInode.NativeSocket.Connect(endpoint);
            return 0;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock || ex.SocketErrorCode == SocketError.IOPending)
        {
            // EINPROGRESS
            return -(int)Errno.EINPROGRESS;
        }
        catch (SocketException ex)
        {
            return -LinuxToWindowsSocketError(ex.SocketErrorCode);
        }
    }

    private static async ValueTask<int> SysBind(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int fd = (int)a1;
        uint addrPtr = a2;
        int addrLen = (int)a3;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;
        if (file.Dentry.Inode is not HostSocketInode sockInode) return -(int)Errno.ENOTSOCK;

        var endpoint = ReadSockaddr(sm.Engine, addrPtr, addrLen);
        if (endpoint == null) return -(int)Errno.EINVAL;

        try
        {
            sockInode.NativeSocket.Bind(endpoint);
            return 0;
        }
        catch (SocketException ex)
        {
            return -LinuxToWindowsSocketError(ex.SocketErrorCode);
        }
    }

    private static async ValueTask<int> SysListen(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int fd = (int)a1;
        int backlog = (int)a2;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;
        if (file.Dentry.Inode is not HostSocketInode sockInode) return -(int)Errno.ENOTSOCK;

        try
        {
            sockInode.NativeSocket.Listen(backlog);
            return 0;
        }
        catch (SocketException ex)
        {
            return -LinuxToWindowsSocketError(ex.SocketErrorCode);
        }
    }

    private static async ValueTask<int> SysAccept(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await SysAccept4(state, a1, a2, a3, 0, 0, 0);
    }

    private static async ValueTask<int> SysAccept4(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int fd = (int)a1;
        uint addrPtr = a2;
        uint addrLenPtr = a3;
        int flags = (int)a4;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;
        if (file.Dentry.Inode is not HostSocketInode sockInode) return -(int)Errno.ENOTSOCK;

        while (true)
        {
            try
            {
                var newSock = sockInode.NativeSocket.Accept();
                
                var newInode = new HostSocketInode(0, sm.MemfdSuperBlock, newSock);
                var fileFlags = FileFlags.O_RDWR;
                if ((flags & 0x800) != 0) fileFlags |= FileFlags.O_NONBLOCK;
                if ((flags & 0x80000) != 0) fileFlags |= FileFlags.O_CLOEXEC;
                
                var dentry = new Dentry($"socket:[{newInode.Ino}]", newInode, null, sm.MemfdSuperBlock);
                var newFile = new Fiberish.VFS.LinuxFile(dentry, fileFlags);
                
                if (addrPtr != 0 && addrLenPtr != 0)
                {
                    WriteSockaddr(sm.Engine, addrPtr, addrLenPtr, newSock.RemoteEndPoint);
                }

                return sm.AllocFD(newFile);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock || ex.SocketErrorCode == SocketError.IOPending)
            {
                if ((file.Flags & FileFlags.O_NONBLOCK) != 0) return -(int)Errno.EAGAIN;
                
                // Block until POLLIN
                sockInode.RegisterWait(file, () => { }, PollEvents.POLLIN);
                
                // We're digging slightly into the wait queue implementation here...
                // Ideally HostSocketInode exposes an AcceptAsync(). 
                // Because .Accept() blocks, we rely on the same AsyncWaitQueue mechanisms.
                // For simplicity here, we yield to schedule a re-poll
            }
            catch (SocketException ex)
            {
                return -LinuxToWindowsSocketError(ex.SocketErrorCode);
            }
            
            // Re-eval check
            if (task.HasUnblockedPendingSignal()) return -(int)Errno.ERESTARTSYS;
            await sockInode.WaitReadAsync();
        }
    }

    private static async ValueTask<int> SysSend(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await SysSendTo(state, a1, a2, a3, a4, 0, 0);
    }

    private static async ValueTask<int> SysRecv(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await SysRecvFrom(state, a1, a2, a3, a4, 0, 0);
    }

    private static async ValueTask<int> SysSendTo(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int fd = (int)a1;
        uint bufPtr = a2;
        int len = (int)a3;
        int flags = (int)a4;
        uint destAddrPtr = a5;
        int destAddrLen = (int)a6;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;
        if (file.Dentry.Inode is not HostSocketInode sockInode) return -(int)Errno.ENOTSOCK;

        var buf = new byte[len];
        if (!task.CPU.CopyFromUser(bufPtr, buf)) return -(int)Errno.EFAULT;

        try
        {
            if (destAddrPtr != 0)
            {
                var endpoint = ReadSockaddr(sm.Engine, destAddrPtr, destAddrLen);
                if (endpoint == null) return -(int)Errno.EINVAL;
                
                // FIXME: Connectless sendto missing async wrapper just for MVP.
                return sockInode.NativeSocket.SendTo(buf, (SocketFlags)flags, endpoint);
            }
            else
            {
                return await sockInode.SendAsync(file, buf, flags);
            }
        }
        catch (SocketException ex)
        {
            return -LinuxToWindowsSocketError(ex.SocketErrorCode);
        }
    }

    private static async ValueTask<int> SysRecvFrom(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int fd = (int)a1;
        uint bufPtr = a2;
        int len = (int)a3;
        int flags = (int)a4;
        uint srcAddrPtr = a5;
        uint addrLenPtr = a6;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;
        if (file.Dentry.Inode is not HostSocketInode sockInode) return -(int)Errno.ENOTSOCK;

        var buf = new byte[len];
        
        try
        {
            int bytes;
            if (srcAddrPtr != 0 && addrLenPtr != 0)
            {
                EndPoint remoteEp = sockInode.NativeSocket.AddressFamily == AddressFamily.InterNetworkV6 
                    ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);

                // FIXME: Blocking behavior in ReceiveFrom without wrapping...
                bytes = sockInode.NativeSocket.ReceiveFrom(buf, (SocketFlags)flags, ref remoteEp);
                WriteSockaddr(sm.Engine, srcAddrPtr, addrLenPtr, remoteEp);
            }
            else
            {
                bytes = await sockInode.RecvAsync(file, buf, flags);
            }

            if (bytes > 0)
            {
                if (!task.CPU.CopyToUser(bufPtr, buf.AsSpan(0, bytes))) return -(int)Errno.EFAULT;
            }
            return bytes;
        }
        catch (SocketException ex)
        {
            return -LinuxToWindowsSocketError(ex.SocketErrorCode);
        }
    }

    private static async ValueTask<int> SysSendMsg(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int fd = (int)a1;
        uint msgPtr = a2;
        int flags = (int)a3;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        var msgRaw = new byte[28];
        if (!task.CPU.CopyFromUser(msgPtr, msgRaw)) return -(int)Errno.EFAULT;

        uint iovPtr = BinaryPrimitives.ReadUInt32LittleEndian(msgRaw.AsSpan(8, 4));
        int iovLen = BinaryPrimitives.ReadInt32LittleEndian(msgRaw.AsSpan(12, 4));
        uint controlPtr = BinaryPrimitives.ReadUInt32LittleEndian(msgRaw.AsSpan(16, 4));
        int controlLen = BinaryPrimitives.ReadInt32LittleEndian(msgRaw.AsSpan(20, 4));

        int totalBytes = 0;
        var iovs = new (uint Base, int Len)[iovLen];
        for (int i=0; i<iovLen; i++)
        {
            var iovRaw = new byte[8];
            if (!task.CPU.CopyFromUser(iovPtr + (uint)(i*8), iovRaw)) return -(int)Errno.EFAULT;
            iovs[i] = (BinaryPrimitives.ReadUInt32LittleEndian(iovRaw.AsSpan(0, 4)), 
                       BinaryPrimitives.ReadInt32LittleEndian(iovRaw.AsSpan(4, 4)));
            totalBytes += iovs[i].Len;
        }

        var data = new byte[totalBytes];
        int offset = 0;
        foreach (var iov in iovs)
        {
            if (iov.Len > 0)
            {
                if (!task.CPU.CopyFromUser(iov.Base, data.AsSpan(offset, iov.Len))) return -(int)Errno.EFAULT;
                offset += iov.Len;
            }
        }

        var fds = new System.Collections.Generic.List<Fiberish.VFS.LinuxFile>();
        if (controlPtr != 0 && controlLen > 0)
        {
            var cmsgRaw = new byte[controlLen];
            if (!task.CPU.CopyFromUser(controlPtr, cmsgRaw)) return -(int)Errno.EFAULT;

            int cmsgOffset = 0;
            while (cmsgOffset + 12 <= controlLen)
            {
                int cmsgLen = BinaryPrimitives.ReadInt32LittleEndian(cmsgRaw.AsSpan(cmsgOffset, 4));
                int level = BinaryPrimitives.ReadInt32LittleEndian(cmsgRaw.AsSpan(cmsgOffset + 4, 4));
                int type = BinaryPrimitives.ReadInt32LittleEndian(cmsgRaw.AsSpan(cmsgOffset + 8, 4));

                if (level == 1 /* SOL_SOCKET */ && type == 1 /* SCM_RIGHTS */)
                {
                    int fdCount = (cmsgLen - 12) / 4;
                    for (int i=0; i<fdCount; i++)
                    {
                        int passedFd = BinaryPrimitives.ReadInt32LittleEndian(cmsgRaw.AsSpan(cmsgOffset + 12 + i * 4, 4));
                        var passedFile = sm.GetFD(passedFd);
                        if (passedFile != null) fds.Add(passedFile);
                    }
                }
                cmsgOffset += Math.Max(cmsgLen, 12);
                cmsgOffset = (cmsgOffset + 3) & ~3; // align to 4 bytes
            }
        }

        if (file.Dentry.Inode is UnixSocketInode unixSock)
        {
            return await unixSock.SendMessageAsync(file, data, fds.Count > 0 ? fds : null, flags);
        }
        else if (file.Dentry.Inode is HostSocketInode hostSock)
        {
            // Host sockets don't support SCM_RIGHTS, fallback to basic send
            return await hostSock.SendAsync(file, data, flags);
        }
        
        return -(int)Errno.ENOTSOCK;
    }

    private static async ValueTask<int> SysRecvMsg(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int fd = (int)a1;
        uint msgPtr = a2;
        int flags = (int)a3;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        var msgRaw = new byte[28];
        if (!task.CPU.CopyFromUser(msgPtr, msgRaw)) return -(int)Errno.EFAULT;

        uint iovPtr = BinaryPrimitives.ReadUInt32LittleEndian(msgRaw.AsSpan(8, 4));
        int iovLen = BinaryPrimitives.ReadInt32LittleEndian(msgRaw.AsSpan(12, 4));
        uint controlPtr = BinaryPrimitives.ReadUInt32LittleEndian(msgRaw.AsSpan(16, 4));
        int controlLen = BinaryPrimitives.ReadInt32LittleEndian(msgRaw.AsSpan(20, 4));

        int totalBytes = 0;
        var iovs = new (uint Base, int Len)[iovLen];
        for (int i=0; i<iovLen; i++)
        {
            var iovRaw = new byte[8];
            if (!task.CPU.CopyFromUser(iovPtr + (uint)(i*8), iovRaw)) return -(int)Errno.EFAULT;
            iovs[i] = (BinaryPrimitives.ReadUInt32LittleEndian(iovRaw.AsSpan(0, 4)), 
                       BinaryPrimitives.ReadInt32LittleEndian(iovRaw.AsSpan(4, 4)));
            totalBytes += iovs[i].Len;
        }

        var buffer = new byte[totalBytes];
        int bytesRead = 0;
        System.Collections.Generic.List<Fiberish.VFS.LinuxFile>? receivedFds = null;

        if (file.Dentry.Inode is UnixSocketInode unixSock)
        {
            var res = await unixSock.RecvMessageAsync(file, buffer, flags);
            if (res.BytesRead < 0) return res.BytesRead;
            bytesRead = res.BytesRead;
            receivedFds = res.Fds;
        }
        else if (file.Dentry.Inode is HostSocketInode hostSock)
        {
            bytesRead = await hostSock.RecvAsync(file, buffer, flags);
            if (bytesRead < 0) return bytesRead;
        }
        else return -(int)Errno.ENOTSOCK;

        int offset = 0;
        foreach (var iov in iovs)
        {
            if (iov.Len > 0 && offset < bytesRead)
            {
                int toCopy = Math.Min(iov.Len, bytesRead - offset);
                task.CPU.CopyToUser(iov.Base, buffer.AsSpan(offset, toCopy));
                offset += toCopy;
            }
        }

        if (receivedFds != null && receivedFds.Count > 0 && controlPtr != 0)
        {
            int cmsgLen = 12 + receivedFds.Count * 4;
            if (controlLen >= cmsgLen)
            {
                var cmsgRaw = new byte[cmsgLen];
                BinaryPrimitives.WriteInt32LittleEndian(cmsgRaw.AsSpan(0, 4), cmsgLen);
                BinaryPrimitives.WriteInt32LittleEndian(cmsgRaw.AsSpan(4, 4), 1); // SOL_SOCKET
                BinaryPrimitives.WriteInt32LittleEndian(cmsgRaw.AsSpan(8, 4), 1); // SCM_RIGHTS
                
                for (int i=0; i<receivedFds.Count; i++)
                {
                    int newFd = sm.AllocFD(receivedFds[i]);
                    BinaryPrimitives.WriteInt32LittleEndian(cmsgRaw.AsSpan(12 + i * 4, 4), newFd);
                }

                task.CPU.CopyToUser(controlPtr, cmsgRaw);
                BinaryPrimitives.WriteInt32LittleEndian(msgRaw.AsSpan(20, 4), cmsgLen); // Update controllen
                task.CPU.CopyToUser(msgPtr, msgRaw);
            }
        }

        return bytesRead;
    }

    private static async ValueTask<int> SysSocketPair(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int domain = (int)a1;
        int type = (int)a2;
        int protocol = (int)a3;
        uint svPtr = a4;

        if (domain != 1) return -(int)Errno.EAFNOSUPPORT; // AF_UNIX only

        int realType = type & 0xf;
        SocketType sockType = realType == 1 ? SocketType.Stream : SocketType.Dgram;

        var fileFlags = FileFlags.O_RDWR;
        if ((type & 0x800) != 0) fileFlags |= FileFlags.O_NONBLOCK;
        if ((type & 0x80000) != 0) fileFlags |= FileFlags.O_CLOEXEC;

        var inode1 = new UnixSocketInode(0, sm.MemfdSuperBlock, sockType);
        var inode2 = new UnixSocketInode(0, sm.MemfdSuperBlock, sockType);
        inode1.ConnectPair(inode2);
        inode2.ConnectPair(inode1);

        var file1 = new Fiberish.VFS.LinuxFile(new Dentry($"socket:[{inode1.Ino}]", inode1, null, sm.MemfdSuperBlock), fileFlags);
        var file2 = new Fiberish.VFS.LinuxFile(new Dentry($"socket:[{inode2.Ino}]", inode2, null, sm.MemfdSuperBlock), fileFlags);

        int fd1 = sm.AllocFD(file1);
        int fd2 = sm.AllocFD(file2);

        var buf = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), fd1);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), fd2);
        if (!sm.Engine.CopyToUser(svPtr, buf)) return -(int)Errno.EFAULT;

        return 0;
    }

    private static async ValueTask<int> SysSocketCall(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        int call = (int)a1;
        uint argsPtr = a2;

        uint[] args = new uint[6];
        byte[] argsRaw = new byte[24];
        if (!sm.Engine.CopyFromUser(argsPtr, argsRaw)) return -(int)Errno.EFAULT;
        
        for (int i=0; i<6; i++)
            args[i] = BinaryPrimitives.ReadUInt32LittleEndian(argsRaw.AsSpan(i*4, 4));

        return call switch
        {
            1 /* SYS_SOCKET */ => await SysSocket(state, args[0], args[1], args[2], 0, 0, 0),
            2 /* SYS_BIND */ => await SysBind(state, args[0], args[1], args[2], 0, 0, 0),
            3 /* SYS_CONNECT */ => await SysConnect(state, args[0], args[1], args[2], 0, 0, 0),
            4 /* SYS_LISTEN */ => await SysListen(state, args[0], args[1], args[2], 0, 0, 0),
            5 /* SYS_ACCEPT */ => await SysAccept(state, args[0], args[1], args[2], 0, 0, 0),
            8 /* SYS_SOCKETPAIR */ => await SysSocketPair(state, args[0], args[1], args[2], args[3], 0, 0),
            9 /* SYS_SEND */ => await SysSend(state, args[0], args[1], args[2], args[3], 0, 0),
            10 /* SYS_RECV */ => await SysRecv(state, args[0], args[1], args[2], args[3], 0, 0),
            11 /* SYS_SENDTO */ => await SysSendTo(state, args[0], args[1], args[2], args[3], args[4], args[5]),
            12 /* SYS_RECVFROM */ => await SysRecvFrom(state, args[0], args[1], args[2], args[3], args[4], args[5]),
            14 /* SYS_SETSOCKOPT */ => await SysSetSockOpt(state, args[0], args[1], args[2], args[3], args[4], 0),
            15 /* SYS_GETSOCKOPT */ => await SysGetSockOpt(state, args[0], args[1], args[2], args[3], args[4], 0),
            16 /* SYS_SENDMSG */    => await SysSendMsg(state, args[0], args[1], args[2], 0, 0, 0),
            17 /* SYS_RECVMSG */    => await SysRecvMsg(state, args[0], args[1], args[2], 0, 0, 0),
            18 /* SYS_ACCEPT4 */    => await SysAccept4(state, args[0], args[1], args[2], args[3], 0, 0),
            19 /* SYS_RECVMMSG */   => await SysRecvMMsg(state, args[0], args[1], args[2], args[3], args[4], 0),
            20 /* SYS_SENDMMSG */   => await SysSendMMsg(state, args[0], args[1], args[2], args[3], 0, 0),
            _ => -(int)Errno.ENOSYS
        };
    }

    // --- Helpers ---

    private static EndPoint? ReadSockaddr(Engine engine, uint addrPtr, int addrLen)
    {
        if (addrLen < 2) return null;
        var buf = new byte[addrLen];
        if (!engine.CopyFromUser(addrPtr, buf)) return null;

        short family = BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(0, 2));
        
        if (family == 2) // AF_INET
        {
            if (addrLen < 16) return null;
            ushort port = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2, 2)); // Network byte order!
            var ipBuf = buf.AsSpan(4, 4).ToArray();
            var ip = new IPAddress(ipBuf);
            return new IPEndPoint(ip, port);
        }
        else if (family == 10) // AF_INET6
        {
            if (addrLen < 28) return null;
            ushort port = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2, 2));
            // flowinfo = 4..8
            var ipBuf = buf.AsSpan(8, 16).ToArray();
            var ip = new IPAddress(ipBuf);
            // scope_id = 24..28
            return new IPEndPoint(ip, port);
        }

        return null;
    }

    private static void WriteSockaddr(Engine engine, uint addrPtr, uint addrLenPtr, EndPoint? ep)
    {
        if (ep == null) return;
        
        var lenBuf = new byte[4];
        if (!engine.CopyFromUser(addrLenPtr, lenBuf)) return;
        int maxLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);

        if (ep is IPEndPoint ipEp)
        {
            if (ipEp.AddressFamily == AddressFamily.InterNetwork)
            {
                var buf = new byte[16];
                BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(0, 2), 2); // AF_INET
                BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), (ushort)ipEp.Port);
                ipEp.Address.TryWriteBytes(buf.AsSpan(4, 4), out _);

                int toCopy = Math.Min(maxLen, 16);
                engine.CopyToUser(addrPtr, buf.AsSpan(0, toCopy));
                
                BinaryPrimitives.WriteInt32LittleEndian(lenBuf, 16);
                engine.CopyToUser(addrLenPtr, lenBuf);
            }
            else if (ipEp.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var buf = new byte[28];
                BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(0, 2), 10); // AF_INET6
                BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), (ushort)ipEp.Port);
                ipEp.Address.TryWriteBytes(buf.AsSpan(8, 16), out _);
                
                int toCopy = Math.Min(maxLen, 28);
                engine.CopyToUser(addrPtr, buf.AsSpan(0, toCopy));
                
                BinaryPrimitives.WriteInt32LittleEndian(lenBuf, 28);
                engine.CopyToUser(addrLenPtr, lenBuf);
            }
        }
    }

    private static int LinuxToWindowsSocketError(SocketError err)
    {
        return err switch
        {
            SocketError.AccessDenied => (int)Errno.EACCES,
            SocketError.AddressFamilyNotSupported => (int)Errno.EAFNOSUPPORT,
            SocketError.AddressAlreadyInUse => (int)Errno.EADDRINUSE,
            SocketError.AddressNotAvailable => (int)Errno.EADDRNOTAVAIL,
            SocketError.NetworkDown => (int)Errno.ENETDOWN,
            SocketError.NetworkUnreachable => (int)Errno.ENETUNREACH,
            SocketError.NetworkReset => (int)Errno.ENETRESET,
            SocketError.ConnectionAborted => (int)Errno.ECONNABORTED,
            SocketError.ConnectionReset => (int)Errno.ECONNRESET,
            SocketError.NoBufferSpaceAvailable => (int)Errno.ENOBUFS,
            SocketError.IsConnected => (int)Errno.EISCONN,
            SocketError.NotConnected => (int)Errno.ENOTCONN,
            SocketError.TimedOut => (int)Errno.ETIMEDOUT,
            SocketError.ConnectionRefused => (int)Errno.ECONNREFUSED,
            SocketError.HostUnreachable => (int)Errno.EHOSTUNREACH,
            SocketError.WouldBlock => (int)Errno.EAGAIN,
            _ => (int)Errno.EIO
        };
    }
#pragma warning restore CS1998
}
