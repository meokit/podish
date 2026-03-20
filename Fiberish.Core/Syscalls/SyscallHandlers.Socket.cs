using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Fiberish.Core;
using Fiberish.Core.Net;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998

    private async ValueTask<int> SysSocket(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var domain = (int)a1;
        var type = (int)a2; // May contain SOCK_NONBLOCK / SOCK_CLOEXEC
        var protocol = (int)a3;

        var realType = type & 0xf;
        AddressFamily af;
        SocketType sockType;
        ProtocolType proto;

        if (domain == LinuxConstants.AF_NETLINK)
        {
            if (realType != LinuxConstants.SOCK_RAW && realType != LinuxConstants.SOCK_DGRAM)
                return -(int)Errno.ESOCKTNOSUPPORT;
            if (protocol != LinuxConstants.NETLINK_ROUTE)
                return -(int)Errno.EPROTONOSUPPORT;

            var inode = new NetlinkRouteSocketInode(0, MemfdSuperBlock,
                () => NetDeviceSnapshotProvider.Capture(NetworkMode,
                    TryGetPrivateNetNamespace()));
            var fileFlags = FileFlags.O_RDWR;
            if ((type & LinuxConstants.SOCK_NONBLOCK) != 0) fileFlags |= FileFlags.O_NONBLOCK;
            if ((type & LinuxConstants.SOCK_CLOEXEC) != 0) fileFlags |= FileFlags.O_CLOEXEC;

            var dentry = new Dentry($"socket:[{inode.Ino}]", inode, null, MemfdSuperBlock);
            var file = new LinuxFile(dentry, fileFlags, AnonMount);
            return AllocFD(file);
        }

        if (domain == LinuxConstants.AF_UNIX)
        {
            if (protocol != 0 && protocol != LinuxConstants.AF_UNIX)
                return -(int)Errno.EPROTONOSUPPORT;

            if (realType == LinuxConstants.SOCK_STREAM) sockType = SocketType.Stream;
            else if (realType == LinuxConstants.SOCK_DGRAM) sockType = SocketType.Dgram;
            else if (realType == LinuxConstants.SOCK_SEQPACKET) sockType = SocketType.Seqpacket;
            else return -(int)Errno.EINVAL;

            var inode = new UnixSocketInode(0, MemfdSuperBlock, sockType);
            var fileFlags = FileFlags.O_RDWR;
            if ((type & LinuxConstants.SOCK_NONBLOCK) != 0) fileFlags |= FileFlags.O_NONBLOCK;
            if ((type & LinuxConstants.SOCK_CLOEXEC) != 0) fileFlags |= FileFlags.O_CLOEXEC;

            var dentry = new Dentry($"socket:[{inode.Ino}]", inode, null, MemfdSuperBlock);
            var file = new LinuxFile(dentry, fileFlags, AnonMount);
            return AllocFD(file);
        }

        if (domain == LinuxConstants.AF_INET) af = AddressFamily.InterNetwork;
        else if (domain == LinuxConstants.AF_INET6) af = AddressFamily.InterNetworkV6;
        else return -(int)Errno.EAFNOSUPPORT;

        if (realType == LinuxConstants.SOCK_STREAM) sockType = SocketType.Stream;
        else if (realType == LinuxConstants.SOCK_DGRAM) sockType = SocketType.Dgram;
        else if (realType == LinuxConstants.SOCK_RAW) sockType = SocketType.Raw;
        else return -(int)Errno.EINVAL;

        if (NetworkMode == NetworkMode.Private)
        {
            if (af != AddressFamily.InterNetwork) return -(int)Errno.EAFNOSUPPORT;
            if (sockType != SocketType.Stream && sockType != SocketType.Dgram) return -(int)Errno.ESOCKTNOSUPPORT;

            var inode = new NetstackSocketInode(0, MemfdSuperBlock, GetOrCreatePrivateNetNamespace(),
                sockType);
            var fileFlags = FileFlags.O_RDWR;
            if ((type & LinuxConstants.SOCK_NONBLOCK) != 0) fileFlags |= FileFlags.O_NONBLOCK;
            if ((type & LinuxConstants.SOCK_CLOEXEC) != 0) fileFlags |= FileFlags.O_CLOEXEC;

            var dentry = new Dentry($"socket:[{inode.Ino}]", inode, null, MemfdSuperBlock);
            var file = new LinuxFile(dentry, fileFlags, AnonMount);
            return AllocFD(file);
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
        else if (protocol == LinuxConstants.IPPROTO_TCP && sockType == SocketType.Stream)
        {
            proto = ProtocolType.Tcp;
        }
        else if (protocol == LinuxConstants.IPPROTO_UDP && sockType == SocketType.Dgram)
        {
            proto = ProtocolType.Udp;
        }
        else if (protocol == LinuxConstants.IPPROTO_ICMP &&
                 (sockType == SocketType.Raw || sockType == SocketType.Dgram) &&
                 af == AddressFamily.InterNetwork)
        {
            proto = ProtocolType.Icmp;
        }
        else if (protocol == LinuxConstants.IPPROTO_ICMPV6 &&
                 (sockType == SocketType.Raw || sockType == SocketType.Dgram) &&
                 af == AddressFamily.InterNetworkV6)
        {
            proto = ProtocolType.IcmpV6;
        }
        else
        {
            return -(int)Errno.EPROTONOSUPPORT;
        }

        try
        {
            HostSocketInode inode;
            if ((sockType == SocketType.Dgram || sockType == SocketType.Raw) &&
                (protocol == LinuxConstants.IPPROTO_ICMP || protocol == LinuxConstants.IPPROTO_ICMPV6))
                inode = CreateHostSocketForPingSemantics(this, af, proto, sockType);
            else
                inode = new HostSocketInode(0, MemfdSuperBlock, af, sockType, proto);

            var fileFlags = FileFlags.O_RDWR;

            if ((type & LinuxConstants.SOCK_NONBLOCK) != 0) fileFlags |= FileFlags.O_NONBLOCK;
            if ((type & LinuxConstants.SOCK_CLOEXEC) != 0) fileFlags |= FileFlags.O_CLOEXEC;

            var dentry = new Dentry($"socket:[{inode.Ino}]", inode, null, MemfdSuperBlock);
            var file = new LinuxFile(dentry, fileFlags, AnonMount);

            return AllocFD(file);
        }
        catch (SocketException ex)
        {
            return -LinuxToWindowsSocketError(ex.SocketErrorCode);
        }
    }

    /// <summary>
    ///     Maps Linux ping/raw socket semantics onto a host socket shape we can actually support.
    ///     We prefer host datagram sockets for ICMP/ICMPv6 so Linux guest ping follows the same
    ///     shape as the macOS path, while HostSocketInode preserves guest-visible SO_TYPE via
    ///     LinuxSocketType. Keep this policy here, not in HostSocketInode, so the data path stays uniform.
    /// </summary>
    private static HostSocketInode CreateHostSocketForPingSemantics(SyscallManager sm, AddressFamily af,
        ProtocolType proto, SocketType linuxSocketType)
    {
        try
        {
            return new HostSocketInode(0, sm.MemfdSuperBlock, af, SocketType.Dgram, proto, linuxSocketType);
        }
        catch (SocketException ex) when (
            ex.SocketErrorCode is SocketError.ProtocolNotSupported or SocketError.OperationNotSupported or
                SocketError.AccessDenied)
        {
            // Some hosts only support raw ICMP, or they gate ping sockets by capability/group policy.
            // Fall back to raw so privileged environments still work, but keep Linux-visible SO_TYPE.
            return new HostSocketInode(0, sm.MemfdSuperBlock, af, SocketType.Raw, proto, linuxSocketType);
        }
    }

    private async ValueTask<int> SysConnect(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var addrPtr = a2;
        var addrLen = (int)a3;

        var file = GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        var (endpoint, err) = ReadAnySockaddr(engine, file, addrPtr, addrLen);
        if (err < 0) return err;
        if (endpoint == null) return -(int)Errno.EINVAL;

        if (file.TryGetSocketEndpointOps(out var ops))
            return await ops.ConnectAsync(file, task, endpoint);

        return -(int)Errno.ENOTSOCK;
    }

    private async ValueTask<int> SysBind(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var addrPtr = a2;
        var addrLen = (int)a3;

        var file = GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        var (endpoint, err) = ReadAnySockaddr(engine, file, addrPtr, addrLen);
        if (err < 0) return err;
        if (endpoint == null) return -(int)Errno.EINVAL;

        if (file.TryGetSocketEndpointOps(out var ops))
            return ops.Bind(file, task, endpoint);

        if (file.OpenedInode is NetlinkRouteSocketInode) return 0;

        return -(int)Errno.ENOTSOCK;
    }

    private async ValueTask<int> SysListen(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var backlog = (int)a2;

        var file = GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        if (file.TryGetSocketEndpointOps(out var ops))
            return ops.Listen(file, task, backlog);

        return -(int)Errno.ENOTSOCK;
    }

    private async ValueTask<int> SysAccept(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await SysAccept4(engine, a1, a2, a3, 0, 0, 0);
    }

    private async ValueTask<int> SysGetSockName(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var fd = (int)a1;
        var addrPtr = a2;
        var addrLenPtr = a3;

        if (addrPtr == 0 || addrLenPtr == 0) return -(int)Errno.EFAULT;

        var file = GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        if (file.TryGetSocketEndpointOps(out var ops))
        {
            var res = ops.GetSockName(file, engine.Owner as FiberTask ?? throw new InvalidOperationException());
            WriteAnySockaddr(engine, file, addrPtr, addrLenPtr, res);
            return 0;
        }

        if (file.OpenedInode is NetlinkRouteSocketInode)
        {
            WriteSockaddrNetlink(engine, addrPtr, addrLenPtr);
            return 0;
        }

        return -(int)Errno.ENOTSOCK;
    }

    private async ValueTask<int> SysGetPeerName(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var fd = (int)a1;
        var addrPtr = a2;
        var addrLenPtr = a3;

        if (addrPtr == 0 || addrLenPtr == 0) return -(int)Errno.EFAULT;

        var file = GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        if (file.TryGetSocketEndpointOps(out var ops))
        {
            var res = ops.GetPeerName(file, engine.Owner as FiberTask ?? throw new InvalidOperationException());
            if (res.EndPoint == null && res.UnixAddressRaw == null && file.OpenedInode is not UnixSocketInode)
                return -(int)Errno.ENOTCONN;
            if (res.EndPoint == null && res.UnixAddressRaw == null && file.OpenedInode is UnixSocketInode ui &&
                !ui.IsConnected)
                return -(int)Errno.ENOTCONN;
            WriteAnySockaddr(engine, file, addrPtr, addrLenPtr, res);
            return 0;
        }

        if (file.OpenedInode is NetlinkRouteSocketInode) return -(int)Errno.ENOTCONN;

        return -(int)Errno.ENOTSOCK;
    }

    private async ValueTask<int> SysAccept4(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var addrPtr = a2;
        var addrLenPtr = a3;
        var flags = (int)a4;

        var file = GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        if (file.TryGetSocketEndpointOps(out var ops))
        {
            var accepted = await ops.AcceptAsync(file, task, flags);
            if (accepted.Rc != 0 || accepted.Inode == null)
                return accepted.Rc;

            var fileFlags = FileFlags.O_RDWR;
            if ((flags & LinuxConstants.SOCK_NONBLOCK) != 0 || (flags & 0x800) != 0) fileFlags |= FileFlags.O_NONBLOCK;
            if ((flags & LinuxConstants.SOCK_CLOEXEC) != 0 || (flags & 0x80000) != 0) fileFlags |= FileFlags.O_CLOEXEC;

            var dentry = new Dentry($"socket:[{accepted.Inode.Ino}]", accepted.Inode, null, MemfdSuperBlock);
            var newFile = new LinuxFile(dentry, fileFlags, AnonMount);

            if (addrPtr != 0 && addrLenPtr != 0)
            {
                var addrRes = new SocketAddressResult(accepted.PeerEndPoint, accepted.PeerUnixAddressRaw);
                WriteAnySockaddr(engine, file, addrPtr, addrLenPtr, addrRes);
            }

            return AllocFD(newFile);
        }

        return -(int)Errno.ENOTSOCK;
    }

    private async ValueTask<int> SysSend(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await SysSendTo(engine, a1, a2, a3, a4, 0, 0);
    }

    private async ValueTask<int> SysRecv(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await SysRecvFrom(engine, a1, a2, a3, a4, 0, 0);
    }

    private async ValueTask<int> SysShutdown(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var fd = (int)a1;
        var how = (int)a2;
        var file = GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        if (file.TryGetSocketEndpointOps(out var ops))
            return ops.Shutdown(file, engine.Owner as FiberTask ?? throw new InvalidOperationException(), how);

        return -(int)Errno.ENOTSOCK;
    }

    private async ValueTask<int> SysSendTo(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var bufPtr = a2;
        var len = (int)a3;
        var flags = (int)a4;
        var destAddrPtr = a5;
        var destAddrLen = (int)a6;

        var file = GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;
        var buf = new byte[len];
        if (!task.CPU.CopyFromUser(bufPtr, buf)) return -(int)Errno.EFAULT;

        if (file.TryGetSocketDataOps(out var ops))
        {
            if (destAddrPtr != 0)
            {
                var (endpoint, err) = ReadAnySockaddr(engine, file, destAddrPtr, destAddrLen);
                if (err < 0) return err;
                if (endpoint == null) return -(int)Errno.EINVAL;
                return await ops.SendToAsync(file, task, buf, flags, endpoint);
            }

            return await ops.SendAsync(file, task, buf, flags);
        }

        if (file.OpenedInode is NetlinkRouteSocketInode netlinkInode)
            return await netlinkInode.SendAsync(file, buf, flags);

        return -(int)Errno.ENOTSOCK;
    }

    private async ValueTask<int> SysRecvFrom(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var bufPtr = a2;
        var len = (int)a3;
        var flags = (int)a4;
        var srcAddrPtr = a5;
        var addrLenPtr = a6;

        var file = GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;
        var buf = new byte[len];

        if (file.TryGetSocketDataOps(out var ops))
        {
            var res = await ops.RecvFromAsync(file, task, buf, flags, len);
            var bytes = res.BytesRead;
            if (bytes < 0) return bytes;

            if (bytes > 0)
                if (!task.CPU.CopyToUser(bufPtr, buf.AsSpan(0, bytes)))
                {
                    if (res.Fds != null)
                        foreach (var f in res.Fds)
                            f.Close();
                    return -(int)Errno.EFAULT;
                }

            if (srcAddrPtr != 0 && addrLenPtr != 0)
            {
                var addrRes = new SocketAddressResult(res.SourceEndPoint, res.SourceSunPathRaw);
                WriteAnySockaddr(engine, file, srcAddrPtr, addrLenPtr, addrRes);
            }

            if (res.Fds != null)
                foreach (var f in res.Fds)
                    f.Close();
            return bytes;
        }

        if (file.OpenedInode is NetlinkRouteSocketInode netlinkInode)
        {
            var bytes = await netlinkInode.RecvAsync(file, task, buf, flags, len);
            if (bytes < 0) return bytes;
            if (bytes > 0 && !task.CPU.CopyToUser(bufPtr, buf.AsSpan(0, bytes))) return -(int)Errno.EFAULT;
            return bytes;
        }

        return -(int)Errno.ENOTSOCK;
    }

    private async ValueTask<int> SysSendMsg(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var msgPtr = a2;
        var flags = (int)a3;

        var file = GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        var msgRes = ReadMsgHdr(engine, msgPtr);
        if (msgRes.Error < 0) return msgRes.Error;
        var msg = msgRes.Value;

        object? endpoint = null;
        if (msg.msg_name != 0 && msg.msg_namelen > 0)
        {
            var (ep, err) = ReadAnySockaddr(engine, file, msg.msg_name, (int)msg.msg_namelen);
            if (err < 0) return err;
            endpoint = ep;
        }

        var bufRes = ReadIovecs(engine, msg.msg_iov, msg.msg_iovlen);
        if (bufRes.Error < 0) return bufRes.Error;
        var buf = bufRes.Buffer;

        List<LinuxFile>? fds = null;
        if (msg.msg_control != 0 && msg.msg_controllen >= 16)
        {
            var res = ReadCmsgFds(engine, msg.msg_control, msg.msg_controllen);
            if (!res.Success) return -(int)Errno.EINVAL;
            fds = res.Fds;
        }

        if (file.TryGetSocketDataOps(out var ops))
            return await ops.SendMsgAsync(file, task, buf, fds, flags | msg.msg_flags, endpoint);

        if (file.OpenedInode is NetlinkRouteSocketInode netlinkInode)
            return await netlinkInode.SendAsync(file, buf, flags | msg.msg_flags);

        return -(int)Errno.ENOTSOCK;
    }

    private async ValueTask<int> SysRecvMsg(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;
        var fd = (int)a1;
        var msgPtr = a2;
        var flags = (int)a3;

        var file = GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        var msgRes = ReadMsgHdr(engine, msgPtr);
        if (msgRes.Error < 0) return msgRes.Error;
        var msg = msgRes.Value;

        var totalLen = 0;
        var iovsRes = ReadIovecsDef(engine, msg.msg_iov, msg.msg_iovlen);
        if (iovsRes.Error < 0) return iovsRes.Error;

        foreach (var iov in iovsRes.Iovecs)
            totalLen += (int)iov.Len;

        var buf = new byte[totalLen];

        if (file.TryGetSocketDataOps(out var ops))
        {
            var res = await ops.RecvMsgAsync(file, task, buf, flags | msg.msg_flags, totalLen);
            var bytes = res.BytesRead;
            if (bytes < 0) return bytes;

            var writeRc = WriteIovecs(engine, buf.AsSpan(0, bytes), iovsRes.Iovecs);
            if (writeRc < 0)
            {
                if (res.Fds != null)
                    foreach (var f in res.Fds)
                        f.Close();
                return writeRc;
            }

            if (msg.msg_name != 0 && msg.msg_namelen > 0)
            {
                var addrRes = new SocketAddressResult(res.SourceEndPoint, res.SourceSunPathRaw);
                WriteAnySockaddr(engine, file, msg.msg_name, msgPtr + 4, addrRes);
            }

            var outMsgFlags = 0;

            if (res.Fds != null && res.Fds.Count > 0 && msg.msg_control != 0 && msg.msg_controllen >= 16)
            {
                var cloexec = (flags & LinuxConstants.MSG_CMSG_CLOEXEC) != 0;
                WriteCmsgFds(engine, msg.msg_control, msg.msg_controllen, res.Fds, out var outCtlLen, cloexec);
                if (outCtlLen < 12 + res.Fds.Count * 4)
                    outMsgFlags |= LinuxConstants.MSG_CTRUNC;
                WriteBackMsgControllen(engine, msgPtr, outCtlLen);
            }
            else
            {
                if (res.Fds != null)
                {
                    if (res.Fds.Count > 0) outMsgFlags |= LinuxConstants.MSG_CTRUNC;
                    foreach (var f in res.Fds)
                        f.Close();
                }
                WriteBackMsgControllen(engine, msgPtr, 0);
            }

            WriteBackMsgFlags(engine, msgPtr, outMsgFlags);

            return bytes;
        }

        if (file.OpenedInode is NetlinkRouteSocketInode netlinkInode)
        {
            var bytes = await netlinkInode.RecvAsync(file, task, buf, flags | msg.msg_flags, buf.Length);
            if (bytes < 0) return bytes;

            var writeRc = WriteIovecs(engine, buf.AsSpan(0, bytes), iovsRes.Iovecs);
            if (writeRc < 0) return writeRc;

            WriteBackMsgControllen(engine, msgPtr, 0);
            return bytes;
        }

        return -(int)Errno.ENOTSOCK;
    }

    private async ValueTask<int> SysSocketPair(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var task = engine.Owner as FiberTask;
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

        var inode1 = new UnixSocketInode(0, MemfdSuperBlock, sockType);
        var inode2 = new UnixSocketInode(0, MemfdSuperBlock, sockType);
        inode1.ConnectPair(inode2);
        inode2.ConnectPair(inode1);

        var file1 = new LinuxFile(new Dentry($"socket:[{inode1.Ino}]", inode1, null, MemfdSuperBlock),
            fileFlags, AnonMount);
        var file2 = new LinuxFile(new Dentry($"socket:[{inode2.Ino}]", inode2, null, MemfdSuperBlock),
            fileFlags, AnonMount);

        var fd1 = AllocFD(file1);
        var fd2 = AllocFD(file2);

        var buf = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), fd1);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), fd2);
        if (!engine.CopyToUser(svPtr, buf))
        {
            FreeFD(fd1);
            FreeFD(fd2);
            return -(int)Errno.EFAULT;
        }

        return 0;
    }

    private async ValueTask<int> SysSocketCall(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var call = (int)a1;
        var argsPtr = a2;

        var args = new uint[6];
        var argCount = GetSocketCallArgCount(call);
        if (argCount < 0) return -(int)Errno.ENOSYS;
        if (argCount > 0)
        {
            var argsRaw = new byte[argCount * 4];
            if (!engine.CopyFromUser(argsPtr, argsRaw))
            {
                Logger.LogWarning(
                    "[Socket] socketcall failed to read args call={Call} argsPtr=0x{ArgsPtr:X8} argCount={ArgCount}",
                    call, argsPtr, argCount);
                return -(int)Errno.EFAULT;
            }

            for (var i = 0; i < argCount; i++)
                args[i] = BinaryPrimitives.ReadUInt32LittleEndian(argsRaw.AsSpan(i * 4, 4));
        }

        return call switch
        {
            1 /* SYS_SOCKET */ => await SysSocket(engine, args[0], args[1], args[2], 0, 0, 0),
            2 /* SYS_BIND */ => await SysBind(engine, args[0], args[1], args[2], 0, 0, 0),
            3 /* SYS_CONNECT */ => await SysConnect(engine, args[0], args[1], args[2], 0, 0, 0),
            4 /* SYS_LISTEN */ => await SysListen(engine, args[0], args[1], args[2], 0, 0, 0),
            5 /* SYS_ACCEPT */ => await SysAccept(engine, args[0], args[1], args[2], 0, 0, 0),
            6 /* SYS_GETSOCKNAME */ => await SysGetSockName(engine, args[0], args[1], args[2], 0, 0, 0),
            7 /* SYS_GETPEERNAME */ => await SysGetPeerName(engine, args[0], args[1], args[2], 0, 0, 0),
            8 /* SYS_SOCKETPAIR */ => await SysSocketPair(engine, args[0], args[1], args[2], args[3], 0, 0),
            9 /* SYS_SEND */ => await SysSend(engine, args[0], args[1], args[2], args[3], 0, 0),
            10 /* SYS_RECV */ => await SysRecv(engine, args[0], args[1], args[2], args[3], 0, 0),
            11 /* SYS_SENDTO */ => await SysSendTo(engine, args[0], args[1], args[2], args[3], args[4], args[5]),
            12 /* SYS_RECVFROM */ => await SysRecvFrom(engine, args[0], args[1], args[2], args[3], args[4], args[5]),
            13 /* SYS_SHUTDOWN */ => await SysShutdown(engine, args[0], args[1], 0, 0, 0, 0),
            14 /* SYS_SETSOCKOPT */ => await SysSetSockOpt(engine, args[0], args[1], args[2], args[3], args[4], 0),
            15 /* SYS_GETSOCKOPT */ => await SysGetSockOpt(engine, args[0], args[1], args[2], args[3], args[4], 0),
            16 /* SYS_SENDMSG */ => await SysSendMsg(engine, args[0], args[1], args[2], 0, 0, 0),
            17 /* SYS_RECVMSG */ => await SysRecvMsg(engine, args[0], args[1], args[2], 0, 0, 0),
            18 /* SYS_ACCEPT4 */ => await SysAccept4(engine, args[0], args[1], args[2], args[3], 0, 0),
            19 /* SYS_RECVMMSG */ => await SysRecvMMsg(engine, args[0], args[1], args[2], args[3], args[4], 0),
            20 /* SYS_SENDMMSG */ => await SysSendMMsg(engine, args[0], args[1], args[2], args[3], 0, 0),
            _ => -(int)Errno.ENOSYS
        };
    }


    private static (object? Endpoint, int Error) ReadAnySockaddr(Engine engine, LinuxFile file, uint addrPtr,
        int addrLen)
    {
        if (addrPtr == 0) return (null, 0); // valid for some calls
        if (file.OpenedInode is UnixSocketInode || file.OpenedInode is NetlinkRouteSocketInode)
        {
            var res = ReadUnixSockaddr(engine, addrPtr, addrLen);
            return (res.Address, res.Error);
        }

        var ep = ReadSockaddr(engine, addrPtr, addrLen);
        if (ep == null) return (null, -(int)Errno.EINVAL);
        return (ep, 0);
    }

    private static void WriteAnySockaddr(Engine engine, LinuxFile file, uint addrPtr, uint addrLenPtr,
        SocketAddressResult res)
    {
        if (addrPtr == 0 || addrLenPtr == 0) return;
        if (res.UnixAddressRaw != null || file.OpenedInode is UnixSocketInode ||
            file.OpenedInode is NetlinkRouteSocketInode)
            WriteSockaddrUnix(engine, addrPtr, addrLenPtr, res.UnixAddressRaw);
        else if (res.EndPoint != null) WriteSockaddr(engine, addrPtr, addrLenPtr, res.EndPoint);
    }

    private static int GetSocketCallArgCount(int call)
    {
        return call switch
        {
            1 => 3, // socket
            2 => 3, // bind
            3 => 3, // connect
            4 => 2, // listen
            5 => 3, // accept
            6 => 3, // getsockname
            7 => 3, // getpeername
            8 => 4, // socketpair
            9 => 4, // send
            10 => 4, // recv
            11 => 6, // sendto
            12 => 6, // recvfrom
            13 => 2, // shutdown
            14 => 5, // setsockopt
            15 => 5, // getsockopt
            16 => 3, // sendmsg
            17 => 3, // recvmsg
            18 => 4, // accept4
            19 => 5, // recvmmsg
            20 => 4, // sendmmsg
            _ => -1
        };
    }

    private struct MsgHdr
    {
        public uint msg_name;
        public uint msg_namelen;
        public uint msg_iov;
        public int msg_iovlen;
        public uint msg_control;
        public int msg_controllen;
        public int msg_flags;
    }


    private static (MsgHdr Value, int Error) ReadMsgHdr(Engine engine, uint msgPtr)
    {
        var msgRaw = new byte[28];
        if (!engine.CopyFromUser(msgPtr, msgRaw)) return (default, -(int)Errno.EFAULT);
        var msg = new MsgHdr
        {
            msg_name = BinaryPrimitives.ReadUInt32LittleEndian(msgRaw.AsSpan(0, 4)),
            msg_namelen = BinaryPrimitives.ReadUInt32LittleEndian(msgRaw.AsSpan(4, 4)),
            msg_iov = BinaryPrimitives.ReadUInt32LittleEndian(msgRaw.AsSpan(8, 4)),
            msg_iovlen = BinaryPrimitives.ReadInt32LittleEndian(msgRaw.AsSpan(12, 4)),
            msg_control = BinaryPrimitives.ReadUInt32LittleEndian(msgRaw.AsSpan(16, 4)),
            msg_controllen = BinaryPrimitives.ReadInt32LittleEndian(msgRaw.AsSpan(20, 4)),
            msg_flags = BinaryPrimitives.ReadInt32LittleEndian(msgRaw.AsSpan(24, 4))
        };
        if (msg.msg_iovlen < 0 || msg.msg_iovlen > 1024) return (msg, -(int)Errno.EINVAL);
        if (msg.msg_controllen < 0 || msg.msg_controllen > 1 << 20) return (msg, -(int)Errno.EINVAL);
        return (msg, 0);
    }

    private static (byte[] Buffer, int Error) ReadIovecs(Engine engine, uint iovPtr, int iovLen)
    {
        if (iovLen == 0) return ([], 0);
        var res = ReadIovecsDef(engine, iovPtr, iovLen);
        if (res.Error < 0) return ([], res.Error);
        long totalBytes = 0;
        foreach (var iov in res.Iovecs) totalBytes += iov.Len;
        var buffer = new byte[(int)totalBytes];
        var offset = 0;
        foreach (var iov in res.Iovecs)
            if (iov.Len > 0)
            {
                if (!engine.CopyFromUser(iov.BaseAddr, buffer.AsSpan(offset, (int)iov.Len)))
                    return ([], -(int)Errno.EFAULT);
                offset += (int)iov.Len;
            }

        return (buffer, 0);
    }

    private static (Iovec[] Iovecs, int Error) ReadIovecsDef(Engine engine, uint iovPtr, int iovLen)
    {
        if (iovLen == 0) return ([], 0);
        var iovs = new Iovec[iovLen];
        long totalBytes = 0;
        for (var i = 0; i < iovLen; i++)
        {
            var iovRaw = new byte[8];
            if (!engine.CopyFromUser(iovPtr + (uint)(i * 8), iovRaw)) return ([], -(int)Errno.EFAULT);
            iovs[i] = new Iovec
            {
                BaseAddr = BinaryPrimitives.ReadUInt32LittleEndian(iovRaw.AsSpan(0, 4)),
                Len = (uint)BinaryPrimitives.ReadInt32LittleEndian(iovRaw.AsSpan(4, 4))
            };
            if ((int)iovs[i].Len < 0) return ([], -(int)Errno.EINVAL);
            totalBytes += iovs[i].Len;
            if (totalBytes > int.MaxValue) return ([], -(int)Errno.EINVAL);
        }

        return (iovs, 0);
    }

    private static int WriteIovecs(Engine engine, ReadOnlySpan<byte> data, Iovec[] iovs)
    {
        var offset = 0;
        foreach (var iov in iovs)
            if (iov.Len > 0 && offset < data.Length)
            {
                var len = (int)Math.Min(iov.Len, (uint)(data.Length - offset));
                if (!engine.CopyToUser(iov.BaseAddr, data.Slice(offset, len))) return -(int)Errno.EFAULT;
                offset += len;
            }

        return 0;
    }

    private static (bool Success, List<LinuxFile>? Fds) ReadCmsgFds(Engine engine, uint controlPtr, int controlLen)
    {
        if (controlLen < 16 || controlPtr == 0) return (true, null);
        var cmRaw = new byte[controlLen];
        if (!engine.CopyFromUser(controlPtr, cmRaw)) return (false, null);

        List<LinuxFile> sendFds = new();
        var cmsgOffset = 0;
        while (cmsgOffset + 12 <= controlLen)
        {
            var cmsgLen = BinaryPrimitives.ReadInt32LittleEndian(cmRaw.AsSpan(cmsgOffset, 4));
            if (cmsgLen < 12 || cmsgOffset + cmsgLen > controlLen) break;
            var cmsgLevel = BinaryPrimitives.ReadInt32LittleEndian(cmRaw.AsSpan(cmsgOffset + 4, 4));
            var cmsgType = BinaryPrimitives.ReadInt32LittleEndian(cmRaw.AsSpan(cmsgOffset + 8, 4));

            if (cmsgLevel == LinuxConstants.SOL_SOCKET && cmsgType == 1 /* SCM_RIGHTS */)
            {
                var task = engine.Owner as FiberTask;
                if (task != null)
                {
                    var fdCount = (cmsgLen - 12) / 4;
                    for (var j = 0; j < fdCount; j++)
                    {
                        var cmsgFd = BinaryPrimitives.ReadInt32LittleEndian(cmRaw.AsSpan(cmsgOffset + 12 + j * 4, 4));
                        var file = task.CPU.CurrentSyscallManager!.GetFD(cmsgFd);
                        if (file != null) sendFds.Add(file);
                    }
                }
            }

            cmsgOffset += (cmsgLen + 3) & ~3;
        }

        return (true, sendFds.Count > 0 ? sendFds : null);
    }

    private static void WriteCmsgFds(Engine engine, uint controlPtr, int controlLen, List<LinuxFile> fds, out int writtenBytes, bool cloexec = false)
    {
        writtenBytes = 0;
        var task = engine.Owner as FiberTask;
        if (task == null) return;
        var sm = task.CPU.CurrentSyscallManager;
        if (sm == null) return;

        var maxFds = (controlLen - 12) / 4;
        var numFds = Math.Min(fds.Count, maxFds);
        var reqLen = 12 + numFds * 4;
        if (reqLen > controlLen) return;

        var cmRaw = new byte[reqLen];
        BinaryPrimitives.WriteInt32LittleEndian(cmRaw.AsSpan(0, 4), reqLen);
        BinaryPrimitives.WriteInt32LittleEndian(cmRaw.AsSpan(4, 4), LinuxConstants.SOL_SOCKET);
        BinaryPrimitives.WriteInt32LittleEndian(cmRaw.AsSpan(8, 4), 1 /* SCM_RIGHTS */);

        for (int i = 0; i < numFds; i++) {
            var fd = sm.AllocFD(fds[i]);
            if (cloexec) sm.SetFdCloseOnExec(fd, true);
            BinaryPrimitives.WriteInt32LittleEndian(cmRaw.AsSpan(12 + i * 4, 4), fd);
        }

        engine.CopyToUser(controlPtr, cmRaw);
        writtenBytes = reqLen;
    }

    private static void WriteBackMsgFlags(Engine engine, uint msgPtr, int msgFlags)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, msgFlags);
        engine.CopyToUser(msgPtr + 24, buf);
    }

    private static void WriteBackMsgControllen(Engine engine, uint msgPtr, int writtenLen)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, writtenLen);
        engine.CopyToUser(msgPtr + 20, buf);
    }

    // --- Helpers ---


    internal static (UnixSockaddrInfo? Address, int Error) ReadUnixSockaddr(Engine engine, uint addrPtr, int addrLen)
    {
        if (addrPtr == 0) return (null, -(int)Errno.EFAULT);
        if (addrLen < 2 || addrLen > 110) return (null, -(int)Errno.EINVAL);

        var buf = new byte[addrLen];
        if (!engine.CopyFromUser(addrPtr, buf)) return (null, -(int)Errno.EFAULT);

        var family = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0, 2));
        if (family != LinuxConstants.AF_UNIX) return (null, -(int)Errno.EAFNOSUPPORT);

        var sunPathLen = addrLen - 2;
        if (sunPathLen <= 0)
            return (new UnixSockaddrInfo { SunPathRaw = [], IsAbstract = false }, 0);

        var raw = buf.AsSpan(2, sunPathLen).ToArray();
        if (raw[0] == 0)
        {
            var abstractBody = raw.Length > 1 ? raw.AsSpan(1).ToArray() : [];
            return (new UnixSockaddrInfo
            {
                SunPathRaw = raw,
                IsAbstract = true,
                AbstractKey = Convert.ToHexString(abstractBody)
            }, 0);
        }

        var nul = Array.IndexOf(raw, (byte)0);
        var pathLen = nul >= 0 ? nul : raw.Length;
        if (pathLen <= 0) return (null, -(int)Errno.EINVAL);

        var pathBytes = raw.AsSpan(0, pathLen).ToArray();
        var canonicalRaw = new byte[pathBytes.Length + 1];
        pathBytes.CopyTo(canonicalRaw.AsSpan(0, pathBytes.Length));
        canonicalRaw[^1] = 0;
        return (new UnixSockaddrInfo
        {
            SunPathRaw = canonicalRaw,
            IsAbstract = false,
            Path = Encoding.UTF8.GetString(pathBytes)
        }, 0);
    }

    internal static void WriteSockaddrUnix(Engine engine, uint addrPtr, uint addrLenPtr, byte[]? sunPathRaw)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        if (!engine.CopyFromUser(addrLenPtr, lenBuf)) return;
        var maxLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        if (maxLen < 0) maxLen = 0;

        var raw = sunPathRaw ?? [];
        var fullLen = 2 + raw.Length;
        var buf = new byte[Math.Max(fullLen, 2)];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), LinuxConstants.AF_UNIX);
        if (raw.Length > 0)
            raw.CopyTo(buf.AsSpan(2));

        var toCopy = Math.Min(maxLen, fullLen);
        if (toCopy > 0)
            engine.CopyToUser(addrPtr, buf.AsSpan(0, toCopy));

        BinaryPrimitives.WriteInt32LittleEndian(lenBuf, fullLen);
        engine.CopyToUser(addrLenPtr, lenBuf);
    }

    private static bool WriteInt32ToUser(Engine engine, uint ptr, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        return engine.CopyToUser(ptr, buf);
    }

    private static void RollbackAllocatedFds(SyscallManager sm, List<int> fds)
    {
        foreach (var fd in fds)
            sm.FreeFD(fd);
        fds.Clear();
    }

    private static void ReleaseReceivedRights(List<LinuxFile>? fds)
    {
        if (fds == null || fds.Count == 0) return;
        foreach (var file in fds)
            file.Close();
    }

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

    private static void WriteSockaddrNetlink(Engine engine, uint addrPtr, uint addrLenPtr)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        if (!engine.CopyFromUser(addrLenPtr, lenBuf)) return;
        var maxLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);

        Span<byte> addr = stackalloc byte[12];
        addr.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(addr.Slice(0, 2), LinuxConstants.AF_NETLINK);
        var toCopy = Math.Min(maxLen, 12);
        engine.CopyToUser(addrPtr, addr.Slice(0, toCopy));

        BinaryPrimitives.WriteInt32LittleEndian(lenBuf, 12);
        engine.CopyToUser(addrLenPtr, lenBuf);
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