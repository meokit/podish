using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

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

        var domain = (int)a1;
        var type = (int)a2; // May contain SOCK_NONBLOCK / SOCK_CLOEXEC
        var protocol = (int)a3;

        var realType = type & 0xf;
        AddressFamily af;
        SocketType sockType;
        ProtocolType proto;

        if (domain == LinuxConstants.AF_INET) af = AddressFamily.InterNetwork;
        else if (domain == LinuxConstants.AF_INET6) af = AddressFamily.InterNetworkV6;
        else if (domain == LinuxConstants.AF_UNIX) return -(int)Errno.EAFNOSUPPORT; // TODO: UNIX Domain Sockets
        else return -(int)Errno.EAFNOSUPPORT;

        if (realType == LinuxConstants.SOCK_STREAM) sockType = SocketType.Stream;
        else if (realType == LinuxConstants.SOCK_DGRAM) sockType = SocketType.Dgram;
        else if (realType == LinuxConstants.SOCK_RAW) sockType = SocketType.Raw;
        else return -(int)Errno.EINVAL;

        if (sm.NetworkMode == Fiberish.Core.Net.NetworkMode.Private)
        {
            if (af != AddressFamily.InterNetwork) return -(int)Errno.EAFNOSUPPORT;
            if (sockType != SocketType.Stream && sockType != SocketType.Dgram) return -(int)Errno.ESOCKTNOSUPPORT;

            var inode = new NetstackSocketInode(0, sm.MemfdSuperBlock, sm.GetOrCreatePrivateNetNamespace(), sockType);
            var fileFlags = FileFlags.O_RDWR;
            if ((type & LinuxConstants.SOCK_NONBLOCK) != 0) fileFlags |= FileFlags.O_NONBLOCK;
            if ((type & LinuxConstants.SOCK_CLOEXEC) != 0) fileFlags |= FileFlags.O_CLOEXEC;

            var dentry = new Dentry($"socket:[{inode.Ino}]", inode, null, sm.MemfdSuperBlock);
            var file = new LinuxFile(dentry, fileFlags, sm.AnonMount);
            return sm.AllocFD(file);
        }

        if (OperatingSystem.IsMacOS() && sockType == SocketType.Raw)
        {
            if (af == AddressFamily.InterNetwork) proto = ProtocolType.Icmp;
            else if (af == AddressFamily.InterNetworkV6) proto = ProtocolType.IcmpV6;
            else return -(int)Errno.EPROTONOSUPPORT;
        }
        else if (protocol == 0)
        {
            if (sockType == SocketType.Stream) proto = ProtocolType.Tcp;
            else if (sockType == SocketType.Dgram) proto = ProtocolType.Udp;
            else return -(int)Errno.EPROTONOSUPPORT;
        }
        else if (protocol == LinuxConstants.IPPROTO_TCP && sockType == SocketType.Stream) proto = ProtocolType.Tcp;
        else if (protocol == LinuxConstants.IPPROTO_UDP && sockType == SocketType.Dgram) proto = ProtocolType.Udp;
        else if (protocol == LinuxConstants.IPPROTO_ICMP &&
                 (sockType == SocketType.Raw || sockType == SocketType.Dgram) &&
                 af == AddressFamily.InterNetwork) proto = ProtocolType.Icmp;
        else if (protocol == LinuxConstants.IPPROTO_ICMPV6 &&
                 (sockType == SocketType.Raw || sockType == SocketType.Dgram) &&
                 af == AddressFamily.InterNetworkV6) proto = ProtocolType.IcmpV6;
        else return -(int)Errno.EPROTONOSUPPORT;

        try
        {
            HostSocketInode inode;
            if ((sockType == SocketType.Dgram || sockType == SocketType.Raw) &&
                (protocol == LinuxConstants.IPPROTO_ICMP || protocol == LinuxConstants.IPPROTO_ICMPV6))
            {
                inode = CreateHostSocketForLinuxPingSemantics(sm, af, proto, sockType);
            }
            else
            {
                inode = new HostSocketInode(0, sm.MemfdSuperBlock, af, sockType, proto);
            }
            var fileFlags = FileFlags.O_RDWR;

            if ((type & LinuxConstants.SOCK_NONBLOCK) != 0) fileFlags |= FileFlags.O_NONBLOCK;
            if ((type & LinuxConstants.SOCK_CLOEXEC) != 0) fileFlags |= FileFlags.O_CLOEXEC;

            var dentry = new Dentry($"socket:[{inode.Ino}]", inode, null, sm.MemfdSuperBlock);
            var file = new LinuxFile(dentry, fileFlags, sm.AnonMount);

            return sm.AllocFD(file);
        }
        catch (SocketException ex)
        {
            return -LinuxToWindowsSocketError(ex.SocketErrorCode);
        }
    }

    /// <summary>
    ///     Maps Linux ping/raw socket semantics onto a host socket shape we can actually support.
    ///     Darwin is intentionally special-cased here: guest raw ICMP/ICMPv6 sockets are downgraded
    ///     to host datagram sockets, while HostSocketInode keeps Linux-visible SO_TYPE semantics via
    ///     LinuxSocketType. Keep this policy here, not in HostSocketInode, so the data path stays uniform.
    /// </summary>
    private static HostSocketInode CreateHostSocketForLinuxPingSemantics(SyscallManager sm, AddressFamily af, ProtocolType proto,
        SocketType linuxSocketType)
    {
        if (OperatingSystem.IsMacOS())
        {
            return new HostSocketInode(0, sm.MemfdSuperBlock, af, SocketType.Dgram, proto,
                linuxSocketType: linuxSocketType);
        }

        if (linuxSocketType == SocketType.Dgram)
        {
            try
            {
                return new HostSocketInode(0, sm.MemfdSuperBlock, af, SocketType.Dgram, proto,
                    linuxSocketType: SocketType.Dgram);
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode is SocketError.ProtocolNotSupported or SocketError.OperationNotSupported)
            {
                // Some hosts do not expose Linux-style ping sockets but still allow raw ICMP.
                // Keep guest-visible SO_TYPE as SOCK_DGRAM to preserve Linux ABI.
                return new HostSocketInode(0, sm.MemfdSuperBlock, af, SocketType.Raw, proto,
                    linuxSocketType: SocketType.Dgram);
            }
        }

        return new HostSocketInode(0, sm.MemfdSuperBlock, af, SocketType.Raw, proto, linuxSocketType: linuxSocketType);
    }
    private static async ValueTask<int> SysConnect(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var addrPtr = a2;
        var addrLen = (int)a3;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;
        if (file.Dentry.Inode is HostSocketInode sockInode)
        {
            var endpoint = ReadSockaddr(sm.Engine, addrPtr, addrLen);
            if (endpoint == null) return -(int)Errno.EINVAL;

            try
            {
                return await sockInode.ConnectAsync(file, endpoint);
            }
            catch (SocketException ex)
            {
                return -LinuxToWindowsSocketError(ex.SocketErrorCode);
            }
        }

        if (file.Dentry.Inode is NetstackSocketInode netInode)
        {
            var endpoint = ReadSockaddr(sm.Engine, addrPtr, addrLen) as IPEndPoint;
            if (endpoint == null) return -(int)Errno.EINVAL;
            return await netInode.ConnectAsync(file, endpoint);
        }

        return -(int)Errno.ENOTSOCK;
    }

    private static async ValueTask<int> SysBind(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var addrPtr = a2;
        var addrLen = (int)a3;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;
        if (file.Dentry.Inode is HostSocketInode sockInode)
        {
            var endpoint = ReadSockaddr(sm.Engine, addrPtr, addrLen);
            if (endpoint == null) return -(int)Errno.EINVAL;
            Logger.LogTrace("[Socket] bind fd={Fd} endpoint={Endpoint} addrLen={AddrLen}", fd, endpoint, addrLen);

            try
            {
                sockInode.NativeSocket!.Bind(endpoint);
                return 0;
            }
            catch (SocketException ex)
            {
                Logger.LogWarning(ex,
                    "[Socket] bind failed fd={Fd} endpoint={Endpoint} addrLen={AddrLen} socketError={SocketError}",
                    fd, endpoint, addrLen, ex.SocketErrorCode);
                return -LinuxToWindowsSocketError(ex.SocketErrorCode);
            }
        }

        if (file.Dentry.Inode is NetstackSocketInode netInode)
        {
            var endpoint = ReadSockaddr(sm.Engine, addrPtr, addrLen) as IPEndPoint;
            if (endpoint == null) return -(int)Errno.EINVAL;
            return netInode.Bind(endpoint);
        }

        return -(int)Errno.ENOTSOCK;
    }

    private static async ValueTask<int> SysListen(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var backlog = (int)a2;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;
        if (file.Dentry.Inode is HostSocketInode sockInode)
        {
            try
            {
                sockInode.NativeSocket!.Listen(backlog);
                return 0;
            }
            catch (SocketException ex)
            {
                return -LinuxToWindowsSocketError(ex.SocketErrorCode);
            }
        }

        if (file.Dentry.Inode is NetstackSocketInode netInode)
            return netInode.Listen(backlog);

        return -(int)Errno.ENOTSOCK;
    }

    private static async ValueTask<int> SysAccept(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await SysAccept4(state, a1, a2, a3, 0, 0, 0);
    }

    private static async ValueTask<int> SysGetSockName(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var addrPtr = a2;
        var addrLenPtr = a3;

        if (addrPtr == 0 || addrLenPtr == 0) return -(int)Errno.EFAULT;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;
        if (file.Dentry.Inode is HostSocketInode sockInode)
        {
            EndPoint? ep;
            try
            {
                ep = sockInode.NativeSocket!.LocalEndPoint;
            }
            catch
            {
                ep = null;
            }

            if (ep == null)
                ep = sockInode.HostAddressFamily == AddressFamily.InterNetworkV6
                    ? new IPEndPoint(IPAddress.IPv6Any, 0)
                    : new IPEndPoint(IPAddress.Any, 0);

            WriteSockaddr(sm.Engine, addrPtr, addrLenPtr, ep);
            return 0;
        }

        if (file.Dentry.Inode is NetstackSocketInode netInode)
        {
            WriteSockaddr(sm.Engine, addrPtr, addrLenPtr, netInode.LocalEndPoint ?? new IPEndPoint(IPAddress.Any, 0));
            return 0;
        }

        return -(int)Errno.ENOTSOCK;
    }

    private static async ValueTask<int> SysGetPeerName(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var addrPtr = a2;
        var addrLenPtr = a3;

        if (addrPtr == 0 || addrLenPtr == 0) return -(int)Errno.EFAULT;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;
        if (file.Dentry.Inode is HostSocketInode sockInode)
        {
            EndPoint? ep;
            try
            {
                ep = sockInode.NativeSocket!.RemoteEndPoint;
            }
            catch
            {
                ep = null;
            }

            if (ep == null) return -(int)Errno.ENOTCONN;

            WriteSockaddr(sm.Engine, addrPtr, addrLenPtr, ep);
            return 0;
        }

        if (file.Dentry.Inode is NetstackSocketInode netInode)
        {
            if (netInode.RemoteEndPoint == null) return -(int)Errno.ENOTCONN;
            WriteSockaddr(sm.Engine, addrPtr, addrLenPtr, netInode.RemoteEndPoint);
            return 0;
        }

        return -(int)Errno.ENOTSOCK;
    }

    private static async ValueTask<int> SysAccept4(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var addrPtr = a2;
        var addrLenPtr = a3;
        var flags = (int)a4;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;
        if (file.Dentry.Inode is HostSocketInode sockInode)
        {
            try
            {
                var newSock = await sockInode.AcceptAsync(file, flags);

                var newInode = new HostSocketInode(0, sm.MemfdSuperBlock, newSock);
                var fileFlags = FileFlags.O_RDWR;
                if ((flags & 0x800) != 0) fileFlags |= FileFlags.O_NONBLOCK;
                if ((flags & 0x80000) != 0) fileFlags |= FileFlags.O_CLOEXEC;

                var dentry = new Dentry($"socket:[{newInode.Ino}]", newInode, null, sm.MemfdSuperBlock);
                var newFile = new LinuxFile(dentry, fileFlags, sm.AnonMount);

                if (addrPtr != 0 && addrLenPtr != 0) WriteSockaddr(sm.Engine, addrPtr, addrLenPtr, newSock.RemoteEndPoint);

                return sm.AllocFD(newFile);
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.Interrupted)
                    return -(int)Errno.ERESTARTSYS;
                return -LinuxToWindowsSocketError(ex.SocketErrorCode);
            }
        }

        if (file.Dentry.Inode is NetstackSocketInode netInode)
        {
            var accepted = await netInode.AcceptAsync(file, flags);
            if (accepted.Rc != 0 || accepted.Inode == null)
                return accepted.Rc;

            var fileFlags = FileFlags.O_RDWR;
            if ((flags & 0x800) != 0) fileFlags |= FileFlags.O_NONBLOCK;
            if ((flags & 0x80000) != 0) fileFlags |= FileFlags.O_CLOEXEC;

            var dentry = new Dentry($"socket:[{accepted.Inode.Ino}]", accepted.Inode, null, sm.MemfdSuperBlock);
            var newFile = new LinuxFile(dentry, fileFlags, sm.AnonMount);

            if (addrPtr != 0 && addrLenPtr != 0 && accepted.Inode.RemoteEndPoint != null)
                WriteSockaddr(sm.Engine, addrPtr, addrLenPtr, accepted.Inode.RemoteEndPoint);

            return sm.AllocFD(newFile);
        }

        return -(int)Errno.ENOTSOCK;
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

        var fd = (int)a1;
        var bufPtr = a2;
        var len = (int)a3;
        var flags = (int)a4;
        var destAddrPtr = a5;
        var destAddrLen = (int)a6;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;
        var buf = new byte[len];
        if (!task.CPU.CopyFromUser(bufPtr, buf)) return -(int)Errno.EFAULT;

        if (file.Dentry.Inode is HostSocketInode sockInode)
        {
            try
            {
                var hostFlags = flags & ~LinuxConstants.MSG_NOSIGNAL;

                if (destAddrPtr != 0)
                {
                    var endpoint = ReadSockaddr(sm.Engine, destAddrPtr, destAddrLen);
                    if (endpoint == null) return -(int)Errno.EINVAL;

                    var ret = await sockInode.SendToAsync(file, buf, hostFlags, endpoint);
                    if (ret == -(int)Errno.EPIPE) task.PostSignal((int)Signal.SIGPIPE);
                    return ret;
                }
                else
                {
                    var ret = await sockInode.SendAsync(file, buf, hostFlags);
                    if (ret == -(int)Errno.EPIPE) task.PostSignal((int)Signal.SIGPIPE);
                    return ret;
                }
            }
            catch (SocketException ex)
            {
                return -LinuxToWindowsSocketError(ex.SocketErrorCode);
            }
        }

        if (file.Dentry.Inode is NetstackSocketInode netInode)
        {
            if (destAddrPtr != 0)
            {
                var endpoint = ReadSockaddr(sm.Engine, destAddrPtr, destAddrLen) as IPEndPoint;
                if (endpoint == null) return -(int)Errno.EINVAL;
                return await netInode.SendToAsync(file, buf, endpoint, flags);
            }

            return await netInode.SendAsync(file, buf, flags);
        }

        return -(int)Errno.ENOTSOCK;
    }

    private static async ValueTask<int> SysRecvFrom(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var bufPtr = a2;
        var len = (int)a3;
        var flags = (int)a4;
        var srcAddrPtr = a5;
        var addrLenPtr = a6;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;
        var buf = new byte[len];

        if (file.Dentry.Inode is HostSocketInode sockInode)
        {
            try
            {
                var hostFlags = flags & ~LinuxConstants.MSG_NOSIGNAL;
                int bytes;

                if (srcAddrPtr != 0 && addrLenPtr != 0)
                {
                    EndPoint remoteEp = sockInode.HostAddressFamily == AddressFamily.InterNetworkV6
                        ? new IPEndPoint(IPAddress.IPv6Any, 0)
                        : new IPEndPoint(IPAddress.Any, 0);

                    var result = await sockInode.RecvFromAsync(file, buf, hostFlags, remoteEp);
                    bytes = result.Bytes;

                    if (bytes >= 0 && result.RemoteEp != null)
                        WriteSockaddr(sm.Engine, srcAddrPtr, addrLenPtr, result.RemoteEp);
                }
                else
                {
                    bytes = await sockInode.RecvAsync(file, buf, hostFlags);
                }

                if (bytes > 0)
                    if (!task.CPU.CopyToUser(bufPtr, buf.AsSpan(0, bytes)))
                        return -(int)Errno.EFAULT;
                return bytes;
            }
            catch (SocketException ex)
            {
                return -LinuxToWindowsSocketError(ex.SocketErrorCode);
            }
        }

        if (file.Dentry.Inode is NetstackSocketInode netInode)
        {
            int bytes;
            EndPoint? remoteEp = null;
            if (srcAddrPtr != 0 && addrLenPtr != 0)
            {
                var result = await netInode.RecvFromAsync(file, buf, flags);
                bytes = result.Bytes;
                remoteEp = result.RemoteEndPoint;
            }
            else
            {
                bytes = await netInode.RecvAsync(file, buf, flags);
            }

            if (bytes > 0)
            {
                if (!task.CPU.CopyToUser(bufPtr, buf.AsSpan(0, bytes)))
                    return -(int)Errno.EFAULT;
                if (srcAddrPtr != 0 && addrLenPtr != 0 && remoteEp != null)
                    WriteSockaddr(sm.Engine, srcAddrPtr, addrLenPtr, remoteEp);
            }
            return bytes;
        }

        return -(int)Errno.ENOTSOCK;
    }

    private static async ValueTask<int> SysSendMsg(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var msgPtr = a2;
        var flags = (int)a3;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        var msgRaw = new byte[28];
        if (!task.CPU.CopyFromUser(msgPtr, msgRaw)) return -(int)Errno.EFAULT;

        var iovPtr = BinaryPrimitives.ReadUInt32LittleEndian(msgRaw.AsSpan(8, 4));
        var iovLen = BinaryPrimitives.ReadInt32LittleEndian(msgRaw.AsSpan(12, 4));
        var controlPtr = BinaryPrimitives.ReadUInt32LittleEndian(msgRaw.AsSpan(16, 4));
        var controlLen = BinaryPrimitives.ReadInt32LittleEndian(msgRaw.AsSpan(20, 4));

        var totalBytes = 0;
        var iovs = new (uint Base, int Len)[iovLen];
        for (var i = 0; i < iovLen; i++)
        {
            var iovRaw = new byte[8];
            if (!task.CPU.CopyFromUser(iovPtr + (uint)(i * 8), iovRaw)) return -(int)Errno.EFAULT;
            iovs[i] = (BinaryPrimitives.ReadUInt32LittleEndian(iovRaw.AsSpan(0, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(iovRaw.AsSpan(4, 4)));
            totalBytes += iovs[i].Len;
        }

        var data = new byte[totalBytes];
        var offset = 0;
        foreach (var iov in iovs)
            if (iov.Len > 0)
            {
                if (!task.CPU.CopyFromUser(iov.Base, data.AsSpan(offset, iov.Len))) return -(int)Errno.EFAULT;
                offset += iov.Len;
            }

        var fds = new List<LinuxFile>();
        if (controlPtr != 0 && controlLen > 0)
        {
            var cmsgRaw = new byte[controlLen];
            if (!task.CPU.CopyFromUser(controlPtr, cmsgRaw)) return -(int)Errno.EFAULT;

            var cmsgOffset = 0;
            while (cmsgOffset + 12 <= controlLen)
            {
                var cmsgLen = BinaryPrimitives.ReadInt32LittleEndian(cmsgRaw.AsSpan(cmsgOffset, 4));
                var level = BinaryPrimitives.ReadInt32LittleEndian(cmsgRaw.AsSpan(cmsgOffset + 4, 4));
                var type = BinaryPrimitives.ReadInt32LittleEndian(cmsgRaw.AsSpan(cmsgOffset + 8, 4));

                if (level == 1 /* SOL_SOCKET */ && type == 1 /* SCM_RIGHTS */)
                {
                    var fdCount = (cmsgLen - 12) / 4;
                    for (var i = 0; i < fdCount; i++)
                    {
                        var passedFd =
                            BinaryPrimitives.ReadInt32LittleEndian(cmsgRaw.AsSpan(cmsgOffset + 12 + i * 4, 4));
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
            var ret = await unixSock.SendMessageAsync(file, data, fds.Count > 0 ? fds : null, flags);
            if (ret == -(int)Errno.EPIPE) task.PostSignal((int)Signal.SIGPIPE);
            return ret;
        }

        if (file.Dentry.Inode is HostSocketInode hostSock)
        {
            // Host sockets don't support SCM_RIGHTS, fallback to basic send
            var ret = await hostSock.SendAsync(file, data, flags);
            if (ret == -(int)Errno.EPIPE) task.PostSignal((int)Signal.SIGPIPE);
            return ret;
        }

        if (file.Dentry.Inode is NetstackSocketInode netSock)
        {
            if (fds.Count > 0)
                return -(int)Errno.EOPNOTSUPP;

            var ret = await netSock.SendAsync(file, data, flags);
            if (ret == -(int)Errno.EPIPE) task.PostSignal((int)Signal.SIGPIPE);
            return ret;
        }

        return -(int)Errno.ENOTSOCK;
    }

    private static async ValueTask<int> SysRecvMsg(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var msgPtr = a2;
        var flags = (int)a3;

        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        var msgRaw = new byte[28];
        if (!task.CPU.CopyFromUser(msgPtr, msgRaw)) return -(int)Errno.EFAULT;

        var iovPtr = BinaryPrimitives.ReadUInt32LittleEndian(msgRaw.AsSpan(8, 4));
        var iovLen = BinaryPrimitives.ReadInt32LittleEndian(msgRaw.AsSpan(12, 4));
        var controlPtr = BinaryPrimitives.ReadUInt32LittleEndian(msgRaw.AsSpan(16, 4));
        var controlLen = BinaryPrimitives.ReadInt32LittleEndian(msgRaw.AsSpan(20, 4));

        var totalBytes = 0;
        var iovs = new (uint Base, int Len)[iovLen];
        for (var i = 0; i < iovLen; i++)
        {
            var iovRaw = new byte[8];
            if (!task.CPU.CopyFromUser(iovPtr + (uint)(i * 8), iovRaw)) return -(int)Errno.EFAULT;
            iovs[i] = (BinaryPrimitives.ReadUInt32LittleEndian(iovRaw.AsSpan(0, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(iovRaw.AsSpan(4, 4)));
            totalBytes += iovs[i].Len;
        }

        var buffer = new byte[totalBytes];
        var bytesRead = 0;
        List<LinuxFile>? receivedFds = null;

        var namePtr = BinaryPrimitives.ReadUInt32LittleEndian(msgRaw.AsSpan(0, 4));
        var nameLenPtr = msgPtr + 4; // msg_namelen is at offset 4

        if (file.Dentry.Inode is UnixSocketInode unixSock)
        {
            var res = await unixSock.RecvMessageAsync(file, buffer, flags);
            if (res.BytesRead < 0) return res.BytesRead;
            bytesRead = res.BytesRead;
            receivedFds = res.Fds;
            // TODO: Unix socket peer name?
        }
        else if (file.Dentry.Inode is HostSocketInode hostSock)
        {
            EndPoint remoteEp = hostSock.HostAddressFamily == AddressFamily.InterNetworkV6
                ? new IPEndPoint(IPAddress.IPv6Any, 0)
                : new IPEndPoint(IPAddress.Any, 0);

            var hostFlags = flags & ~LinuxConstants.MSG_NOSIGNAL;
            var res = await hostSock.RecvFromAsync(file, buffer, hostFlags, remoteEp);

            if (res.Bytes < 0) return res.Bytes;
            bytesRead = res.Bytes;

            if (bytesRead >= 0 && namePtr != 0 && res.RemoteEp != null)
                WriteSockaddr(sm.Engine, namePtr, nameLenPtr, res.RemoteEp);
        }
        else if (file.Dentry.Inode is NetstackSocketInode netSock)
        {
            bytesRead = await netSock.RecvAsync(file, buffer, flags);
            if (bytesRead < 0) return bytesRead;

            if (bytesRead >= 0 && namePtr != 0 && netSock.RemoteEndPoint != null)
                WriteSockaddr(sm.Engine, namePtr, nameLenPtr, netSock.RemoteEndPoint);
        }
        else
        {
            return -(int)Errno.ENOTSOCK;
        }

        var offset = 0;
        foreach (var iov in iovs)
            if (iov.Len > 0 && offset < bytesRead)
            {
                var toCopy = Math.Min(iov.Len, bytesRead - offset);
                task.CPU.CopyToUser(iov.Base, buffer.AsSpan(offset, toCopy));
                offset += toCopy;
            }

        if (receivedFds != null && receivedFds.Count > 0 && controlPtr != 0)
        {
            var cmsgLen = 12 + receivedFds.Count * 4;
            if (controlLen >= cmsgLen)
            {
                var cmsgRaw = new byte[cmsgLen];
                BinaryPrimitives.WriteInt32LittleEndian(cmsgRaw.AsSpan(0, 4), cmsgLen);
                BinaryPrimitives.WriteInt32LittleEndian(cmsgRaw.AsSpan(4, 4), 1); // SOL_SOCKET
                BinaryPrimitives.WriteInt32LittleEndian(cmsgRaw.AsSpan(8, 4), 1); // SCM_RIGHTS

                for (var i = 0; i < receivedFds.Count; i++)
                {
                    var newFd = sm.DupFD(receivedFds[i]);
                    BinaryPrimitives.WriteInt32LittleEndian(cmsgRaw.AsSpan(12 + i * 4, 4), newFd);
                }

                task.CPU.CopyToUser(controlPtr, cmsgRaw);
                BinaryPrimitives.WriteInt32LittleEndian(msgRaw.AsSpan(20, 4), cmsgLen); // Update controllen
                task.CPU.CopyToUser(msgPtr, msgRaw);
            }
        }

        return bytesRead;
    }

    private static async ValueTask<int> SysSocketPair(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var domain = (int)a1;
        var type = (int)a2;
        var protocol = (int)a3;
        var svPtr = a4;

        if (domain != LinuxConstants.AF_UNIX) return -(int)Errno.EAFNOSUPPORT; // AF_UNIX only

        if (protocol != 0 && protocol != LinuxConstants.AF_UNIX) return -(int)Errno.EPROTONOSUPPORT;

        var realType = type & 0xf;
        SocketType sockType;
        if (realType == LinuxConstants.SOCK_STREAM) sockType = SocketType.Stream;
        else if (realType == LinuxConstants.SOCK_DGRAM) sockType = SocketType.Dgram;
        else if (realType == LinuxConstants.SOCK_SEQPACKET) sockType = SocketType.Seqpacket;
        else return -(int)Errno.EINVAL;

        var fileFlags = FileFlags.O_RDWR;
        if ((type & LinuxConstants.SOCK_NONBLOCK) != 0) fileFlags |= FileFlags.O_NONBLOCK;
        if ((type & LinuxConstants.SOCK_CLOEXEC) != 0) fileFlags |= FileFlags.O_CLOEXEC;

        var inode1 = new UnixSocketInode(0, sm.MemfdSuperBlock, sockType);
        var inode2 = new UnixSocketInode(0, sm.MemfdSuperBlock, sockType);
        inode1.ConnectPair(inode2);
        inode2.ConnectPair(inode1);

        var file1 = new LinuxFile(new Dentry($"socket:[{inode1.Ino}]", inode1, null, sm.MemfdSuperBlock),
            fileFlags, sm.AnonMount);
        var file2 = new LinuxFile(new Dentry($"socket:[{inode2.Ino}]", inode2, null, sm.MemfdSuperBlock),
            fileFlags, sm.AnonMount);

        var fd1 = sm.AllocFD(file1);
        var fd2 = sm.AllocFD(file2);

        var buf = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), fd1);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), fd2);
        if (!sm.Engine.CopyToUser(svPtr, buf)) return -(int)Errno.EFAULT;

        return 0;
    }

    private static async ValueTask<int> SysSocketCall(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var call = (int)a1;
        var argsPtr = a2;

        var args = new uint[6];
        var argsRaw = new byte[24];
        if (!sm.Engine.CopyFromUser(argsPtr, argsRaw)) return -(int)Errno.EFAULT;

        for (var i = 0; i < 6; i++)
            args[i] = BinaryPrimitives.ReadUInt32LittleEndian(argsRaw.AsSpan(i * 4, 4));

        return call switch
        {
            1 /* SYS_SOCKET */ => await SysSocket(state, args[0], args[1], args[2], 0, 0, 0),
            2 /* SYS_BIND */ => await SysBind(state, args[0], args[1], args[2], 0, 0, 0),
            3 /* SYS_CONNECT */ => await SysConnect(state, args[0], args[1], args[2], 0, 0, 0),
            4 /* SYS_LISTEN */ => await SysListen(state, args[0], args[1], args[2], 0, 0, 0),
            5 /* SYS_ACCEPT */ => await SysAccept(state, args[0], args[1], args[2], 0, 0, 0),
            6 /* SYS_GETSOCKNAME */ => await SysGetSockName(state, args[0], args[1], args[2], 0, 0, 0),
            7 /* SYS_GETPEERNAME */ => await SysGetPeerName(state, args[0], args[1], args[2], 0, 0, 0),
            8 /* SYS_SOCKETPAIR */ => await SysSocketPair(state, args[0], args[1], args[2], args[3], 0, 0),
            9 /* SYS_SEND */ => await SysSend(state, args[0], args[1], args[2], args[3], 0, 0),
            10 /* SYS_RECV */ => await SysRecv(state, args[0], args[1], args[2], args[3], 0, 0),
            11 /* SYS_SENDTO */ => await SysSendTo(state, args[0], args[1], args[2], args[3], args[4], args[5]),
            12 /* SYS_RECVFROM */ => await SysRecvFrom(state, args[0], args[1], args[2], args[3], args[4], args[5]),
            14 /* SYS_SETSOCKOPT */ => await SysSetSockOpt(state, args[0], args[1], args[2], args[3], args[4], 0),
            15 /* SYS_GETSOCKOPT */ => await SysGetSockOpt(state, args[0], args[1], args[2], args[3], args[4], 0),
            16 /* SYS_SENDMSG */ => await SysSendMsg(state, args[0], args[1], args[2], 0, 0, 0),
            17 /* SYS_RECVMSG */ => await SysRecvMsg(state, args[0], args[1], args[2], 0, 0, 0),
            18 /* SYS_ACCEPT4 */ => await SysAccept4(state, args[0], args[1], args[2], args[3], 0, 0),
            19 /* SYS_RECVMMSG */ => await SysRecvMMsg(state, args[0], args[1], args[2], args[3], args[4], 0),
            20 /* SYS_SENDMMSG */ => await SysSendMMsg(state, args[0], args[1], args[2], args[3], 0, 0),
            _ => -(int)Errno.ENOSYS
        };
    }

    // --- Helpers ---

    private static EndPoint? ReadSockaddr(Engine engine, uint addrPtr, int addrLen)
    {
        if (addrLen < 2) return null;
        var buf = new byte[addrLen];
        if (!engine.CopyFromUser(addrPtr, buf)) return null;

        var family = BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(0, 2));

        if (family == 2) // AF_INET
        {
            if (addrLen < 16) return null;
            var port = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2, 2)); // Network byte order!
            var ipBuf = buf.AsSpan(4, 4).ToArray();
            var ip = new IPAddress(ipBuf);
            return new IPEndPoint(ip, port);
        }

        if (family == 10) // AF_INET6
        {
            if (addrLen < 28) return null;
            var port = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2, 2));
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
        var maxLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);

        if (ep is IPEndPoint ipEp)
        {
            if (ipEp.AddressFamily == AddressFamily.InterNetwork)
            {
                var buf = new byte[16];
                BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(0, 2), 2); // AF_INET
                BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), (ushort)ipEp.Port);
                ipEp.Address.TryWriteBytes(buf.AsSpan(4, 4), out _);

                var toCopy = Math.Min(maxLen, 16);
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

                var toCopy = Math.Min(maxLen, 28);
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
            SocketError.InvalidArgument => (int)Errno.EINVAL,
            SocketError.MessageSize => (int)Errno.EMSGSIZE,
            SocketError.ProtocolNotSupported => (int)Errno.EPROTONOSUPPORT,
            SocketError.SocketNotSupported => (int)Errno.ESOCKTNOSUPPORT,
            _ => (int)Errno.EIO
        };
    }
#pragma warning restore CS1998
}
