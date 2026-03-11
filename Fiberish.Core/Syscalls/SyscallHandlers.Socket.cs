using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Fiberish.Auth.Permission;
using Fiberish.Core;
using Fiberish.Core.Net;
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

        if (domain == LinuxConstants.AF_NETLINK)
        {
            if (realType != LinuxConstants.SOCK_RAW && realType != LinuxConstants.SOCK_DGRAM)
                return -(int)Errno.ESOCKTNOSUPPORT;
            if (protocol != LinuxConstants.NETLINK_ROUTE)
                return -(int)Errno.EPROTONOSUPPORT;

            var inode = new NetlinkRouteSocketInode(0, sm.MemfdSuperBlock,
                () => NetDeviceSnapshotProvider.Capture(sm.NetworkMode,
                    sm.TryGetPrivateNetNamespace()));
            var fileFlags = FileFlags.O_RDWR;
            if ((type & LinuxConstants.SOCK_NONBLOCK) != 0) fileFlags |= FileFlags.O_NONBLOCK;
            if ((type & LinuxConstants.SOCK_CLOEXEC) != 0) fileFlags |= FileFlags.O_CLOEXEC;

            var dentry = new Dentry($"socket:[{inode.Ino}]", inode, null, sm.MemfdSuperBlock);
            var file = new LinuxFile(dentry, fileFlags, sm.AnonMount);
            return sm.AllocFD(file);
        }

        if (domain == LinuxConstants.AF_UNIX)
        {
            if (protocol != 0 && protocol != LinuxConstants.AF_UNIX)
                return -(int)Errno.EPROTONOSUPPORT;

            if (realType == LinuxConstants.SOCK_STREAM) sockType = SocketType.Stream;
            else if (realType == LinuxConstants.SOCK_DGRAM) sockType = SocketType.Dgram;
            else if (realType == LinuxConstants.SOCK_SEQPACKET) sockType = SocketType.Seqpacket;
            else return -(int)Errno.EINVAL;

            var inode = new UnixSocketInode(0, sm.MemfdSuperBlock, sockType);
            var fileFlags = FileFlags.O_RDWR;
            if ((type & LinuxConstants.SOCK_NONBLOCK) != 0) fileFlags |= FileFlags.O_NONBLOCK;
            if ((type & LinuxConstants.SOCK_CLOEXEC) != 0) fileFlags |= FileFlags.O_CLOEXEC;

            var dentry = new Dentry($"socket:[{inode.Ino}]", inode, null, sm.MemfdSuperBlock);
            var file = new LinuxFile(dentry, fileFlags, sm.AnonMount);
            return sm.AllocFD(file);
        }

        if (domain == LinuxConstants.AF_INET) af = AddressFamily.InterNetwork;
        else if (domain == LinuxConstants.AF_INET6) af = AddressFamily.InterNetworkV6;
        else return -(int)Errno.EAFNOSUPPORT;

        if (realType == LinuxConstants.SOCK_STREAM) sockType = SocketType.Stream;
        else if (realType == LinuxConstants.SOCK_DGRAM) sockType = SocketType.Dgram;
        else if (realType == LinuxConstants.SOCK_RAW) sockType = SocketType.Raw;
        else return -(int)Errno.EINVAL;

        if (sm.NetworkMode == NetworkMode.Private)
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
                inode = CreateHostSocketForLinuxPingSemantics(sm, af, proto, sockType);
            else
                inode = new HostSocketInode(0, sm.MemfdSuperBlock, af, sockType, proto);

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
    private static HostSocketInode CreateHostSocketForLinuxPingSemantics(SyscallManager sm, AddressFamily af,
        ProtocolType proto,
        SocketType linuxSocketType)
    {
        if (OperatingSystem.IsMacOS())
            return new HostSocketInode(0, sm.MemfdSuperBlock, af, SocketType.Dgram, proto,
                linuxSocketType);

        if (linuxSocketType == SocketType.Dgram)
            try
            {
                return new HostSocketInode(0, sm.MemfdSuperBlock, af, SocketType.Dgram, proto,
                    SocketType.Dgram);
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode is SocketError.ProtocolNotSupported or SocketError.OperationNotSupported)
            {
                // Some hosts do not expose Linux-style ping sockets but still allow raw ICMP.
                // Keep guest-visible SO_TYPE as SOCK_DGRAM to preserve Linux ABI.
                return new HostSocketInode(0, sm.MemfdSuperBlock, af, SocketType.Raw, proto,
                    SocketType.Dgram);
            }

        return new HostSocketInode(0, sm.MemfdSuperBlock, af, SocketType.Raw, proto, linuxSocketType);
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
        if (file.OpenedInode is UnixSocketInode unixSock)
        {
            var parsed = ReadUnixSockaddr(sm.Engine, addrPtr, addrLen);
            if (parsed.Error < 0) return parsed.Error;
            var unixAddr = parsed.Address;
            if (unixAddr == null) return -(int)Errno.EINVAL;

            UnixSocketInode? target = null;
            if (unixAddr.IsAbstract)
            {
                target = sm.LookupUnixAbstractSocket(unixAddr.AbstractKey);
            }
            else
            {
                var loc = sm.PathWalk(unixAddr.Path);
                if (!loc.IsValid || loc.Dentry?.Inode == null) return -(int)Errno.ENOENT;
                if (loc.Dentry.Inode.Type != InodeType.Socket) return -(int)Errno.ECONNREFUSED;
                target = sm.LookupUnixPathSocket(loc.Dentry.Inode);
            }

            if (target == null) return -(int)Errno.ECONNREFUSED;
            if (unixSock.UnixSocketType != target.UnixSocketType) return -(int)Errno.EPROTOTYPE;

            if (unixSock.UnixSocketType == SocketType.Dgram)
            {
                unixSock.ConnectPair(target);
                unixSock.SetPeerSunPathRaw(target.GetLocalSunPathRaw());
                return 0;
            }

            if (unixSock.IsConnected) return -(int)Errno.EISCONN;
            if (!target.IsListening) return -(int)Errno.ECONNREFUSED;

            var serverConn = new UnixSocketInode(0, sm.MemfdSuperBlock, unixSock.UnixSocketType);
            unixSock.ConnectPair(serverConn);
            serverConn.ConnectPair(unixSock);
            unixSock.SetPeerSunPathRaw(target.GetLocalSunPathRaw());
            serverConn.SetLocalSunPathRaw(target.GetLocalSunPathRaw());
            serverConn.SetPeerSunPathRaw(unixSock.GetLocalSunPathRaw());

            var enqueueRc = target.EnqueueConnection(serverConn);
            if (enqueueRc < 0)
            {
                unixSock.DisconnectPeer();
                return enqueueRc;
            }

            return 0;
        }

        if (file.OpenedInode is HostSocketInode sockInode)
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

        if (file.OpenedInode is NetstackSocketInode netInode)
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
        if (file.OpenedInode is UnixSocketInode unixSock)
        {
            if (unixSock.IsBound) return -(int)Errno.EINVAL;

            var parsed = ReadUnixSockaddr(sm.Engine, addrPtr, addrLen);
            if (parsed.Error < 0) return parsed.Error;
            var unixAddr = parsed.Address;
            if (unixAddr == null || unixAddr.SunPathRaw.Length == 0) return -(int)Errno.EINVAL;

            if (unixAddr.IsAbstract)
            {
                if (!sm.TryBindUnixAbstractSocket(unixAddr.AbstractKey, unixSock))
                    return -(int)Errno.EADDRINUSE;
                unixSock.SetLocalSunPathRaw(unixAddr.SunPathRaw);
                unixSock.SetReleaseUnbindCallback(sm.UnbindUnixSocket);
                return 0;
            }

            var (parent, name, createErr) = sm.PathWalkForCreate(unixAddr.Path);
            if (createErr < 0) return createErr;
            if (!parent.IsValid || string.IsNullOrEmpty(name)) return -(int)Errno.EINVAL;
            if (parent.Mount != null && parent.Mount.IsReadOnly) return -(int)Errno.EROFS;

            var existing = sm.PathWalk(unixAddr.Path);
            if (existing.IsValid) return -(int)Errno.EADDRINUSE;

            var currentTask = sm.Engine.Owner as FiberTask;
            var uid = currentTask?.Process.EUID ?? 0;
            var gid = currentTask?.Process.EGID ?? 0;
            var mode = DacPolicy.ApplyUmask(unixSock.Mode & 0x0FFF, currentTask?.Process.Umask ?? 0);
            var socketDentry = new Dentry(name, null, parent.Dentry, parent.Dentry!.SuperBlock);

            try
            {
                parent.Dentry.Inode!.Mknod(socketDentry, mode, uid, gid, InodeType.Socket, 0);
            }
            catch (Exception ex)
            {
                return MapFsExceptionToErrno(ex, Errno.EACCES);
            }

            if (socketDentry.Inode == null)
            {
                try
                {
                    parent.Dentry.Inode!.Unlink(name);
                    _ = parent.Dentry.TryUncacheChild(name, "SysBind.rollback.mknod-null-inode", out _);
                }
                catch
                {
                    // Best effort rollback.
                }

                return -(int)Errno.EIO;
            }

            if (!sm.TryBindUnixPathSocket(socketDentry.Inode, unixSock))
            {
                try
                {
                    parent.Dentry.Inode!.Unlink(name);
                    _ = parent.Dentry.TryUncacheChild(name, "SysBind.rollback.bind-failed", out _);
                }
                catch
                {
                    // Best effort rollback.
                }

                return -(int)Errno.EADDRINUSE;
            }

            unixSock.SetLocalSunPathRaw(unixAddr.SunPathRaw);
            unixSock.SetReleaseUnbindCallback(sm.UnbindUnixSocket);
            return 0;
        }

        if (file.OpenedInode is HostSocketInode sockInode)
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

        if (file.OpenedInode is NetstackSocketInode netInode)
        {
            var endpoint = ReadSockaddr(sm.Engine, addrPtr, addrLen) as IPEndPoint;
            if (endpoint == null) return -(int)Errno.EINVAL;
            return netInode.Bind(endpoint);
        }

        if (file.OpenedInode is NetlinkRouteSocketInode)
            // Userspace (iproute2/busybox ip) binds AF_NETLINK sockets before dump requests.
            // For our in-process route socket, bind has no side effects.
            return 0;

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
        if (file.OpenedInode is UnixSocketInode unixSock)
            return unixSock.Listen(backlog);

        if (file.OpenedInode is HostSocketInode sockInode)
            try
            {
                sockInode.NativeSocket!.Listen(backlog);
                return 0;
            }
            catch (SocketException ex)
            {
                return -LinuxToWindowsSocketError(ex.SocketErrorCode);
            }

        if (file.OpenedInode is NetstackSocketInode netInode)
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
        if (file.OpenedInode is UnixSocketInode unixSock)
        {
            WriteSockaddrUnix(sm.Engine, addrPtr, addrLenPtr, unixSock.GetLocalSunPathRaw());
            return 0;
        }

        if (file.OpenedInode is HostSocketInode sockInode)
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

        if (file.OpenedInode is NetstackSocketInode netInode)
        {
            WriteSockaddr(sm.Engine, addrPtr, addrLenPtr, netInode.LocalEndPoint ?? new IPEndPoint(IPAddress.Any, 0));
            return 0;
        }

        if (file.OpenedInode is NetlinkRouteSocketInode)
        {
            WriteSockaddrNetlink(sm.Engine, addrPtr, addrLenPtr);
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
        if (file.OpenedInode is UnixSocketInode unixSock)
        {
            if (!unixSock.IsConnected) return -(int)Errno.ENOTCONN;
            WriteSockaddrUnix(sm.Engine, addrPtr, addrLenPtr, unixSock.GetPeerSunPathRaw());
            return 0;
        }

        if (file.OpenedInode is HostSocketInode sockInode)
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

        if (file.OpenedInode is NetstackSocketInode netInode)
        {
            if (netInode.RemoteEndPoint == null) return -(int)Errno.ENOTCONN;
            WriteSockaddr(sm.Engine, addrPtr, addrLenPtr, netInode.RemoteEndPoint);
            return 0;
        }

        if (file.OpenedInode is NetlinkRouteSocketInode)
            return -(int)Errno.ENOTCONN;

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
        if (file.OpenedInode is UnixSocketInode unixSock)
        {
            var accepted = await unixSock.AcceptAsync(file, flags);
            if (accepted.Rc != 0 || accepted.Inode == null)
                return accepted.Rc;

            var fileFlags = FileFlags.O_RDWR;
            if ((flags & LinuxConstants.SOCK_NONBLOCK) != 0) fileFlags |= FileFlags.O_NONBLOCK;
            if ((flags & LinuxConstants.SOCK_CLOEXEC) != 0) fileFlags |= FileFlags.O_CLOEXEC;

            var dentry = new Dentry($"socket:[{accepted.Inode.Ino}]", accepted.Inode, null, sm.MemfdSuperBlock);
            var newFile = new LinuxFile(dentry, fileFlags, sm.AnonMount);

            if (addrPtr != 0 && addrLenPtr != 0)
                WriteSockaddrUnix(sm.Engine, addrPtr, addrLenPtr, accepted.Inode.GetPeerSunPathRaw());

            return sm.AllocFD(newFile);
        }

        if (file.OpenedInode is HostSocketInode sockInode)
            try
            {
                var newSock = await sockInode.AcceptAsync(file, flags);

                var newInode = new HostSocketInode(0, sm.MemfdSuperBlock, newSock);
                var fileFlags = FileFlags.O_RDWR;
                if ((flags & 0x800) != 0) fileFlags |= FileFlags.O_NONBLOCK;
                if ((flags & 0x80000) != 0) fileFlags |= FileFlags.O_CLOEXEC;

                var dentry = new Dentry($"socket:[{newInode.Ino}]", newInode, null, sm.MemfdSuperBlock);
                var newFile = new LinuxFile(dentry, fileFlags, sm.AnonMount);

                if (addrPtr != 0 && addrLenPtr != 0)
                    WriteSockaddr(sm.Engine, addrPtr, addrLenPtr, newSock.RemoteEndPoint);

                return sm.AllocFD(newFile);
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.Interrupted)
                    return -(int)Errno.ERESTARTSYS;
                return -LinuxToWindowsSocketError(ex.SocketErrorCode);
            }

        if (file.OpenedInode is NetstackSocketInode netInode)
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

    private static async ValueTask<int> SysShutdown(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fd = (int)a1;
        var how = (int)a2;
        var file = sm.GetFD(fd);
        if (file == null) return -(int)Errno.EBADF;

        if (file.OpenedInode is UnixSocketInode unixInode)
            return unixInode.Shutdown(how);

        if (file.OpenedInode is HostSocketInode sockInode)
            try
            {
                var mode = how switch
                {
                    0 => SocketShutdown.Receive,
                    1 => SocketShutdown.Send,
                    2 => SocketShutdown.Both,
                    _ => throw new ArgumentOutOfRangeException(nameof(how))
                };
                sockInode.NativeSocket!.Shutdown(mode);
                return 0;
            }
            catch (ArgumentOutOfRangeException)
            {
                return -(int)Errno.EINVAL;
            }
            catch (SocketException ex)
            {
                return -LinuxToWindowsSocketError(ex.SocketErrorCode);
            }
            catch (ObjectDisposedException)
            {
                return -(int)Errno.ENOTCONN;
            }

        if (file.OpenedInode is NetstackSocketInode netInode)
            return netInode.Shutdown(how);

        return -(int)Errno.ENOTSOCK;
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

        if (file.OpenedInode is UnixSocketInode unixSock)
        {
            UnixSocketInode? explicitPeer = null;
            if (destAddrPtr != 0)
            {
                if (unixSock.UnixSocketType != SocketType.Dgram)
                    return -(int)Errno.EISCONN;

                var parsed = ReadUnixSockaddr(sm.Engine, destAddrPtr, destAddrLen);
                if (parsed.Error < 0) return parsed.Error;
                var unixAddr = parsed.Address;
                if (unixAddr == null) return -(int)Errno.EINVAL;

                if (unixAddr.IsAbstract)
                {
                    explicitPeer = sm.LookupUnixAbstractSocket(unixAddr.AbstractKey);
                }
                else
                {
                    var loc = sm.PathWalk(unixAddr.Path);
                    if (!loc.IsValid || loc.Dentry?.Inode == null) return -(int)Errno.ENOENT;
                    if (loc.Dentry.Inode.Type != InodeType.Socket) return -(int)Errno.ECONNREFUSED;
                    explicitPeer = sm.LookupUnixPathSocket(loc.Dentry.Inode);
                }

                if (explicitPeer == null)
                    return unixAddr.IsAbstract ? -(int)Errno.ECONNREFUSED : -(int)Errno.ECONNREFUSED;
            }

            var rc = await unixSock.SendMessageAsync(file, buf, null, flags, explicitPeer);
            if (rc == -(int)Errno.EPIPE) task.PostSignal((int)Signal.SIGPIPE);
            return rc;
        }

        if (file.OpenedInode is HostSocketInode sockInode)
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

        if (file.OpenedInode is NetstackSocketInode netInode)
        {
            if (destAddrPtr != 0)
            {
                var endpoint = ReadSockaddr(sm.Engine, destAddrPtr, destAddrLen) as IPEndPoint;
                if (endpoint == null) return -(int)Errno.EINVAL;
                return await netInode.SendToAsync(file, buf, endpoint, flags);
            }

            return await netInode.SendAsync(file, buf, flags);
        }

        if (file.OpenedInode is NetlinkRouteSocketInode netlinkInode)
            return await netlinkInode.SendAsync(file, buf, flags);

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

        if (file.OpenedInode is UnixSocketInode unixSock)
        {
            var res = await unixSock.RecvMessageAsync(file, buf, flags, len);
            var bytes = res.BytesRead;
            if (bytes < 0) return bytes;

            if (bytes > 0)
                if (!task.CPU.CopyToUser(bufPtr, buf.AsSpan(0, bytes)))
                {
                    ReleaseReceivedRights(res.Fds);
                    return -(int)Errno.EFAULT;
                }

            if (srcAddrPtr != 0 && addrLenPtr != 0)
                WriteSockaddrUnix(sm.Engine, srcAddrPtr, addrLenPtr, res.SourceSunPathRaw);

            // recv/recvfrom cannot return SCM_RIGHTS; discard ancillary rights.
            ReleaseReceivedRights(res.Fds);
            return bytes;
        }

        if (file.OpenedInode is HostSocketInode sockInode)
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

        if (file.OpenedInode is NetstackSocketInode netInode)
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

        if (file.OpenedInode is NetlinkRouteSocketInode netlinkInode)
        {
            var bytes = await netlinkInode.RecvAsync(file, buf, flags);
            if (bytes > 0 && !task.CPU.CopyToUser(bufPtr, buf.AsSpan(0, bytes)))
                return -(int)Errno.EFAULT;
            if (bytes > 0 && srcAddrPtr != 0 && addrLenPtr != 0)
                WriteSockaddrNetlink(sm.Engine, srcAddrPtr, addrLenPtr);
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
        if (!task.CPU.CopyFromUser(msgPtr, msgRaw))
        {
            Logger.LogWarning("[Socket] recvmsg failed to read msghdr fd={Fd} msgPtr=0x{MsgPtr:X8}", fd, msgPtr);
            return -(int)Errno.EFAULT;
        }

        var iovPtr = BinaryPrimitives.ReadUInt32LittleEndian(msgRaw.AsSpan(8, 4));
        var iovLen = BinaryPrimitives.ReadInt32LittleEndian(msgRaw.AsSpan(12, 4));
        var controlPtr = BinaryPrimitives.ReadUInt32LittleEndian(msgRaw.AsSpan(16, 4));
        var controlLen = BinaryPrimitives.ReadInt32LittleEndian(msgRaw.AsSpan(20, 4));
        if (iovLen < 0 || iovLen > 1024)
        {
            Logger.LogWarning("[Socket] sendmsg invalid iovLen fd={Fd} iovLen={IovLen} msgPtr=0x{MsgPtr:X8}", fd,
                iovLen, msgPtr);
            return -(int)Errno.EINVAL;
        }

        if (controlLen < 0 || controlLen > 1 << 20) return -(int)Errno.EINVAL;

        long totalBytes = 0;
        var iovs = new (uint Base, int Len)[iovLen];
        for (var i = 0; i < iovLen; i++)
        {
            var iovRaw = new byte[8];
            if (!task.CPU.CopyFromUser(iovPtr + (uint)(i * 8), iovRaw))
            {
                Logger.LogWarning(
                    "[Socket] recvmsg failed to read iov fd={Fd} iovPtr=0x{IovPtr:X8} i={I} iovLen={IovLen} msgPtr=0x{MsgPtr:X8}",
                    fd, iovPtr, i, iovLen, msgPtr);
                return -(int)Errno.EFAULT;
            }

            iovs[i] = (BinaryPrimitives.ReadUInt32LittleEndian(iovRaw.AsSpan(0, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(iovRaw.AsSpan(4, 4)));
            if (iovs[i].Len < 0) return -(int)Errno.EINVAL;
            totalBytes += iovs[i].Len;
            if (totalBytes > int.MaxValue) return -(int)Errno.EINVAL;
        }

        var data = new byte[(int)totalBytes];
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
                if (cmsgLen < 12) return -(int)Errno.EINVAL;
                if (cmsgOffset + cmsgLen > controlLen) return -(int)Errno.EINVAL;
                var level = BinaryPrimitives.ReadInt32LittleEndian(cmsgRaw.AsSpan(cmsgOffset + 4, 4));
                var type = BinaryPrimitives.ReadInt32LittleEndian(cmsgRaw.AsSpan(cmsgOffset + 8, 4));

                if (level == 1 /* SOL_SOCKET */ && type == 1 /* SCM_RIGHTS */)
                {
                    if (((cmsgLen - 12) & 3) != 0) return -(int)Errno.EINVAL;
                    var fdCount = (cmsgLen - 12) / 4;
                    for (var i = 0; i < fdCount; i++)
                    {
                        var passedFd =
                            BinaryPrimitives.ReadInt32LittleEndian(cmsgRaw.AsSpan(cmsgOffset + 12 + i * 4, 4));
                        var passedFile = sm.GetFD(passedFd);
                        if (passedFile == null) return -(int)Errno.EBADF;
                        fds.Add(passedFile);
                    }
                }

                cmsgOffset += Math.Max(cmsgLen, 12);
                cmsgOffset = (cmsgOffset + 3) & ~3; // align to 4 bytes
            }
        }

        if (file.OpenedInode is UnixSocketInode unixSock)
        {
            var ret = await unixSock.SendMessageAsync(file, data, fds.Count > 0 ? fds : null, flags);
            if (ret == -(int)Errno.EPIPE) task.PostSignal((int)Signal.SIGPIPE);
            return ret;
        }

        if (file.OpenedInode is HostSocketInode hostSock)
        {
            // Host sockets don't support SCM_RIGHTS, fallback to basic send
            var ret = await hostSock.SendAsync(file, data, flags);
            if (ret == -(int)Errno.EPIPE) task.PostSignal((int)Signal.SIGPIPE);
            return ret;
        }

        if (file.OpenedInode is NetstackSocketInode netSock)
        {
            if (fds.Count > 0)
                return -(int)Errno.EOPNOTSUPP;

            var ret = await netSock.SendAsync(file, data, flags);
            if (ret == -(int)Errno.EPIPE) task.PostSignal((int)Signal.SIGPIPE);
            return ret;
        }

        if (file.OpenedInode is NetlinkRouteSocketInode netlinkSock)
        {
            if (fds.Count > 0)
                return -(int)Errno.EOPNOTSUPP;
            return await netlinkSock.SendAsync(file, data, flags);
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
        if (iovLen < 0 || iovLen > 1024) return -(int)Errno.EINVAL;
        if (controlLen < 0 || controlLen > 1 << 20) return -(int)Errno.EINVAL;

        long totalBytes = 0;
        var iovs = new (uint Base, int Len)[iovLen];
        for (var i = 0; i < iovLen; i++)
        {
            var iovRaw = new byte[8];
            if (!task.CPU.CopyFromUser(iovPtr + (uint)(i * 8), iovRaw)) return -(int)Errno.EFAULT;
            iovs[i] = (BinaryPrimitives.ReadUInt32LittleEndian(iovRaw.AsSpan(0, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(iovRaw.AsSpan(4, 4)));
            if (iovs[i].Len < 0) return -(int)Errno.EINVAL;
            totalBytes += iovs[i].Len;
            if (totalBytes > int.MaxValue) return -(int)Errno.EINVAL;
        }

        var buffer = new byte[(int)totalBytes];
        var bytesRead = 0;
        List<LinuxFile>? receivedFds = null;

        var namePtr = BinaryPrimitives.ReadUInt32LittleEndian(msgRaw.AsSpan(0, 4));
        var nameLenPtr = msgPtr + 4; // msg_namelen is at offset 4

        if (file.OpenedInode is UnixSocketInode unixSock)
        {
            var res = await unixSock.RecvMessageAsync(file, buffer, flags);
            if (res.BytesRead < 0) return res.BytesRead;
            bytesRead = res.BytesRead;
            receivedFds = res.Fds;
            if (bytesRead >= 0 && namePtr != 0)
                WriteSockaddrUnix(sm.Engine, namePtr, nameLenPtr, res.SourceSunPathRaw);
        }
        else if (file.OpenedInode is HostSocketInode hostSock)
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
        else if (file.OpenedInode is NetstackSocketInode netSock)
        {
            bytesRead = await netSock.RecvAsync(file, buffer, flags);
            if (bytesRead < 0) return bytesRead;

            if (bytesRead >= 0 && namePtr != 0 && netSock.RemoteEndPoint != null)
                WriteSockaddr(sm.Engine, namePtr, nameLenPtr, netSock.RemoteEndPoint);
        }
        else if (file.OpenedInode is NetlinkRouteSocketInode netlinkSock)
        {
            bytesRead = await netlinkSock.RecvAsync(file, buffer, flags);
            if (bytesRead < 0) return bytesRead;
            if (bytesRead >= 0 && namePtr != 0)
                WriteSockaddrNetlink(sm.Engine, namePtr, nameLenPtr);
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
                if (!task.CPU.CopyToUser(iov.Base, buffer.AsSpan(offset, toCopy)))
                {
                    ReleaseReceivedRights(receivedFds);
                    return -(int)Errno.EFAULT;
                }

                offset += toCopy;
            }

        var outputControlLen = 0;
        var outputMsgFlags = 0;
        List<int>? allocatedFds = null;

        if (receivedFds != null && receivedFds.Count > 0)
        {
            if (controlPtr != 0 && controlLen >= 12)
            {
                var fdCapacity = (controlLen - 12) / 4;
                var deliverCount = Math.Min(receivedFds.Count, fdCapacity);
                if (deliverCount < receivedFds.Count) outputMsgFlags |= LinuxConstants.MSG_CTRUNC;

                if (deliverCount > 0)
                {
                    allocatedFds = new List<int>(deliverCount);
                    var cloexec = (flags & LinuxConstants.MSG_CMSG_CLOEXEC) != 0;
                    for (var i = 0; i < deliverCount; i++)
                    {
                        var newFd = sm.DupFD(receivedFds[i], closeOnExec: cloexec);
                        if (newFd < 0)
                        {
                            RollbackAllocatedFds(sm, allocatedFds);
                            ReleaseReceivedRights(receivedFds);
                            return newFd;
                        }

                        allocatedFds.Add(newFd);
                    }
                }

                var cmsgLen = 12 + (allocatedFds?.Count ?? 0) * 4;
                outputControlLen = cmsgLen > 12 ? cmsgLen : 0;
                if (cmsgLen > 12)
                {
                    var cmsgRaw = new byte[cmsgLen];
                    BinaryPrimitives.WriteInt32LittleEndian(cmsgRaw.AsSpan(0, 4), cmsgLen);
                    BinaryPrimitives.WriteInt32LittleEndian(cmsgRaw.AsSpan(4, 4), 1); // SOL_SOCKET
                    BinaryPrimitives.WriteInt32LittleEndian(cmsgRaw.AsSpan(8, 4), 1); // SCM_RIGHTS
                    for (var i = 0; i < allocatedFds!.Count; i++)
                        BinaryPrimitives.WriteInt32LittleEndian(cmsgRaw.AsSpan(12 + i * 4, 4), allocatedFds[i]);

                    if (!task.CPU.CopyToUser(controlPtr, cmsgRaw))
                    {
                        RollbackAllocatedFds(sm, allocatedFds);
                        ReleaseReceivedRights(receivedFds);
                        return -(int)Errno.EFAULT;
                    }
                }
            }
            else
            {
                outputMsgFlags |= LinuxConstants.MSG_CTRUNC;
            }
        }

        if (!WriteInt32ToUser(sm.Engine, msgPtr + 20, outputControlLen) ||
            !WriteInt32ToUser(sm.Engine, msgPtr + 24, outputMsgFlags))
        {
            if (allocatedFds != null)
                RollbackAllocatedFds(sm, allocatedFds);
            ReleaseReceivedRights(receivedFds);
            return -(int)Errno.EFAULT;
        }

        ReleaseReceivedRights(receivedFds);
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
        if (!sm.Engine.CopyToUser(svPtr, buf))
        {
            sm.FreeFD(fd1);
            sm.FreeFD(fd2);
            return -(int)Errno.EFAULT;
        }

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
        var argCount = GetSocketCallArgCount(call);
        if (argCount < 0) return -(int)Errno.ENOSYS;
        if (argCount > 0)
        {
            var argsRaw = new byte[argCount * 4];
            if (!sm.Engine.CopyFromUser(argsPtr, argsRaw))
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
            13 /* SYS_SHUTDOWN */ => await SysShutdown(state, args[0], args[1], 0, 0, 0, 0),
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

    // --- Helpers ---

    private sealed class UnixSockaddrInfo
    {
        public required byte[] SunPathRaw { get; init; }
        public required bool IsAbstract { get; init; }
        public string Path { get; init; } = "";
        public string AbstractKey { get; init; } = "";
    }

    private static (UnixSockaddrInfo? Address, int Error) ReadUnixSockaddr(Engine engine, uint addrPtr, int addrLen)
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

    private static void WriteSockaddrUnix(Engine engine, uint addrPtr, uint addrLenPtr, byte[]? sunPathRaw)
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