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

        public int ReadInt32(uint addr)
        {
            Span<byte> buf = stackalloc byte[4];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return BinaryPrimitives.ReadInt32LittleEndian(buf);
        }
    }
}
