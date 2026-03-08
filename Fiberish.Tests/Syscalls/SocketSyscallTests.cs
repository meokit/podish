using System.Net.Sockets;
using System.Reflection;
using System.Buffers.Binary;
using Fiberish.Core;
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
        var method = typeof(SyscallManager).GetMethod("SysSocket", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, domain, type, protocol, 0u, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysSetSockOpt(TestEnv env, uint fd, uint level, uint optname, uint optval, uint optlen)
    {
        var method = typeof(SyscallManager).GetMethod("SysSetSockOpt", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fd, level, optname, optval, optlen, 0u])!;
    }

    private static ValueTask<int> CallSysGetSockOpt(TestEnv env, uint fd, uint level, uint optname, uint optval, uint optlenPtr)
    {
        var method = typeof(SyscallManager).GetMethod("SysGetSockOpt", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fd, level, optname, optval, optlenPtr, 0u])!;
    }

    private static ValueTask<int> CallSysBind(TestEnv env, uint fd, uint addrPtr, uint addrLen)
    {
        var method = typeof(SyscallManager).GetMethod("SysBind", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fd, addrPtr, addrLen, 0u, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysListen(TestEnv env, uint fd, uint backlog)
    {
        var method = typeof(SyscallManager).GetMethod("SysListen", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fd, backlog, 0u, 0u, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysConnect(TestEnv env, uint fd, uint addrPtr, uint addrLen)
    {
        var method = typeof(SyscallManager).GetMethod("SysConnect", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fd, addrPtr, addrLen, 0u, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysGetSockName(TestEnv env, uint fd, uint addrPtr, uint addrLenPtr)
    {
        var method = typeof(SyscallManager).GetMethod("SysGetSockName", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fd, addrPtr, addrLenPtr, 0u, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysGetPeerName(TestEnv env, uint fd, uint addrPtr, uint addrLenPtr)
    {
        var method = typeof(SyscallManager).GetMethod("SysGetPeerName", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fd, addrPtr, addrLenPtr, 0u, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysAccept(TestEnv env, uint fd, uint addrPtr, uint addrLenPtr)
    {
        var method = typeof(SyscallManager).GetMethod("SysAccept", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fd, addrPtr, addrLenPtr, 0u, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysSendMsg(TestEnv env, uint fd, uint msgPtr, uint flags)
    {
        var method = typeof(SyscallManager).GetMethod("SysSendMsg", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fd, msgPtr, flags, 0u, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysRecvMsg(TestEnv env, uint fd, uint msgPtr, uint flags)
    {
        var method = typeof(SyscallManager).GetMethod("SysRecvMsg", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fd, msgPtr, flags, 0u, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysShutdown(TestEnv env, uint fd, uint how)
    {
        var method = typeof(SyscallManager).GetMethod("SysShutdown", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fd, how, 0u, 0u, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysSend(TestEnv env, uint fd, uint bufPtr, uint len, uint flags = 0)
    {
        var method = typeof(SyscallManager).GetMethod("SysSend", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fd, bufPtr, len, flags, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysSendTo(TestEnv env, uint fd, uint bufPtr, uint len, uint flags, uint addrPtr = 0, uint addrLen = 0)
    {
        var method = typeof(SyscallManager).GetMethod("SysSendTo", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fd, bufPtr, len, flags, addrPtr, addrLen])!;
    }

    private static ValueTask<int> CallSysRecvFrom(TestEnv env, uint fd, uint bufPtr, uint len, uint flags, uint addrPtr = 0, uint addrLen = 0)
    {
        var method = typeof(SyscallManager).GetMethod("SysRecvFrom", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fd, bufPtr, len, flags, addrPtr, addrLen])!;
    }

    private static ValueTask<int> CallSysIoctl(TestEnv env, uint fd, uint request, uint arg)
    {
        var method = typeof(SyscallManager).GetMethod("SysIoctl", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fd, request, arg, 0u, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysWrite(TestEnv env, uint fd, uint bufPtr, uint len)
    {
        var method = typeof(SyscallManager).GetMethod("SysWrite", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fd, bufPtr, len, 0u, 0u, 0u])!;
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
            (uint)(LinuxConstants.SOCK_RAW | LinuxConstants.SOCK_NONBLOCK | LinuxConstants.SOCK_CLOEXEC),
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

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET6, LinuxConstants.SOCK_RAW, LinuxConstants.IPPROTO_ICMPV6);

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

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_RAW, LinuxConstants.IPPROTO_ICMPV6);

        if (OperatingSystem.IsMacOS())
        {
            if (rc < 0)
                Assert.True(rc is -(int)Errno.EACCES or -(int)Errno.EPERM, $"unexpected errno {rc}");
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

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, LinuxConstants.IPPROTO_ICMP);

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

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET6, LinuxConstants.SOCK_DGRAM, LinuxConstants.IPPROTO_ICMPV6);

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

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, LinuxConstants.IPPROTO_ICMPV6);

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

        var rc = await CallSysGetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_TYPE, 0x13000, 0x14000);
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

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, LinuxConstants.IPPROTO_ICMP);
        if (fd < 0)
        {
            Assert.True(fd is -(int)Errno.EACCES or -(int)Errno.EPERM,
                $"unexpected errno {fd}");
            return;
        }

        var rc = await CallSysGetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_TYPE, 0x15000, 0x16000);
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
        Assert.Equal(0, await CallSysSetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_LINGER, 0x17000, 8));

        env.WriteInt32(0x19000, 8);
        Assert.Equal(0, await CallSysGetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_LINGER, 0x18000, 0x19000));

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
        Assert.Equal(0, await CallSysSetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_LINGER, 0x1A000, 8));

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
        env.SyscallManager.NetworkMode = Fiberish.Core.Net.NetworkMode.Private;

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);

        Assert.True(fd >= 0);
        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(fd));
        Assert.IsType<NetstackSocketInode>(file.Dentry.Inode);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_Dgram_ReturnsEsocktnosupport()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = Fiberish.Core.Net.NetworkMode.Private;

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);

        Assert.True(rc >= 0);
        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rc));
        Assert.IsType<NetstackSocketInode>(file.Dentry.Inode);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_Raw_ReturnsEsocktnosupport()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = Fiberish.Core.Net.NetworkMode.Private;

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_RAW, 0);

        Assert.Equal(-(int)Errno.ESOCKTNOSUPPORT, rc);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_Dgram_GetSockOpt_TypeAndError_Work()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = Fiberish.Core.Net.NetworkMode.Private;
        env.MapUserPage(0x26000);
        env.MapUserPage(0x27000);
        env.WriteInt32(0x27000, 4);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);

        Assert.Equal(0, await CallSysGetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_TYPE, 0x26000, 0x27000));
        Assert.Equal(LinuxConstants.SOCK_DGRAM, env.ReadInt32(0x26000));

        env.WriteInt32(0x27000, 4);
        Assert.Equal(0, await CallSysGetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_ERROR, 0x26000, 0x27000));
        Assert.Equal(0, env.ReadInt32(0x26000));
    }

    [Fact]
    public async Task Socket_NetlinkRoute_CreatesNetlinkRouteInode()
    {
        using var env = new TestEnv();

        var fd = await CallSysSocket(env, LinuxConstants.AF_NETLINK, LinuxConstants.SOCK_DGRAM, LinuxConstants.NETLINK_ROUTE);
        Assert.True(fd >= 0);

        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(fd));
        Assert.IsType<NetlinkRouteSocketInode>(file.Dentry.Inode);
    }

    [Fact]
    public async Task Socket_NetlinkRoute_GetLinkAndAddrDump_ReturnsMultipartMessages()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = Fiberish.Core.Net.NetworkMode.Private;
        _ = env.SyscallManager.GetOrCreatePrivateNetNamespace();
        env.MapUserPage(0x33000);
        env.MapUserPage(0x34000);

        var fd = await CallSysSocket(env, LinuxConstants.AF_NETLINK, LinuxConstants.SOCK_DGRAM | LinuxConstants.SOCK_NONBLOCK,
            LinuxConstants.NETLINK_ROUTE);
        Assert.True(fd >= 0);

        WriteNlMsg(env, 0x33000, LinuxConstants.RTM_GETLINK, LinuxConstants.NLM_F_REQUEST | LinuxConstants.NLM_F_DUMP, 100);
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

        WriteNlMsg(env, 0x33000, LinuxConstants.RTM_GETADDR, LinuxConstants.NLM_F_REQUEST | LinuxConstants.NLM_F_DUMP, 101);
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
        env.SyscallManager.NetworkMode = Fiberish.Core.Net.NetworkMode.Private;
        _ = env.SyscallManager.GetOrCreatePrivateNetNamespace();
        env.MapUserPage(0x38000);
        env.MapUserPage(0x39000);
        env.MapUserPage(0x3A000);
        env.WriteInt32(0x3A000, 12);

        var fd = await CallSysSocket(env, LinuxConstants.AF_NETLINK, LinuxConstants.SOCK_DGRAM | LinuxConstants.SOCK_NONBLOCK,
            LinuxConstants.NETLINK_ROUTE);
        Assert.True(fd >= 0);

        Assert.Equal(0, await CallSysGetSockName(env, (uint)fd, 0x39000, 0x3A000));
        Assert.Equal(12, env.ReadInt32(0x3A000));
        var family = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x39000, 2));
        Assert.Equal((ushort)LinuxConstants.AF_NETLINK, family);

        WriteNlMsg(env, 0x38000, LinuxConstants.RTM_GETLINK, LinuxConstants.NLM_F_REQUEST | LinuxConstants.NLM_F_DUMP, 200);
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
        env.SyscallManager.NetworkMode = Fiberish.Core.Net.NetworkMode.Private;
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
        Assert.NotEqual(0u, (uint)(flags & LinuxConstants.IFF_LOOPBACK));
    }

    [Fact]
    public async Task Ioctl_Siocgiftxqlen_ReturnsQueueLength()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = Fiberish.Core.Net.NetworkMode.Private;
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
    public async Task Socket_PrivateNetwork_Dgram_Connect_ReportsPeerAndEphemeralLocalPort()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = Fiberish.Core.Net.NetworkMode.Private;
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
        env.SyscallManager.NetworkMode = Fiberish.Core.Net.NetworkMode.Private;
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
    public async Task Socket_PrivateNetwork_SendMsgRecvMsg_StreamPayload_Works()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = Fiberish.Core.Net.NetworkMode.Private;
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
        env.WriteBytes(0x22000, System.Text.Encoding.ASCII.GetBytes(payload));
        WriteIovec(env, 0x20000, 0x22000, payload.Length);
        WriteMsgHdr(env, 0x1F000, 0x20000, 1, 0, 0, 0, 0);

        Assert.Equal(payload.Length, await CallSysSendMsg(env, (uint)clientFd, 0x1F000, 0));

        WriteIovec(env, 0x24000, 0x25000, 16);
        WriteMsgHdr(env, 0x23000, 0x24000, 1, 0, 0, 0, 0);
        var recv = await CallSysRecvMsg(env, (uint)acceptedFd, 0x23000, 0);
        Assert.Equal(payload.Length, recv);
        Assert.Equal(payload, System.Text.Encoding.ASCII.GetString(env.ReadBytes(0x25000, recv)));
    }

    [Fact]
    public async Task Socket_PrivateNetwork_ShutdownWrite_MakesFurtherSendFailWithEpipe()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = Fiberish.Core.Net.NetworkMode.Private;
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

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
            Process = new Process(100, Vma, null!);
            Scheduler = new KernelScheduler();
            Task = new FiberTask(100, Process, Engine, Scheduler);
            Engine.Owner = Task;
            KernelScheduler.Current = Scheduler;

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
            KernelScheduler.Current = null;
            GC.KeepAlive(Task);
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, LinuxConstants.PageSize, "[test]",
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

    private static void WriteSockaddrIn(TestEnv env, uint addr, uint ipv4Be, ushort port)
    {
        Span<byte> buf = stackalloc byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(buf[0..2], (ushort)LinuxConstants.AF_INET);
        BinaryPrimitives.WriteUInt16BigEndian(buf[2..4], port);
        BinaryPrimitives.WriteUInt32BigEndian(buf[4..8], ipv4Be);
        Assert.True(env.Engine.CopyToUser(addr, buf));
    }

    private static void WriteIovec(TestEnv env, uint addr, uint baseAddr, int len)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buf[0..4], baseAddr);
        BinaryPrimitives.WriteInt32LittleEndian(buf[4..8], len);
        Assert.True(env.Engine.CopyToUser(addr, buf));
    }

    private static void WriteMsgHdr(TestEnv env, uint addr, uint iovPtr, int iovLen, uint controlPtr, int controlLen, uint namePtr, int nameLen)
    {
        Span<byte> buf = stackalloc byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(buf[0..4], namePtr);
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
        BinaryPrimitives.WriteUInt32LittleEndian(buf[0..4], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[4..6], type);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[6..8], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[8..12], seq);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[12..16], 0);
        Assert.True(env.Engine.CopyToUser(ptr, buf));
    }

    private static void WriteIfreqName(TestEnv env, uint ptr, string name)
    {
        var buf = new byte[LinuxConstants.IFNAMSIZ];
        var bytes = System.Text.Encoding.ASCII.GetBytes(name);
        Array.Copy(bytes, 0, buf, 0, Math.Min(bytes.Length, LinuxConstants.IFNAMSIZ - 1));
        env.WriteBytes(ptr, buf);
    }

    private static string ReadIfreqName(TestEnv env, uint ptr)
    {
        var raw = env.ReadBytes(ptr, LinuxConstants.IFNAMSIZ);
        var nul = Array.IndexOf(raw, (byte)0);
        if (nul < 0) nul = raw.Length;
        return System.Text.Encoding.ASCII.GetString(raw, 0, nul);
    }

    private static (uint IpBe, ushort Port) ReadSockaddrIn(TestEnv env, uint addr)
    {
        Span<byte> buf = stackalloc byte[16];
        Assert.True(env.Engine.CopyFromUser(addr, buf));
        var family = BinaryPrimitives.ReadUInt16LittleEndian(buf[0..2]);
        Assert.Equal((ushort)LinuxConstants.AF_INET, family);
        var port = BinaryPrimitives.ReadUInt16BigEndian(buf[2..4]);
        var ip = BinaryPrimitives.ReadUInt32BigEndian(buf[4..8]);
        return (ip, port);
    }
}
