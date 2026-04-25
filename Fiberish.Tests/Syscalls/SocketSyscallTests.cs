using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Core.Net;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class SocketSyscallTests
{
    private static ValueTask<int> CallSysSocket(TestEnv env, uint domain, uint type, uint protocol)
    {
        return env.Invoke("SysSocket", domain, type, protocol, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysSetSockOpt(TestEnv env, uint fd, uint level, uint optname, uint optval,
        uint optlen)
    {
        return env.Invoke("SysSetSockOpt", fd, level, optname, optval, optlen, 0u);
    }

    private static ValueTask<int> CallSysGetSockOpt(TestEnv env, uint fd, uint level, uint optname, uint optval,
        uint optlenPtr)
    {
        return env.Invoke("SysGetSockOpt", fd, level, optname, optval, optlenPtr, 0u);
    }

    private static ValueTask<int> CallSysBind(TestEnv env, uint fd, uint addrPtr, uint addrLen)
    {
        return env.Invoke("SysBind", fd, addrPtr, addrLen, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysListen(TestEnv env, uint fd, uint backlog)
    {
        return env.Invoke("SysListen", fd, backlog, 0u, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysConnect(TestEnv env, uint fd, uint addrPtr, uint addrLen)
    {
        return env.Invoke("SysConnect", fd, addrPtr, addrLen, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysGetSockName(TestEnv env, uint fd, uint addrPtr, uint addrLenPtr)
    {
        return env.Invoke("SysGetSockName", fd, addrPtr, addrLenPtr, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysGetPeerName(TestEnv env, uint fd, uint addrPtr, uint addrLenPtr)
    {
        return env.Invoke("SysGetPeerName", fd, addrPtr, addrLenPtr, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysAccept(TestEnv env, uint fd, uint addrPtr, uint addrLenPtr)
    {
        return env.Invoke("SysAccept", fd, addrPtr, addrLenPtr, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysSocketPair(TestEnv env, uint domain, uint type, uint protocol, uint svPtr)
    {
        return env.Invoke("SysSocketPair", domain, type, protocol, svPtr, 0u, 0u);
    }

    private static ValueTask<int> CallSysSendMsg(TestEnv env, uint fd, uint msgPtr, uint flags)
    {
        return env.Invoke("SysSendMsg", fd, msgPtr, flags, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysRecvMsg(TestEnv env, uint fd, uint msgPtr, uint flags)
    {
        return env.Invoke("SysRecvMsg", fd, msgPtr, flags, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysShutdown(TestEnv env, uint fd, uint how)
    {
        return env.Invoke("SysShutdown", fd, how, 0u, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysSend(TestEnv env, uint fd, uint bufPtr, uint len, uint flags = 0)
    {
        return env.Invoke("SysSend", fd, bufPtr, len, flags, 0u, 0u);
    }

    private static ValueTask<int> CallSysSendTo(TestEnv env, uint fd, uint bufPtr, uint len, uint flags,
        uint addrPtr = 0, uint addrLen = 0)
    {
        return env.Invoke("SysSendTo", fd, bufPtr, len, flags, addrPtr, addrLen);
    }

    private static ValueTask<int> CallSysRecvFrom(TestEnv env, uint fd, uint bufPtr, uint len, uint flags,
        uint addrPtr = 0, uint addrLen = 0)
    {
        return env.Invoke("SysRecvFrom", fd, bufPtr, len, flags, addrPtr, addrLen);
    }

    private static ValueTask<int> CallSysIoctl(TestEnv env, uint fd, uint request, uint arg)
    {
        return env.Invoke("SysIoctl", fd, request, arg, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysWrite(TestEnv env, uint fd, uint bufPtr, uint len)
    {
        return env.Invoke("SysWrite", fd, bufPtr, len, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysReadV(TestEnv env, uint fd, uint iovPtr, uint iovCount)
    {
        return env.Invoke("SysReadV", fd, iovPtr, iovCount, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysWriteV(TestEnv env, uint fd, uint iovPtr, uint iovCount)
    {
        return env.Invoke("SysWriteV", fd, iovPtr, iovCount, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysUnlink(TestEnv env, uint pathPtr)
    {
        return env.Invoke("SysUnlink", pathPtr, 0u, 0u, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysLink(TestEnv env, uint oldPathPtr, uint newPathPtr)
    {
        return env.Invoke("SysLink", oldPathPtr, newPathPtr, 0u, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysRename(TestEnv env, uint oldPathPtr, uint newPathPtr)
    {
        return env.Invoke("SysRename", oldPathPtr, newPathPtr, 0u, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysClose(TestEnv env, uint fd)
    {
        return env.Invoke("SysClose", fd, 0u, 0u, 0u, 0u, 0u);
    }

    [Fact]
    public async Task Socket_RawWithProtocolZero_ReturnsEprotonosupport()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_RAW, 0);

        if (OperatingSystem.IsMacOS())
        {
            if (rc < 0)
                Assert.True(rc is -(int)Errno.EACCES or -(int)Errno.EPERM, $"unexpected errno {rc}");
            else
                Assert.IsType<HostSocketInode>(Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rc)).Dentry.Inode);
            return;
        }

        Assert.Equal(-(int)Errno.EPROTONOSUPPORT, rc);
    }

    [Fact]
    public async Task Socket_RawWithUnsupportedProtocol_ReturnsEprotonosupport()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_RAW, LinuxConstants.IPPROTO_TCP);

        if (OperatingSystem.IsMacOS())
        {
            if (rc < 0)
                Assert.True(rc is -(int)Errno.EACCES or -(int)Errno.EPERM, $"unexpected errno {rc}");
            else
                Assert.IsType<HostSocketInode>(Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rc)).Dentry.Inode);
            return;
        }

        Assert.Equal(-(int)Errno.EPROTONOSUPPORT, rc);
    }

    [Fact]
    public async Task Socket_RawIcmp_CreatesRawSocketOrReturnsPermissionError()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET,
            LinuxConstants.SOCK_RAW | LinuxConstants.SOCK_NONBLOCK | LinuxConstants.SOCK_CLOEXEC,
            LinuxConstants.IPPROTO_ICMP);

        if (rc < 0)
        {
            Assert.True(rc is -(int)Errno.EACCES or -(int)Errno.EPERM,
                $"unexpected errno {rc}");
            return;
        }

        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rc));
        var inode = Assert.IsType<HostSocketInode>(file.Dentry.Inode);
        Assert.Equal(FileFlags.O_RDWR | FileFlags.O_NONBLOCK | FileFlags.O_CLOEXEC, file.Flags);
        Assert.Equal(SocketType.Raw, inode.LinuxSocketType);
        Assert.Equal(OperatingSystem.IsMacOS() ? SocketType.Dgram : SocketType.Raw, inode.HostSocketType);
        Assert.Equal(ProtocolType.Icmp, inode.HostProtocolType);
    }

    [Fact]
    public async Task Socket_RawIcmpV6_CreatesRawSocketOrReturnsPermissionError()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET6, LinuxConstants.SOCK_RAW,
            LinuxConstants.IPPROTO_ICMPV6);

        if (rc < 0)
        {
            Assert.True(rc is -(int)Errno.EACCES or -(int)Errno.EPERM,
                $"unexpected errno {rc}");
            return;
        }

        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rc));
        var inode = Assert.IsType<HostSocketInode>(file.Dentry.Inode);
        Assert.Equal(SocketType.Raw, inode.LinuxSocketType);
        Assert.Equal(OperatingSystem.IsMacOS() ? SocketType.Dgram : SocketType.Raw, inode.HostSocketType);
        Assert.Equal(ProtocolType.IcmpV6, inode.HostProtocolType);
    }

    [Fact]
    public async Task Socket_RawIcmpV6_OnIpv4_ReturnsEprotonosupport()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_RAW,
            LinuxConstants.IPPROTO_ICMPV6);

        if (OperatingSystem.IsMacOS())
        {
            if (rc < 0)
            {
                Assert.True(rc is -(int)Errno.EACCES or -(int)Errno.EPERM, $"unexpected errno {rc}");
            }
            else
            {
                var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rc));
                var inode = Assert.IsType<HostSocketInode>(file.Dentry.Inode);
                Assert.Equal(SocketType.Raw, inode.LinuxSocketType);
                Assert.Equal(AddressFamily.InterNetwork, inode.HostAddressFamily);
                Assert.Equal(ProtocolType.Icmp, inode.HostProtocolType);
                Assert.Equal(SocketType.Dgram, inode.HostSocketType);
            }

            return;
        }

        Assert.Equal(-(int)Errno.EPROTONOSUPPORT, rc);
    }

    [Fact]
    public async Task Socket_DgramIcmp_CreatesPingSocketOrReturnsPermissionError()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM,
            LinuxConstants.IPPROTO_ICMP);

        if (rc < 0)
        {
            Assert.True(rc is -(int)Errno.EACCES or -(int)Errno.EPERM,
                $"unexpected errno {rc}");
            return;
        }

        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rc));
        var inode = Assert.IsType<HostSocketInode>(file.Dentry.Inode);
        Assert.Equal(SocketType.Dgram, inode.LinuxSocketType);
        Assert.Equal(ProtocolType.Icmp, inode.HostProtocolType);
        Assert.Contains(inode.HostSocketType, [SocketType.Dgram, SocketType.Raw]);
    }

    [Fact]
    public async Task Socket_DgramIcmpV6_CreatesPingSocketOrReturnsPermissionError()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET6, LinuxConstants.SOCK_DGRAM,
            LinuxConstants.IPPROTO_ICMPV6);

        if (rc < 0)
        {
            Assert.True(rc is -(int)Errno.EACCES or -(int)Errno.EPERM,
                $"unexpected errno {rc}");
            return;
        }

        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rc));
        var inode = Assert.IsType<HostSocketInode>(file.Dentry.Inode);
        Assert.Equal(SocketType.Dgram, inode.LinuxSocketType);
        Assert.Equal(ProtocolType.IcmpV6, inode.HostProtocolType);
        Assert.Contains(inode.HostSocketType, [SocketType.Dgram, SocketType.Raw]);
    }

    [Fact]
    public async Task Socket_DgramIcmpV6_OnIpv4_ReturnsEprotonosupport()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM,
            LinuxConstants.IPPROTO_ICMPV6);

        Assert.Equal(-(int)Errno.EPROTONOSUPPORT, rc);
    }

    [Fact]
    public async Task SetSockOpt_UnsupportedSocketOption_ReturnsEnoprotoopt()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x10000);
        env.WriteInt32(0x10000, 1);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);

        var rc = await CallSysSetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, 0x7fff, 0x10000, 4);
        Assert.Equal(-(int)Errno.ENOPROTOOPT, rc);
    }

    [Fact]
    public async Task GetSockOpt_UnsupportedLevel_ReturnsEnoprotoopt()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x11000);
        env.MapUserPage(0x12000);
        env.WriteInt32(0x12000, 4);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);

        var rc = await CallSysGetSockOpt(env, (uint)fd, 0x7fff, 1, 0x11000, 0x12000);
        Assert.Equal(-(int)Errno.ENOPROTOOPT, rc);
    }

    [Fact]
    public async Task GetSockOpt_SoType_OnRawSocket_ReturnsSockRaw()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x13000);
        env.MapUserPage(0x14000);
        env.WriteInt32(0x14000, 4);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_RAW, LinuxConstants.IPPROTO_ICMP);
        if (fd < 0)
        {
            Assert.True(fd is -(int)Errno.EACCES or -(int)Errno.EPERM,
                $"unexpected errno {fd}");
            return;
        }

        var rc = await CallSysGetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_TYPE, 0x13000,
            0x14000);
        Assert.Equal(0, rc);
        Assert.Equal(LinuxConstants.SOCK_RAW, env.ReadInt32(0x13000));
        Assert.Equal(4, env.ReadInt32(0x14000));
    }

    [Fact]
    public async Task GetSockOpt_SoType_OnPingSocket_ReturnsSockDgram()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x15000);
        env.MapUserPage(0x16000);
        env.WriteInt32(0x16000, 4);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM,
            LinuxConstants.IPPROTO_ICMP);
        if (fd < 0)
        {
            Assert.True(fd is -(int)Errno.EACCES or -(int)Errno.EPERM,
                $"unexpected errno {fd}");
            return;
        }

        var rc = await CallSysGetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_TYPE, 0x15000,
            0x16000);
        Assert.Equal(0, rc);
        Assert.Equal(LinuxConstants.SOCK_DGRAM, env.ReadInt32(0x15000));
        Assert.Equal(4, env.ReadInt32(0x16000));
    }

    [Fact]
    public async Task SetGetSockOpt_SoLinger_OnHostStreamSocket_RoundTrips()
    {
        using var env = new TestEnv();

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(fd >= 0);

        env.MapUserPage(0x17000);
        env.MapUserPage(0x18000);
        env.MapUserPage(0x19000);

        // struct linger { int l_onoff; int l_linger; }
        env.WriteInt32(0x17000, 1);
        env.WriteInt32(0x17004, 0);
        Assert.Equal(0,
            await CallSysSetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_LINGER, 0x17000, 8));

        env.WriteInt32(0x19000, 8);
        Assert.Equal(0,
            await CallSysGetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_LINGER, 0x18000,
                0x19000));

        Assert.Equal(8, env.ReadInt32(0x19000));
        Assert.Equal(1, env.ReadInt32(0x18000));
        Assert.Equal(0, env.ReadInt32(0x18004));

        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(fd));
        var inode = Assert.IsType<HostSocketInode>(file.Dentry.Inode);
        var linger = inode.NativeSocket.LingerState;
        Assert.NotNull(linger);
        Assert.True(linger.Enabled);
        Assert.Equal(0, linger.LingerTime);
    }

    [Fact]
    public async Task SetSockOpt_TcpKeepAliveOptions_OnHostStreamSocket_AreAccepted()
    {
        using var env = new TestEnv();

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(fd >= 0);

        env.MapUserPage(0x1C000);
        env.WriteInt32(0x1C000, 30);

        Assert.Equal(0,
            await CallSysSetSockOpt(env, (uint)fd, LinuxConstants.IPPROTO_TCP, LinuxConstants.TCP_KEEPIDLE, 0x1C000,
                4));
        Assert.Equal(0,
            await CallSysSetSockOpt(env, (uint)fd, LinuxConstants.IPPROTO_TCP, LinuxConstants.TCP_KEEPINTVL, 0x1C000,
                4));
        Assert.Equal(0,
            await CallSysSetSockOpt(env, (uint)fd, LinuxConstants.IPPROTO_TCP, LinuxConstants.TCP_KEEPCNT, 0x1C000,
                4));
    }

    [Fact]
    public async Task Shutdown_UnconnectedSocket_WithSoLingerZero_ReturnsEnotconnAndKeepsFdAlive()
    {
        using var env = new TestEnv();

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(fd >= 0);

        env.MapUserPage(0x1A000);
        env.MapUserPage(0x1B000);

        // SO_LINGER { on=1, linger=0 }
        env.WriteInt32(0x1A000, 1);
        env.WriteInt32(0x1A004, 0);
        Assert.Equal(0,
            await CallSysSetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_LINGER, 0x1A000, 8));

        Assert.Equal(-(int)Errno.ENOTCONN, await CallSysShutdown(env, (uint)fd, 2));

        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(fd));
        var inode = Assert.IsType<HostSocketInode>(file.Dentry.Inode);
        _ = inode.NativeSocket.Available;

        env.WriteBytes(0x1B000, [1]);
        var sendRc = await CallSysSend(env, (uint)fd, 0x1B000, 1);
        Assert.Equal(-(int)Errno.ENOTCONN, sendRc);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_Stream_UsesNetstackSocketInode()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);

        Assert.True(fd >= 0);
        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(fd));
        Assert.IsType<NetstackSocketInode>(file.Dentry.Inode);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_Dgram_ReturnsEsocktnosupport()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);

        Assert.True(rc >= 0);
        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rc));
        Assert.IsType<NetstackSocketInode>(file.Dentry.Inode);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_Raw_ReturnsEsocktnosupport()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_RAW, 0);

        Assert.Equal(-(int)Errno.ESOCKTNOSUPPORT, rc);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_Dgram_GetSockOpt_TypeAndError_Work()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x26000);
        env.MapUserPage(0x27000);
        env.WriteInt32(0x27000, 4);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);

        Assert.Equal(0,
            await CallSysGetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_TYPE, 0x26000,
                0x27000));
        Assert.Equal(LinuxConstants.SOCK_DGRAM, env.ReadInt32(0x26000));

        env.WriteInt32(0x27000, 4);
        Assert.Equal(0,
            await CallSysGetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_ERROR, 0x26000,
                0x27000));
        Assert.Equal(0, env.ReadInt32(0x26000));
    }

    [Fact]
    public async Task Socket_HostNetwork_ClosedPortConnect_DoesNotReportFalseSuccessOrEio()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x28000);
        env.MapUserPage(0x29000);
        env.MapUserPage(0x2A000);

        int closedPort;
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            closedPort = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }

        WriteSockaddrIn(env, 0x28000, 0x7F000001u, (ushort)closedPort);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(fd >= 0);

        var connectRc = await CallSysConnect(env, (uint)fd, 0x28000, 16);
        Assert.NotEqual(0, connectRc);
        Assert.Equal(-(int)Errno.ECONNREFUSED, connectRc);

        env.WriteBytes(0x2A000, [0x42]);
        var sendRc = await CallSysSend(env, (uint)fd, 0x2A000, 1);
        Assert.Contains(sendRc, [-(int)Errno.EPIPE, -(int)Errno.ENOTCONN, -(int)Errno.ECONNREFUSED]);
    }

    [Fact]
    public async Task Socket_NetlinkRoute_CreatesNetlinkRouteInode()
    {
        using var env = new TestEnv();

        var fd = await CallSysSocket(env, LinuxConstants.AF_NETLINK, LinuxConstants.SOCK_DGRAM,
            LinuxConstants.NETLINK_ROUTE);
        Assert.True(fd >= 0);

        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(fd));
        Assert.IsType<NetlinkRouteSocketInode>(file.Dentry.Inode);
    }

    [Fact]
    public async Task Socket_NetlinkRoute_GetLinkAndAddrDump_ReturnsMultipartMessages()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        _ = env.SyscallManager.GetOrCreatePrivateNetNamespace();
        env.MapUserPage(0x33000);
        env.MapUserPage(0x34000);

        var fd = await CallSysSocket(env, LinuxConstants.AF_NETLINK,
            LinuxConstants.SOCK_DGRAM | LinuxConstants.SOCK_NONBLOCK,
            LinuxConstants.NETLINK_ROUTE);
        Assert.True(fd >= 0);

        WriteNlMsg(env, 0x33000, LinuxConstants.RTM_GETLINK, LinuxConstants.NLM_F_REQUEST | LinuxConstants.NLM_F_DUMP,
            100);
        Assert.Equal(16, await CallSysSendTo(env, (uint)fd, 0x33000, 16, 0));

        var rc1 = await CallSysRecvFrom(env, (uint)fd, 0x34000, 512, 0);
        var type1 = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x34000 + 4, 2));
        Assert.True(rc1 > 0);
        Assert.Equal(LinuxConstants.RTM_NEWLINK, type1);

        var rc2 = await CallSysRecvFrom(env, (uint)fd, 0x34000, 512, 0);
        var type2 = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x34000 + 4, 2));
        Assert.True(rc2 > 0);
        Assert.Equal(LinuxConstants.RTM_NEWLINK, type2);

        var rc3 = await CallSysRecvFrom(env, (uint)fd, 0x34000, 512, 0);
        var doneType = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x34000 + 4, 2));
        Assert.True(rc3 > 0);
        Assert.Equal(LinuxConstants.NLMSG_DONE, doneType);

        WriteNlMsg(env, 0x33000, LinuxConstants.RTM_GETADDR, LinuxConstants.NLM_F_REQUEST | LinuxConstants.NLM_F_DUMP,
            101);
        Assert.Equal(16, await CallSysSendTo(env, (uint)fd, 0x33000, 16, 0));

        var a1 = await CallSysRecvFrom(env, (uint)fd, 0x34000, 512, 0);
        var t1 = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x34000 + 4, 2));
        Assert.True(a1 > 0);
        Assert.Equal(LinuxConstants.RTM_NEWADDR, t1);

        var a2 = await CallSysRecvFrom(env, (uint)fd, 0x34000, 512, 0);
        var t2 = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x34000 + 4, 2));
        Assert.True(a2 > 0);
        Assert.Equal(LinuxConstants.RTM_NEWADDR, t2);

        var a3 = await CallSysRecvFrom(env, (uint)fd, 0x34000, 512, 0);
        var t3 = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x34000 + 4, 2));
        Assert.True(a3 > 0);
        Assert.Equal(LinuxConstants.NLMSG_DONE, t3);

        var empty = await CallSysRecvFrom(env, (uint)fd, 0x34000, 512, 0);
        Assert.Equal(-(int)Errno.EAGAIN, empty);
    }

    [Fact]
    public async Task Socket_NetlinkRoute_WritePath_ProducesDumpAndGetSockNameWorks()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        _ = env.SyscallManager.GetOrCreatePrivateNetNamespace();
        env.MapUserPage(0x38000);
        env.MapUserPage(0x39000);
        env.MapUserPage(0x3A000);
        env.WriteInt32(0x3A000, 12);

        var fd = await CallSysSocket(env, LinuxConstants.AF_NETLINK,
            LinuxConstants.SOCK_DGRAM | LinuxConstants.SOCK_NONBLOCK,
            LinuxConstants.NETLINK_ROUTE);
        Assert.True(fd >= 0);

        Assert.Equal(0, await CallSysGetSockName(env, (uint)fd, 0x39000, 0x3A000));
        Assert.Equal(12, env.ReadInt32(0x3A000));
        var family = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x39000, 2));
        Assert.Equal((ushort)LinuxConstants.AF_NETLINK, family);

        WriteNlMsg(env, 0x38000, LinuxConstants.RTM_GETLINK, LinuxConstants.NLM_F_REQUEST | LinuxConstants.NLM_F_DUMP,
            200);
        Assert.Equal(16, await CallSysWrite(env, (uint)fd, 0x38000, 16));

        var first = await CallSysRecvFrom(env, (uint)fd, 0x39000, 512, 0);
        Assert.True(first > 0);
        var msgType = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x39000 + 4, 2));
        Assert.Equal(LinuxConstants.RTM_NEWLINK, msgType);
    }

    [Fact]
    public async Task Ioctl_Siocgifconf_InPrivateMode_ReturnsLoAndEth0()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        _ = env.SyscallManager.GetOrCreatePrivateNetNamespace();
        env.MapUserPage(0x35000);
        env.MapUserPage(0x36000);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);

        env.WriteInt32(0x35000, 64);
        env.WriteUInt32(0x35004, 0x36000);
        Assert.Equal(0, await CallSysIoctl(env, (uint)fd, LinuxConstants.SIOCGIFCONF, 0x35000));
        Assert.Equal(64, env.ReadInt32(0x35000));

        var name0 = ReadIfreqName(env, 0x36000);
        var name1 = ReadIfreqName(env, 0x36020);
        Assert.Equal("lo", name0);
        Assert.Equal("eth0", name1);
    }

    [Fact]
    public async Task Ioctl_Siocgifflags_OnHostSocket_ReturnsLoopbackFlagForLo()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x37000);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);

        WriteIfreqName(env, 0x37000, "lo");
        Assert.Equal(0, await CallSysIoctl(env, (uint)fd, LinuxConstants.SIOCGIFFLAGS, 0x37000));

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x37010, 2));
        Assert.NotEqual(0u, flags & LinuxConstants.IFF_LOOPBACK);
    }

    [Fact]
    public async Task Ioctl_Siocgiftxqlen_ReturnsQueueLength()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        _ = env.SyscallManager.GetOrCreatePrivateNetNamespace();
        env.MapUserPage(0x3B000);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);

        WriteIfreqName(env, 0x3B000, "eth0");
        Assert.Equal(0, await CallSysIoctl(env, (uint)fd, LinuxConstants.SIOCGIFTXQLEN, 0x3B000));
        var qlen = BinaryPrimitives.ReadInt32LittleEndian(env.ReadBytes(0x3B010, 4));
        Assert.Equal(1000, qlen);
    }

    [Fact]
    public async Task Socket_HostUdp_SendTo_WithMsgNosignal_SendsPacket()
    {
        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        server.ReceiveTimeout = 2000;
        var port = ((IPEndPoint)server.LocalEndPoint!).Port;

        using var env = new TestEnv();
        env.MapUserPage(0x3C000);
        env.MapUserPage(0x3D000);
        env.WriteBytes(0x3C000, Encoding.ASCII.GetBytes("dns-probe"));
        WriteSockaddrIn(env, 0x3D000, 0x7F000001u, (ushort)port);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);

        var rc = await CallSysSendTo(env, (uint)fd, 0x3C000, 9, LinuxConstants.MSG_NOSIGNAL, 0x3D000, 16);
        Assert.Equal(9, rc);

        var receiveBuffer = new byte[64];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        var received = server.ReceiveFrom(receiveBuffer, 0, receiveBuffer.Length, SocketFlags.None, ref remote);
        Assert.Equal("dns-probe", Encoding.ASCII.GetString(receiveBuffer, 0, received));
    }

    [Fact]
    public async Task Socket_HostUdp_ReadV_ConsumesSingleDatagramPerCall()
    {
        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var serverPort = ((IPEndPoint)server.LocalEndPoint!).Port;

        using var env = new TestEnv();
        env.MapUserPage(0x3E000);
        env.MapUserPage(0x3F000);
        env.MapUserPage(0x40000);
        env.MapUserPage(0x41000);
        env.MapUserPage(0x42000);
        env.MapUserPage(0x43000);
        env.MapUserPage(0x44000);
        env.MapUserPage(0x45000);
        env.WriteInt32(0x40000, 16);

        WriteSockaddrIn(env, 0x3E000, 0x7F000001u, (ushort)serverPort);
        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);
        Assert.Equal(0, await CallSysConnect(env, (uint)fd, 0x3E000, 16));
        Assert.Equal(0, await CallSysGetSockName(env, (uint)fd, 0x3F000, 0x40000));

        var (guestIp, guestPort) = ReadSockaddrIn(env, 0x3F000);
        var guestEndpoint = new IPEndPoint(new IPAddress(BinaryPrimitives.ReverseEndianness(guestIp)), guestPort);
        _ = server.SendTo(Encoding.ASCII.GetBytes("one"), guestEndpoint);
        _ = server.SendTo(Encoding.ASCII.GetBytes("two"), guestEndpoint);

        env.WriteBytes(0x42000, [0x58, 0x58]);
        env.WriteBytes(0x43000, [0x59, 0x59]);
        env.WriteBytes(0x45000, [0x5A, 0x5A]);
        env.WriteBytes(0x45010, [0x57, 0x57]);
        WriteIovec(env, 0x41000, 0x42000, 2);
        WriteIovec(env, 0x41008, 0x43000, 2);
        WriteIovec(env, 0x44000, 0x45000, 2);
        WriteIovec(env, 0x44008, 0x45010, 2);

        var first = await CallSysReadV(env, (uint)fd, 0x41000, 2);
        Assert.Equal(3, first);
        Assert.Equal([0x6F, 0x6E], env.ReadBytes(0x42000, 2));
        Assert.Equal([0x65, 0x59], env.ReadBytes(0x43000, 2));

        var second = await CallSysReadV(env, (uint)fd, 0x44000, 2);
        Assert.Equal(3, second);
        Assert.Equal([0x74, 0x77], env.ReadBytes(0x45000, 2));
        Assert.Equal([0x6F, 0x57], env.ReadBytes(0x45010, 2));
    }

    [Fact]
    public async Task Socket_HostUdp_WriteV_SendsSingleDatagram()
    {
        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        server.ReceiveTimeout = 2000;
        var serverPort = ((IPEndPoint)server.LocalEndPoint!).Port;

        using var env = new TestEnv();
        env.MapUserPage(0x46000);
        env.MapUserPage(0x47000);
        env.MapUserPage(0x48000);
        env.MapUserPage(0x49000);
        env.WriteBytes(0x47000, Encoding.ASCII.GetBytes("alpha"));
        env.WriteBytes(0x48000, Encoding.ASCII.GetBytes("beta"));

        WriteSockaddrIn(env, 0x46000, 0x7F000001u, (ushort)serverPort);
        WriteIovec(env, 0x49000, 0x47000, 5);
        WriteIovec(env, 0x49008, 0x48000, 4);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);
        Assert.Equal(0, await CallSysConnect(env, (uint)fd, 0x46000, 16));

        var written = await CallSysWriteV(env, (uint)fd, 0x49000, 2);
        Assert.Equal(9, written);

        var receiveBuffer = new byte[64];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        var received = server.ReceiveFrom(receiveBuffer, 0, receiveBuffer.Length, SocketFlags.None, ref remote);
        Assert.Equal(9, received);
        Assert.Equal("alphabeta", Encoding.ASCII.GetString(receiveBuffer, 0, received));
    }

    [Fact]
    public async Task Socket_PrivateNetwork_Dgram_Connect_ReportsPeerAndEphemeralLocalPort()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x28000);
        env.MapUserPage(0x29000);
        env.MapUserPage(0x2A000);
        env.MapUserPage(0x2B000);
        env.WriteInt32(0x29000, 16);
        env.WriteInt32(0x2B000, 16);

        WriteSockaddrIn(env, 0x28000, 0x7F000001u, 19220);
        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);

        Assert.Equal(0, await CallSysConnect(env, (uint)fd, 0x28000, 16));

        Assert.Equal(0, await CallSysGetPeerName(env, (uint)fd, 0x2A000, 0x2B000));
        var (peerIp, peerPort) = ReadSockaddrIn(env, 0x2A000);
        Assert.Equal(0x7F000001u, peerIp);
        Assert.Equal(19220, peerPort);

        env.WriteInt32(0x29000, 16);
        Assert.Equal(0, await CallSysGetSockName(env, (uint)fd, 0x28000, 0x29000));
        var (localIp, localPort) = ReadSockaddrIn(env, 0x28000);
        Assert.Equal(0x00000000u, localIp);
        Assert.True(localPort >= 49152);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_GetSockNameAndPeerName_ReportLoopbackEndpoints()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x17000);
        env.MapUserPage(0x18000);
        env.MapUserPage(0x19000);
        env.MapUserPage(0x1A000);
        env.MapUserPage(0x1B000);
        env.MapUserPage(0x1C000);

        WriteSockaddrIn(env, 0x17000, 0x7F000001u, 19090);
        WriteSockaddrIn(env, 0x18000, 0x7F000001u, 19090);
        env.WriteInt32(0x1A000, 16);
        env.WriteInt32(0x1C000, 16);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x17000, 16));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 1));
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x18000, 16));

        var clientNameRc = await CallSysGetSockName(env, (uint)clientFd, 0x19000, 0x1A000);
        Assert.Equal(0, clientNameRc);
        Assert.Equal(16, env.ReadInt32(0x1A000));
        var (clientIp, clientPort) = ReadSockaddrIn(env, 0x19000);
        Assert.Equal(0x7F000001u, clientIp);
        Assert.True(clientPort >= 49152);

        var peerRc = await CallSysGetPeerName(env, (uint)clientFd, 0x1B000, 0x1C000);
        Assert.Equal(0, peerRc);
        Assert.Equal(16, env.ReadInt32(0x1C000));
        var (peerIp, peerPort) = ReadSockaddrIn(env, 0x1B000);
        Assert.Equal(0x7F000001u, peerIp);
        Assert.Equal(19090, peerPort);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_Accept_HalfNullPeerAddress_ReturnsEfault()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x41000);
        env.MapUserPage(0x42000);
        env.MapUserPage(0x43000);

        WriteSockaddrIn(env, 0x41000, 0x7F000001u, 19110);
        WriteSockaddrIn(env, 0x42000, 0x7F000001u, 19110);
        env.WriteInt32(0x43000, 16);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x41000, 16));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 1));
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x42000, 16));

        var rc = await CallSysAccept(env, (uint)serverFd, 0x41000, 0);

        Assert.Equal(-(int)Errno.EFAULT, rc);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_RecvFrom_HalfNullPeerAddress_ReturnsEfault()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x44000);
        env.MapUserPage(0x45000);
        env.MapUserPage(0x46000);
        env.MapUserPage(0x47000);
        env.MapUserPage(0x48000);

        WriteSockaddrIn(env, 0x44000, 0x7F000001u, 19120);
        WriteSockaddrIn(env, 0x45000, 0x7F000001u, 19120);
        env.WriteInt32(0x48000, 16);
        env.WriteBytes(0x47000, Encoding.ASCII.GetBytes("ping"));

        var receiverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        var senderFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(receiverFd >= 0 && senderFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)receiverFd, 0x44000, 16));
        Assert.Equal(4, await CallSysSendTo(env, (uint)senderFd, 0x47000, 4, 0, 0x45000, 16));

        var rc = await CallSysRecvFrom(env, (uint)receiverFd, 0x46000, 16, 0, 0x44000, 0);

        Assert.Equal(-(int)Errno.EFAULT, rc);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_RecvFrom_NullPeerAddressPair_IsAllowed()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x49000);
        env.MapUserPage(0x4A000);
        env.MapUserPage(0x4B000);
        env.MapUserPage(0x4C000);

        WriteSockaddrIn(env, 0x49000, 0x7F000001u, 19130);
        WriteSockaddrIn(env, 0x4A000, 0x7F000001u, 19130);
        env.WriteBytes(0x4C000, Encoding.ASCII.GetBytes("pong"));

        var receiverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        var senderFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(receiverFd >= 0 && senderFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)receiverFd, 0x49000, 16));
        Assert.Equal(4, await CallSysSendTo(env, (uint)senderFd, 0x4C000, 4, 0, 0x4A000, 16));

        var rc = await CallSysRecvFrom(env, (uint)receiverFd, 0x4B000, 16, 0, 0, 0);

        Assert.Equal(4, rc);
        Assert.Equal("pong", Encoding.ASCII.GetString(env.ReadBytes(0x4B000, 4)));
    }

    [Fact]
    public async Task Socket_PrivateNetwork_SendMsgRecvMsg_StreamPayload_Works()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x1D000);
        env.MapUserPage(0x1E000);
        env.MapUserPage(0x1F000);
        env.MapUserPage(0x20000);
        env.MapUserPage(0x21000);
        env.MapUserPage(0x22000);
        env.MapUserPage(0x23000);
        env.MapUserPage(0x24000);
        env.MapUserPage(0x25000);

        WriteSockaddrIn(env, 0x1D000, 0x7F000001u, 19100);
        WriteSockaddrIn(env, 0x1E000, 0x7F000001u, 19100);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x1D000, 16));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 1));
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x1E000, 16));

        var acceptedFd = await CallSysAccept(env, (uint)serverFd, 0, 0);
        Assert.True(acceptedFd >= 0);

        var payload = "netmsg";
        env.WriteBytes(0x22000, Encoding.ASCII.GetBytes(payload));
        WriteIovec(env, 0x20000, 0x22000, payload.Length);
        WriteMsgHdr(env, 0x1F000, 0x20000, 1, 0, 0, 0, 0);

        Assert.Equal(payload.Length, await CallSysSendMsg(env, (uint)clientFd, 0x1F000, 0));

        WriteIovec(env, 0x24000, 0x25000, 16);
        WriteMsgHdr(env, 0x23000, 0x24000, 1, 0, 0, 0, 0);
        var recv = await CallSysRecvMsg(env, (uint)acceptedFd, 0x23000, 0);
        Assert.Equal(payload.Length, recv);
        Assert.Equal(payload, Encoding.ASCII.GetString(env.ReadBytes(0x25000, recv)));
    }

    [Fact]
    public async Task Socket_PrivateNetwork_ShutdownWrite_MakesFurtherSendFailWithEpipe()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x30000);
        env.MapUserPage(0x31000);
        env.MapUserPage(0x32000);

        WriteSockaddrIn(env, 0x30000, 0x7F000001u, 19140);
        WriteSockaddrIn(env, 0x31000, 0x7F000001u, 19140);
        env.WriteBytes(0x32000, [1, 2, 3, 4]);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x30000, 16));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 1));
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x31000, 16));

        var acceptedFd = await CallSysAccept(env, (uint)serverFd, 0, 0);
        Assert.True(acceptedFd >= 0);

        Assert.Equal(0, await CallSysShutdown(env, (uint)clientFd, 1));
        Assert.Equal(-(int)Errno.EPIPE, await CallSysSend(env, (uint)clientFd, 0x32000u, 4u));
    }

    [Fact]
    public async Task Socket_UnixPath_BindListenConnectAccept_SendRecv_Works()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x33000);
        env.MapUserPage(0x34000);
        env.MapUserPage(0x35000);
        env.MapUserPage(0x36000);

        var path = $"/sock_unix_syscall_test_{Guid.NewGuid():N}";
        var sockaddrLen = WriteSockaddrUn(env, 0x33000, path);
        WriteSockaddrUn(env, 0x34000, path);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x33000, (uint)sockaddrLen));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 1));
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x34000, (uint)sockaddrLen));

        var acceptedFd = await CallSysAccept(env, (uint)serverFd, 0, 0);
        Assert.True(acceptedFd >= 0);

        var payload = Encoding.ASCII.GetBytes("unix");
        env.WriteBytes(0x35000, payload);
        Assert.Equal(payload.Length, await CallSysSend(env, (uint)clientFd, 0x35000, (uint)payload.Length));

        var recv = await CallSysRecvFrom(env, (uint)acceptedFd, 0x36000, 16, 0);
        Assert.Equal(payload.Length, recv);
        Assert.Equal("unix", Encoding.ASCII.GetString(env.ReadBytes(0x36000, recv)));
    }

    [Fact]
    public async Task Socket_UnixSocketPair_CopyToUserFault_RollsBackDescriptors()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocketPair(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0, 0xDEAD0000);
        Assert.Equal(-(int)Errno.EFAULT, rc);
        Assert.Empty(env.SyscallManager.FDs);
    }

    [Fact]
    public async Task Socket_UnixPath_UnlinkThenRebind_WhileOriginalOpen_Works()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x47000);
        env.MapUserPage(0x48000);

        var path = $"/sock_unix_rebind_test_{Guid.NewGuid():N}";
        var sockaddrLen = WriteSockaddrUn(env, 0x47000, path);
        env.WriteBytes(0x48000, Encoding.UTF8.GetBytes(path + '\0'));

        var firstFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(firstFd >= 0);
        Assert.Equal(0, await CallSysBind(env, (uint)firstFd, 0x47000, (uint)sockaddrLen));
        Assert.True(env.SyscallManager.PathWalk(path).IsValid);

        Assert.Equal(0, await CallSysUnlink(env, 0x48000));
        Assert.False(env.SyscallManager.PathWalk(path).IsValid);

        var secondFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(secondFd >= 0);
        Assert.Equal(0, await CallSysBind(env, (uint)secondFd, 0x47000, (uint)sockaddrLen));
        Assert.True(env.SyscallManager.PathWalk(path).IsValid);
    }

    [Fact]
    public async Task Socket_UnixPath_Rename_KeepsEndpointReachableViaNewPath()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x4D000);
        env.MapUserPage(0x4E000);
        env.MapUserPage(0x4F000);
        env.MapUserPage(0x50000);

        var oldPath = $"/sock_unix_rename_old_{Guid.NewGuid():N}";
        var newPath = $"/sock_unix_rename_new_{Guid.NewGuid():N}";
        var oldLen = WriteSockaddrUn(env, 0x4D000, oldPath);
        var newLen = WriteSockaddrUn(env, 0x4E000, newPath);
        WriteCString(env, 0x4F000, oldPath);
        WriteCString(env, 0x50000, newPath);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x4D000, (uint)oldLen));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 1));
        Assert.Equal(0, await CallSysRename(env, 0x4F000, 0x50000));
        Assert.False(env.SyscallManager.PathWalk(oldPath).IsValid);
        Assert.True(env.SyscallManager.PathWalk(newPath).IsValid);

        WriteSockaddrUn(env, 0x4E000, newPath);
        var raw = env.ReadBytes(0x4E000, newLen);
        var nul = Array.IndexOf(raw, (byte)0, 2);
        Assert.True(nul > 2);
        Assert.Equal(newPath, Encoding.UTF8.GetString(raw, 2, nul - 2));
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x4E000, (uint)newLen));
    }

    [Fact]
    public async Task Socket_UnixPath_LinkAlias_IsReachableViaAliasPath()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x51000);
        env.MapUserPage(0x52000);
        env.MapUserPage(0x53000);
        env.MapUserPage(0x54000);

        var basePath = $"/sock_unix_link_base_{Guid.NewGuid():N}";
        var aliasPath = $"/sock_unix_link_alias_{Guid.NewGuid():N}";
        var baseLen = WriteSockaddrUn(env, 0x51000, basePath);
        var aliasLen = WriteSockaddrUn(env, 0x52000, aliasPath);
        WriteCString(env, 0x53000, basePath);
        WriteCString(env, 0x54000, aliasPath);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x51000, (uint)baseLen));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 1));
        Assert.Equal(0, await CallSysLink(env, 0x53000, 0x54000));
        Assert.True(env.SyscallManager.PathWalk(aliasPath).IsValid);

        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x52000, (uint)aliasLen));
    }

    [Fact]
    public async Task Socket_UnixPath_CloseBoundSocket_RemovesActiveEndpointButKeepsNode()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x55000);
        env.MapUserPage(0x56000);
        env.MapUserPage(0x57000);

        var path = $"/sock_unix_close_bound_{Guid.NewGuid():N}";
        var sockaddrLen = WriteSockaddrUn(env, 0x55000, path);
        WriteCString(env, 0x56000, path);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x55000, (uint)sockaddrLen));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 1));
        Assert.Equal(0, await CallSysClose(env, (uint)serverFd));
        Assert.True(env.SyscallManager.PathWalk(path).IsValid);

        var rc = await CallSysConnect(env, (uint)clientFd, 0x55000, (uint)sockaddrLen);
        Assert.Equal(-(int)Errno.ECONNREFUSED, rc);
    }

    [Fact]
    public async Task Socket_UnixRecvMsg_ScmRights_MsgCmsgCloexec_DeliversCloexecFd()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x37000);
        env.MapUserPage(0x38000);
        env.MapUserPage(0x39000);
        env.MapUserPage(0x3A000);
        env.MapUserPage(0x3B000);
        env.MapUserPage(0x3C000);
        env.MapUserPage(0x3D000);
        env.MapUserPage(0x3E000);

        var spRc = await CallSysSocketPair(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_DGRAM, 0, 0x37000);
        Assert.Equal(0, spRc);
        var sockA = env.ReadInt32(0x37000);
        var sockB = env.ReadInt32(0x37004);
        Assert.True(sockA >= 0 && sockB >= 0);

        var passFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(passFd >= 0);

        env.WriteBytes(0x39000, [0x2A]);
        WriteIovec(env, 0x38000, 0x39000, 1);
        var sendControlLen = WriteScmRightsCmsg(env, 0x3A000, [passFd]);
        WriteMsgHdr(env, 0x3B000, 0x38000, 1, 0x3A000, sendControlLen, 0, 0);
        Assert.Equal(1, await CallSysSendMsg(env, (uint)sockA, 0x3B000, 0));

        WriteIovec(env, 0x3C000, 0x3D000, 8);
        WriteMsgHdr(env, 0x3E000, 0x3C000, 1, 0x3A000, 64, 0, 0);
        var recv = await CallSysRecvMsg(env, (uint)sockB, 0x3E000, LinuxConstants.MSG_CMSG_CLOEXEC);
        Assert.Equal(1, recv);
        Assert.Equal(16, env.ReadInt32(0x3E000 + 20));
        Assert.Equal(0, env.ReadInt32(0x3E000 + 24));

        var receivedFd = env.ReadInt32(0x3A000 + 12);
        Assert.True(receivedFd >= 0);
        Assert.True(env.SyscallManager.IsFdCloseOnExec(receivedFd));
    }

    [Fact]
    public async Task Socket_UnixRecvMsg_ScmRights_ControlTooSmall_SetsCtrunc()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x3F000);
        env.MapUserPage(0x40000);
        env.MapUserPage(0x41000);
        env.MapUserPage(0x42000);
        env.MapUserPage(0x43000);
        env.MapUserPage(0x44000);
        env.MapUserPage(0x45000);
        env.MapUserPage(0x46000);

        var spRc = await CallSysSocketPair(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_DGRAM, 0, 0x3F000);
        Assert.Equal(0, spRc);
        var sockA = env.ReadInt32(0x3F000);
        var sockB = env.ReadInt32(0x3F004);
        Assert.True(sockA >= 0 && sockB >= 0);

        var passFd1 = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_DGRAM, 0);
        var passFd2 = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(passFd1 >= 0 && passFd2 >= 0);

        env.WriteBytes(0x41000, [0x55]);
        WriteIovec(env, 0x40000, 0x41000, 1);
        var sendControlLen = WriteScmRightsCmsg(env, 0x42000, [passFd1, passFd2]);
        WriteMsgHdr(env, 0x43000, 0x40000, 1, 0x42000, sendControlLen, 0, 0);
        Assert.Equal(1, await CallSysSendMsg(env, (uint)sockA, 0x43000, 0));

        var fdCountBeforeRecv = env.SyscallManager.FDs.Count;
        WriteIovec(env, 0x44000, 0x45000, 8);
        WriteMsgHdr(env, 0x46000, 0x44000, 1, 0x42000, 16, 0, 0); // only room for 1 fd
        var recv = await CallSysRecvMsg(env, (uint)sockB, 0x46000, 0);
        Assert.Equal(1, recv);
        Assert.Equal(16, env.ReadInt32(0x46000 + 20));
        Assert.Equal(LinuxConstants.MSG_CTRUNC, env.ReadInt32(0x46000 + 24) & LinuxConstants.MSG_CTRUNC);
        Assert.Equal(fdCountBeforeRecv + 1, env.SyscallManager.FDs.Count);
        Assert.True(env.ReadInt32(0x42000 + 12) >= 0);
    }

    [Fact]
    public async Task Socket_UnixRecvFrom_MsgDontwait_ReturnsEagainWhenNoData()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x49000);
        env.MapUserPage(0x4A000);

        var spRc = await CallSysSocketPair(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_DGRAM, 0, 0x49000);
        Assert.Equal(0, spRc);
        var receiverFd = env.ReadInt32(0x49004);
        Assert.True(receiverFd >= 0);

        var rc = await CallSysRecvFrom(env, (uint)receiverFd, 0x4A000, 8, LinuxConstants.MSG_DONTWAIT);
        Assert.Equal(-(int)Errno.EAGAIN, rc);
    }

    [Fact]
    public async Task Socket_UnixSend_MsgDontwait_ReturnsEagainWhenPeerBufferIsFull()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x4B000);
        env.MapUserPage(0x4C000);
        env.WriteBytes(0x4C000, new byte[4096]);

        var spRc = await CallSysSocketPair(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_DGRAM, 0, 0x4B000);
        Assert.Equal(0, spRc);
        var senderFd = env.ReadInt32(0x4B000);
        Assert.True(senderFd >= 0);

        var sawEagain = false;
        for (var i = 0; i < 256; i++)
        {
            var rc = await CallSysSend(env, (uint)senderFd, 0x4C000, 4096, LinuxConstants.MSG_DONTWAIT);
            if (rc == -(int)Errno.EAGAIN)
            {
                sawEagain = true;
                break;
            }

            Assert.Equal(4096, rc);
        }

        Assert.True(sawEagain);
    }

    private static void WriteSockaddrIn(TestEnv env, uint addr, uint ipv4Be, ushort port)
    {
        Span<byte> buf = stackalloc byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(buf[..2], LinuxConstants.AF_INET);
        BinaryPrimitives.WriteUInt16BigEndian(buf[2..4], port);
        BinaryPrimitives.WriteUInt32BigEndian(buf[4..8], ipv4Be);
        Assert.True(env.Engine.CopyToUser(addr, buf));
    }

    private static int WriteSockaddrUn(TestEnv env, uint addr, string path)
    {
        var pathBytes = Encoding.UTF8.GetBytes(path);
        var len = 2 + pathBytes.Length + 1;
        var buf = new byte[len];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), LinuxConstants.AF_UNIX);
        pathBytes.CopyTo(buf.AsSpan(2, pathBytes.Length));
        buf[^1] = 0;
        Assert.True(env.Engine.CopyToUser(addr, buf));
        return len;
    }

    private static void WriteCString(TestEnv env, uint addr, string value)
    {
        env.WriteBytes(addr, Encoding.UTF8.GetBytes(value + '\0'));
    }

    private static void WriteIovec(TestEnv env, uint addr, uint baseAddr, int len)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buf[..4], baseAddr);
        BinaryPrimitives.WriteInt32LittleEndian(buf[4..8], len);
        Assert.True(env.Engine.CopyToUser(addr, buf));
    }

    private static int WriteScmRightsCmsg(TestEnv env, uint controlPtr, IReadOnlyList<int> fds)
    {
        var cmsgLen = 12 + fds.Count * 4;
        var buf = new byte[cmsgLen];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), cmsgLen);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), 1); // SOL_SOCKET
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), 1); // SCM_RIGHTS
        for (var i = 0; i < fds.Count; i++)
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12 + i * 4, 4), fds[i]);
        env.WriteBytes(controlPtr, buf);
        return cmsgLen;
    }

    private static void WriteMsgHdr(TestEnv env, uint addr, uint iovPtr, int iovLen, uint controlPtr, int controlLen,
        uint namePtr, int nameLen)
    {
        Span<byte> buf = stackalloc byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(buf[..4], namePtr);
        BinaryPrimitives.WriteInt32LittleEndian(buf[4..8], nameLen);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[8..12], iovPtr);
        BinaryPrimitives.WriteInt32LittleEndian(buf[12..16], iovLen);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[16..20], controlPtr);
        BinaryPrimitives.WriteInt32LittleEndian(buf[20..24], controlLen);
        BinaryPrimitives.WriteInt32LittleEndian(buf[24..28], 0);
        Assert.True(env.Engine.CopyToUser(addr, buf));
    }

    private static void WriteNlMsg(TestEnv env, uint ptr, ushort type, ushort flags, uint seq)
    {
        Span<byte> buf = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(buf[..4], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[4..6], type);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[6..8], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[8..12], seq);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[12..16], 0);
        Assert.True(env.Engine.CopyToUser(ptr, buf));
    }

    private static void WriteIfreqName(TestEnv env, uint ptr, string name)
    {
        var buf = new byte[LinuxConstants.IFNAMSIZ];
        var bytes = Encoding.ASCII.GetBytes(name);
        Array.Copy(bytes, 0, buf, 0, Math.Min(bytes.Length, LinuxConstants.IFNAMSIZ - 1));
        env.WriteBytes(ptr, buf);
    }

    private static string ReadIfreqName(TestEnv env, uint ptr)
    {
        var raw = env.ReadBytes(ptr, LinuxConstants.IFNAMSIZ);
        var nul = Array.IndexOf(raw, (byte)0);
        if (nul < 0) nul = raw.Length;
        return Encoding.ASCII.GetString(raw, 0, nul);
    }

    private static (uint IpBe, ushort Port) ReadSockaddrIn(TestEnv env, uint addr)
    {
        Span<byte> buf = stackalloc byte[16];
        Assert.True(env.Engine.CopyFromUser(addr, buf));
        var family = BinaryPrimitives.ReadUInt16LittleEndian(buf[..2]);
        Assert.Equal((ushort)LinuxConstants.AF_INET, family);
        var port = BinaryPrimitives.ReadUInt16BigEndian(buf[2..4]);
        var ip = BinaryPrimitives.ReadUInt32BigEndian(buf[4..8]);
        return (ip, port);
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly TestRuntimeFactory _runtime = new();

        public TestEnv()
        {
            Engine = _runtime.CreateEngine();
            Vma = _runtime.CreateAddressSpace();
            Process = new Process(100, Vma, null!);
            Scheduler = new KernelScheduler();
            Task = new FiberTask(100, Process, Engine, Scheduler);
            Engine.Owner = Task;
            Task.Status = FiberTaskStatus.Waiting;

            SyscallManager = new SyscallManager(Engine, Vma, 0);
            SyscallManager.MountRootHostfs(".");
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public Process Process { get; }
        public FiberTask Task { get; }
        public KernelScheduler Scheduler { get; }
        public SyscallManager SyscallManager { get; }

        public void Dispose()
        {
            GC.KeepAlive(Task);
        }

        public ValueTask<int> Invoke(string methodName, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
        {
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var tcs = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async void Entry()
            {
                var previous = Engine.CurrentSyscallManager;
                Engine.CurrentSyscallManager = SyscallManager;
                try
                {
                    var pending = (ValueTask<int>)method!.Invoke(SyscallManager, [Engine, a1, a2, a3, a4, a5, a6])!;
                    var rc = await pending;
                    tcs.TrySetResult(rc);
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    tcs.TrySetException(ex.InnerException);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    Engine.CurrentSyscallManager = previous;
                    Scheduler.Running = false;
                    Scheduler.WakeUp();
                }
            }

            Task.Continuation = Entry;
            Scheduler.Running = true;
            Scheduler.Schedule(Task);
            Scheduler.Run();
            return new ValueTask<int>(tcs.Task);
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void WriteInt32(uint addr, int value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(buf, value);
            Assert.True(Engine.CopyToUser(addr, buf));
        }

        public void WriteUInt32(uint addr, uint value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
            Assert.True(Engine.CopyToUser(addr, buf));
        }

        public int ReadInt32(uint addr)
        {
            Span<byte> buf = stackalloc byte[4];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return BinaryPrimitives.ReadInt32LittleEndian(buf);
        }

        public void WriteBytes(uint addr, byte[] data)
        {
            Assert.True(Engine.CopyToUser(addr, data));
        }

        public byte[] ReadBytes(uint addr, int len)
        {
            var data = new byte[len];
            Assert.True(Engine.CopyFromUser(addr, data));
            return data;
        }
    }
}
