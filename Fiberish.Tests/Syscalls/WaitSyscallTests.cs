using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Fiberish.Core;
using Fiberish.Core.Net;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class WaitSyscallTests
{
    private static ValueTask<int> Invoke(TestEnv env, string methodName, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        return env.Invoke(methodName, a1, a2, a3, a4, a5, a6);
    }

    [Fact]
    public async Task Pselect6_ZeroTimeout_NoFds_ReturnsZero()
    {
        using var env = new TestEnv();
        const uint tsPtr = 0x10000;
        env.MapUserPage(tsPtr);
        env.Write(tsPtr, new byte[8]); // timespec{0,0}

        var rc = await Invoke(env, "SysPselect6", 0, 0, 0, 0, tsPtr, 0);
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task Pselect6_InvalidSigsetSize_ReturnsEinval()
    {
        using var env = new TestEnv();
        const uint tsPtr = 0x11000;
        const uint sigArgPtr = 0x12000;
        const uint sigMaskPtr = 0x13000;
        env.MapUserPage(tsPtr);
        env.MapUserPage(sigArgPtr);
        env.MapUserPage(sigMaskPtr);
        env.Write(tsPtr, new byte[8]); // zero timeout
        env.Write(sigMaskPtr, new byte[8]);

        var sigArg = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(sigArg.AsSpan(0, 4), sigMaskPtr);
        BinaryPrimitives.WriteUInt32LittleEndian(sigArg.AsSpan(4, 4), 4); // invalid, expected 8
        env.Write(sigArgPtr, sigArg);

        var rc = await Invoke(env, "SysPselect6", 0, 0, 0, 0, tsPtr, sigArgPtr);
        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task Ppoll_InvalidSigsetSize_ReturnsEinval()
    {
        using var env = new TestEnv();
        const uint sigMaskPtr = 0x14000;
        env.MapUserPage(sigMaskPtr);
        env.Write(sigMaskPtr, new byte[8]);

        var rc = await Invoke(env, "SysPpoll", 0, 0, 0, sigMaskPtr, 4, 0);
        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task PpollTime64_InvalidNsec_ReturnsEinval()
    {
        using var env = new TestEnv();
        const uint ts64Ptr = 0x15000;
        env.MapUserPage(ts64Ptr);
        var ts = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(ts.AsSpan(0, 8), 0);
        BinaryPrimitives.WriteInt64LittleEndian(ts.AsSpan(8, 8), 1_000_000_000); // invalid nsec
        env.Write(ts64Ptr, ts);

        var rc = await Invoke(env, "SysPpollTime64", 0, 0, ts64Ptr, 0, 0, 0);
        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task EpollPwait2_ZeroTimeout_ReturnsZero()
    {
        using var env = new TestEnv();
        const uint eventsPtr = 0x16000;
        const uint ts64Ptr = 0x17000;
        env.MapUserPage(eventsPtr);
        env.MapUserPage(ts64Ptr);
        env.Write(ts64Ptr, new byte[16]); // timespec64 {0,0}

        var epfd = await Invoke(env, "SysEpollCreate1", 0, 0, 0, 0, 0, 0);
        Assert.True(epfd >= 0);

        var rc = await Invoke(env, "SysEpollPwait2", (uint)epfd, eventsPtr, 1, ts64Ptr, 0, 0);
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task Ppoll_EventFdWake_CompletesAndSetsRevents()
    {
        using var env = new TestEnv();
        const uint pollfdPtr = 0x18000;
        const uint tsPtr = 0x19000;
        env.MapUserPage(pollfdPtr);
        env.MapUserPage(tsPtr);

        var eventFd = new EventFdInode(10, env.SyscallManager.MemfdSuperBlock, 0, FileFlags.O_RDWR);
        var file = new LinuxFile(new Dentry("eventfd", eventFd, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        var fd = env.SyscallManager.AllocFD(file);

        env.WriteStruct(pollfdPtr, new PollFd
        {
            Fd = fd,
            Events = LinuxConstants.POLLIN,
            Revents = 0
        });

        var ts = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(4, 4), 0);
        env.Write(tsPtr, ts);

        var pending = env.StartOnScheduler(() => env.Invoke("SysPpoll", pollfdPtr, 1, tsPtr, 0, 0, 0));
        Assert.False(pending.IsCompleted);

        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, 1);
        await env.WaitForBackgroundSchedulerAsync();
        await env.InvokeOnSchedulerAsync(() => Assert.Equal(8, eventFd.WriteFromHost(null, file, payload, 0)));

        var rc = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, rc);

        var pfd = env.ReadStruct<PollFd>(pollfdPtr);
        Assert.Equal(LinuxConstants.POLLIN, pfd.Revents);
    }

    [Fact]
    public async Task Ppoll_ControllingTtyWithoutBackingTty_ReportsHupAndErrImmediately()
    {
        using var env = new TestEnv();
        const uint pollfdPtr = 0x181000;
        env.MapUserPage(pollfdPtr);

        var inode = new ControllingTtyInode(env.SyscallManager.MemfdSuperBlock);
        var file = new LinuxFile(new Dentry("tty", inode, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        var fd = env.SyscallManager.AllocFD(file);

        env.WriteStruct(pollfdPtr, new PollFd { Fd = fd, Events = LinuxConstants.POLLIN, Revents = 0 });

        var rc = await env.Invoke("SysPpoll", pollfdPtr, 1, 0, 0, 0, 0);
        Assert.Equal(1, rc);

        var pfd = env.ReadStruct<PollFd>(pollfdPtr);
        Assert.Equal((short)(PollEvents.POLLHUP | PollEvents.POLLERR), pfd.Revents);
    }

    [Fact]
    public async Task EpollPwait2_ReadyEvent_ReturnsOneAndWritesEvent()
    {
        using var env = new TestEnv();
        const uint eventsPtr = 0x1A000;
        const uint epollEventPtr = 0x1B000;
        env.MapUserPage(eventsPtr);
        env.MapUserPage(epollEventPtr);

        var epfd = await Invoke(env, "SysEpollCreate1", 0, 0, 0, 0, 0, 0);
        Assert.True(epfd >= 0);

        var eventFd = new EventFdInode(11, env.SyscallManager.MemfdSuperBlock, 0, FileFlags.O_RDWR);
        var file = new LinuxFile(new Dentry("eventfd", eventFd, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        var fd = env.SyscallManager.AllocFD(file);

        var epollEvent = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(epollEvent.AsSpan(0, 4), LinuxConstants.EPOLLIN);
        BinaryPrimitives.WriteUInt64LittleEndian(epollEvent.AsSpan(4, 8), 0x1122334455667788UL);
        env.Write(epollEventPtr, epollEvent);

        var ctl = await Invoke(env, "SysEpollCtl", (uint)epfd, LinuxConstants.EPOLL_CTL_ADD, (uint)fd, epollEventPtr, 0,
            0);
        Assert.Equal(0, ctl);

        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, 1);
        Assert.Equal(8, eventFd.WriteFromHost(null, file, payload, 0));

        var rc = await Invoke(env, "SysEpollPwait2", (uint)epfd, eventsPtr, 1, 0, 0, 0);
        Assert.Equal(1, rc);
        Assert.Equal(LinuxConstants.EPOLLIN,
            BinaryPrimitives.ReadUInt32LittleEndian(env.Read(eventsPtr, 12).AsSpan(0, 4)));
        Assert.Equal(0x1122334455667788UL,
            BinaryPrimitives.ReadUInt64LittleEndian(env.Read(eventsPtr, 12).AsSpan(4, 8)));
    }

    [Fact]
    public async Task Pause_DefaultIgnoredSignal_MustNotReturnUntilInterruptingSignalArrives()
    {
        using var env = new TestEnv();

        var pending = env.StartOnScheduler(() => env.Invoke("SysPause", 0, 0, 0, 0, 0, 0));
        Assert.False(pending.IsCompleted);

        await env.WaitForBackgroundSchedulerAsync();
        await env.PostSignalAsync((int)Signal.SIGWINCH);
        Assert.False(pending.IsCompleted);

        await env.PostSignalAsync((int)Signal.SIGUSR1);

        var rc = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(-(int)Errno.ERESTARTSYS, rc);
    }

    [Fact]
    public async Task Ppoll_DefaultIgnoredSignal_MustNotInterruptWait()
    {
        using var env = new TestEnv();
        const uint pollfdPtr = 0x23000;
        const uint tsPtr = 0x24000;
        env.MapUserPage(pollfdPtr);
        env.MapUserPage(tsPtr);

        var eventFd = new EventFdInode(12, env.SyscallManager.MemfdSuperBlock, 0, FileFlags.O_RDWR);
        var file = new LinuxFile(new Dentry("eventfd", eventFd, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        var fd = env.SyscallManager.AllocFD(file);

        env.WriteStruct(pollfdPtr, new PollFd { Fd = fd, Events = LinuxConstants.POLLIN, Revents = 0 });
        WriteTimespecSec(env, tsPtr, 1);

        var pending = env.StartOnScheduler(() => env.Invoke("SysPpoll", pollfdPtr, 1, tsPtr, 0, 0, 0));
        Assert.False(pending.IsCompleted);

        await env.WaitForBackgroundSchedulerAsync();
        await env.PostSignalAsync((int)Signal.SIGWINCH);
        Assert.False(pending.IsCompleted);

        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, 1);
        await env.InvokeOnSchedulerAsync(() => Assert.Equal(8, eventFd.WriteFromHost(null, file, payload, 0)));

        var rc = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task Ppoll_InterruptingSignal_MustReturnErestartsys()
    {
        using var env = new TestEnv();
        const uint pollfdPtr = 0x25000;
        const uint tsPtr = 0x26000;
        env.MapUserPage(pollfdPtr);
        env.MapUserPage(tsPtr);

        var eventFd = new EventFdInode(13, env.SyscallManager.MemfdSuperBlock, 0, FileFlags.O_RDWR);
        var file = new LinuxFile(new Dentry("eventfd", eventFd, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        var fd = env.SyscallManager.AllocFD(file);

        env.WriteStruct(pollfdPtr, new PollFd { Fd = fd, Events = LinuxConstants.POLLIN, Revents = 0 });
        WriteTimespecSec(env, tsPtr, 1);

        var pending = env.StartOnScheduler(() => env.Invoke("SysPpoll", pollfdPtr, 1, tsPtr, 0, 0, 0));
        Assert.False(pending.IsCompleted);

        await env.WaitForBackgroundSchedulerAsync();
        await env.PostSignalAsync((int)Signal.SIGUSR1);

        var rc = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(-(int)Errno.ERESTARTSYS, rc);
    }

    [Fact]
    public async Task EpollPwait2_DefaultIgnoredSignal_MustNotInterruptWait()
    {
        using var env = new TestEnv();
        const uint eventsPtr = 0x27000;
        const uint epollEventPtr = 0x28000;
        const uint ts64Ptr = 0x29000;
        env.MapUserPage(eventsPtr);
        env.MapUserPage(epollEventPtr);
        env.MapUserPage(ts64Ptr);

        var epfd = await Invoke(env, "SysEpollCreate1", 0, 0, 0, 0, 0, 0);
        Assert.True(epfd >= 0);

        var eventFd = new EventFdInode(14, env.SyscallManager.MemfdSuperBlock, 0, FileFlags.O_RDWR);
        var file = new LinuxFile(new Dentry("eventfd", eventFd, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        var fd = env.SyscallManager.AllocFD(file);

        var epollEvent = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(epollEvent.AsSpan(0, 4), LinuxConstants.EPOLLIN);
        BinaryPrimitives.WriteUInt64LittleEndian(epollEvent.AsSpan(4, 8), 0x9988776655443322UL);
        env.Write(epollEventPtr, epollEvent);

        Assert.Equal(0, await Invoke(env, "SysEpollCtl", (uint)epfd, LinuxConstants.EPOLL_CTL_ADD, (uint)fd,
            epollEventPtr, 0, 0));

        var ts = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(ts.AsSpan(0, 8), 1);
        BinaryPrimitives.WriteInt64LittleEndian(ts.AsSpan(8, 8), 0);
        env.Write(ts64Ptr, ts);

        var pending = env.StartOnScheduler(() => env.Invoke("SysEpollPwait2", (uint)epfd, eventsPtr, 1, ts64Ptr, 0, 0));
        Assert.False(pending.IsCompleted);

        await env.WaitForBackgroundSchedulerAsync();
        await env.PostSignalAsync((int)Signal.SIGWINCH);
        Assert.False(pending.IsCompleted);

        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, 1);
        await env.InvokeOnSchedulerAsync(() => Assert.Equal(8, eventFd.WriteFromHost(null, file, payload, 0)));

        var rc = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task Ppoll_HostListeningSocket_WakesOnIncomingConnection()
    {
        using var env = new TestEnv();
        const uint pollfdPtr = 0x1C000;
        const uint tsPtr = 0x1D000;
        env.MapUserPage(pollfdPtr);
        env.MapUserPage(tsPtr);

        var inode = new HostSocketInode(200, env.SyscallManager.MemfdSuperBlock, AddressFamily.InterNetwork,
            SocketType.Stream, ProtocolType.Tcp);
        inode.NativeSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        inode.NativeSocket.Listen(16);
        var listenEp = (IPEndPoint)inode.NativeSocket.LocalEndPoint!;

        var file = new LinuxFile(new Dentry("host-listen", inode, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        var fd = env.SyscallManager.AllocFD(file);

        env.WriteStruct(pollfdPtr, new PollFd { Fd = fd, Events = LinuxConstants.POLLIN, Revents = 0 });
        WriteTimespecSec(env, tsPtr, 1);

        var pending = env.StartOnScheduler(() => env.Invoke("SysPpoll", pollfdPtr, 1, tsPtr, 0, 0, 0));
        Assert.False(pending.IsCompleted);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listenEp);

        var rc = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, rc);
        var pfd = env.ReadStruct<PollFd>(pollfdPtr);
        Assert.True((pfd.Revents & LinuxConstants.POLLIN) != 0);
    }

    [Fact]
    public async Task Ppoll_HostConnectedSocket_WakesOnReadableData()
    {
        using var env = new TestEnv();
        const uint pollfdPtr = 0x1E000;
        const uint tsPtr = 0x1F000;
        env.MapUserPage(pollfdPtr);
        env.MapUserPage(tsPtr);

        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var ep = (IPEndPoint)listener.LocalEndPoint!;

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(ep);
        using var server = listener.Accept();
        server.Blocking = false;

        var inode = new HostSocketInode(201, env.SyscallManager.MemfdSuperBlock, server);
        var file = new LinuxFile(new Dentry("host-connected", inode, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        var fd = env.SyscallManager.AllocFD(file);

        env.WriteStruct(pollfdPtr, new PollFd { Fd = fd, Events = LinuxConstants.POLLIN, Revents = 0 });
        WriteTimespecSec(env, tsPtr, 1);

        var pending = env.StartOnScheduler(() => env.Invoke("SysPpoll", pollfdPtr, 1, tsPtr, 0, 0, 0));
        Assert.False(pending.IsCompleted);

        var payload = new byte[] { 0x41 };
        _ = client.Send(payload);

        var rc = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, rc);
        var pfd = env.ReadStruct<PollFd>(pollfdPtr);
        Assert.True((pfd.Revents & LinuxConstants.POLLIN) != 0);
    }

    [Fact]
    public async Task Ppoll_HostNonBlockingConnect_WakesWithPollOutWithoutPollErr()
    {
        using var env = new TestEnv();
        const uint pollfdPtr = 0x21000;
        const uint tsPtr = 0x22000;
        env.MapUserPage(pollfdPtr);
        env.MapUserPage(tsPtr);

        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(16);
        var ep = (IPEndPoint)listener.LocalEndPoint!;

        var inode = new HostSocketInode(202, env.SyscallManager.MemfdSuperBlock, AddressFamily.InterNetwork,
            SocketType.Stream, ProtocolType.Tcp);
        var file = new LinuxFile(new Dentry("host-connect", inode, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR | FileFlags.O_NONBLOCK, env.SyscallManager.AnonMount);
        var fd = env.SyscallManager.AllocFD(file);

        try
        {
            inode.NativeSocket.Connect(ep);
        }
        catch (SocketException ex)
        {
            Assert.Contains(ex.SocketErrorCode,
                [SocketError.WouldBlock, SocketError.IOPending, SocketError.InProgress, SocketError.AlreadyInProgress]);
        }

        env.WriteStruct(pollfdPtr, new PollFd { Fd = fd, Events = LinuxConstants.POLLOUT, Revents = 0 });
        WriteTimespecSec(env, tsPtr, 1);

        var pending = env.StartOnScheduler(() => env.Invoke("SysPpoll", pollfdPtr, 1, tsPtr, 0, 0, 0));

        var rc = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, rc);

        var pfd = env.ReadStruct<PollFd>(pollfdPtr);
        Assert.True((pfd.Revents & LinuxConstants.POLLOUT) != 0);
        Assert.True((pfd.Revents & PollEvents.POLLERR) == 0);
    }

    private static void WriteSockaddrIn(TestEnv env, uint addr, uint ipv4Be, ushort port)
    {
        Span<byte> buf = stackalloc byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(buf[..2], LinuxConstants.AF_INET);
        BinaryPrimitives.WriteUInt16BigEndian(buf[2..4], port);
        BinaryPrimitives.WriteUInt32BigEndian(buf[4..8], ipv4Be);
        env.Write(addr, buf);
    }

    [Fact]
    public async Task Socket_Host_RecvTimeout_Interrupted_ReturnsEintr()
    {
        using var env = new TestEnv();
        const uint optvalPtr = 0x30000;
        const uint bufPtr = 0x31000;
        env.MapUserPage(optvalPtr);
        env.MapUserPage(bufPtr);

        // Create Host socket
        var fd = await env.Invoke("SysSocket", LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0, 0, 0, 0);
        Assert.True(fd >= 0);

        // Set SO_RCVTIMEO
        var ts = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(0, 4), 10); // 10 seconds
        BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(4, 4), 0);
        env.Write(optvalPtr, ts);
        Assert.Equal(0,
            await env.Invoke("SysSetSockOpt", (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_RCVTIMEO,
                optvalPtr, 8, 0));

        // Bind to any port so ReceiveFrom doesn't throw InvalidOperationException
        const uint bindAddrPtr = 0x34000;
        env.MapUserPage(bindAddrPtr);
        WriteSockaddrIn(env, bindAddrPtr, 0, 0); // INADDR_ANY:0
        Assert.Equal(0, await env.Invoke("SysBind", (uint)fd, bindAddrPtr, 16, 0, 0, 0));

        var pending = env.StartOnScheduler(() => env.Invoke("SysRecvFrom", (uint)fd, bufPtr, 64, 0, 0, 0));
        Assert.False(pending.IsCompleted);

        await env.WaitForBackgroundSchedulerAsync();
        await env.PostSignalAsync((int)Signal.SIGUSR1);

        var rc = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(-(int)Errno.EINTR, rc);
    }

    [Fact]
    public async Task Socket_Netstack_RecvTimeout_Interrupted_ReturnsEintr()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        const uint optvalPtr = 0x32000;
        const uint bufPtr = 0x33000;
        env.MapUserPage(optvalPtr);
        env.MapUserPage(bufPtr);

        // Create Netstack socket
        var fd = await env.Invoke("SysSocket", LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0, 0, 0, 0);
        Assert.True(fd >= 0);

        // Set SO_RCVTIMEO
        var ts = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(0, 4), 10); // 10 seconds
        BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(4, 4), 0);
        env.Write(optvalPtr, ts);
        Assert.Equal(0,
            await env.Invoke("SysSetSockOpt", (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_RCVTIMEO,
                optvalPtr, 8, 0));

        // Bind to any port
        const uint bindAddrPtr = 0x35000;
        env.MapUserPage(bindAddrPtr);
        WriteSockaddrIn(env, bindAddrPtr, 0, 0);
        Assert.Equal(0, await env.Invoke("SysBind", (uint)fd, bindAddrPtr, 16, 0, 0, 0));

        var pending = env.StartOnScheduler(() => env.Invoke("SysRecvFrom", (uint)fd, bufPtr, 64, 0, 0, 0));
        Assert.False(pending.IsCompleted);

        await env.WaitForBackgroundSchedulerAsync();
        await env.PostSignalAsync((int)Signal.SIGUSR1);

        var rc = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(-(int)Errno.EINTR, rc);
    }

    [Fact]
    public void FutexWait_IsNotInNeverRestartList()
    {
        using var env = new TestEnv();
        var method = typeof(FiberTask).GetMethod("IsSyscallNeverRestart",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var restartNever = (bool)method!.Invoke(null,
            [env.Task, (uint)X86SyscallNumbers.futex, 0u, (uint)LinuxConstants.FUTEX_WAIT])!;

        Assert.False(restartNever);
    }

    [Fact]
    public async Task RecvTimeoutSocket_IsInNeverRestartList()
    {
        using var env = new TestEnv();
        const uint optvalPtr = 0x36000;
        env.MapUserPage(optvalPtr);
        env.Process.Syscalls = env.SyscallManager;

        var fd = await env.Invoke("SysSocket", LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0, 0, 0, 0);
        Assert.True(fd >= 0);

        var ts = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(0, 4), 10);
        BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(4, 4), 0);
        env.Write(optvalPtr, ts);
        Assert.Equal(0,
            await env.Invoke("SysSetSockOpt", (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_RCVTIMEO,
                optvalPtr, 8, 0));

        var method = typeof(FiberTask).GetMethod("IsSyscallNeverRestart",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var restartNever = (bool)method!.Invoke(null,
            [env.Task, (uint)X86SyscallNumbers.recvfrom, (uint)fd, 0u])!;

        Assert.True(restartNever);
    }

    [Fact]
    public void EpollWait_IsInStopSignalEintrList()
    {
        using var env = new TestEnv();
        var method = typeof(FiberTask).GetMethod("IsSyscallInterruptedByStopSignal",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var stopInterrupted = (bool)method!.Invoke(null,
            [env.Task, (uint)X86SyscallNumbers.epoll_wait, 0u])!;

        Assert.True(stopInterrupted);
    }

    private static void WriteTimespecSec(TestEnv env, uint tsPtr, int seconds)
    {
        var ts = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(0, 4), seconds);
        BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(4, 4), 0);
        env.Write(tsPtr, ts);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    private sealed class TestEnv : IDisposable
    {
        private static readonly FieldInfo OwnerThreadIdField =
            typeof(KernelScheduler).GetField("_ownerThreadId", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public TestEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
            Process = new Process(100, Vma, null!);
            Scheduler = new KernelScheduler();
            Task = new FiberTask(100, Process, Engine, Scheduler);
            Engine.Owner = Task;
            Task.Status = FiberTaskStatus.Waiting;

            SyscallManager = new SyscallManager(Engine, Vma, 0);
            Process.Syscalls = SyscallManager;
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
        }

        public ValueTask<int> Invoke(string methodName, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
        {
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            async void Entry()
            {
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
                    Scheduler.Running = false;
                    Scheduler.WakeUp();
                }
            }

            Task.Continuation = Entry;
            Scheduler.Running = true;
            Scheduler.Schedule(Task);
            Scheduler.Run();
            ResetSchedulerThreadBinding();
            return new ValueTask<int>(tcs.Task);
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void Write(uint addr, ReadOnlySpan<byte> data)
        {
            Assert.True(Engine.CopyToUser(addr, data));
        }

        public byte[] Read(uint addr, int count)
        {
            var buffer = new byte[count];
            Assert.True(Engine.CopyFromUser(addr, buffer));
            return buffer;
        }

        public void WriteStruct<T>(uint addr, T value) where T : struct
        {
            var size = Marshal.SizeOf<T>();
            var buffer = new byte[size];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), false);
            }
            finally
            {
                handle.Free();
            }

            Write(addr, buffer);
        }

        public T ReadStruct<T>(uint addr) where T : struct
        {
            var buffer = Read(addr, Marshal.SizeOf<T>());
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        public Task<T> StartOnScheduler<T>(Func<ValueTask<T>> action)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                ResetSchedulerThreadBinding();

                async void Entry()
                {
                    try
                    {
                        var result = await action();
                        tcs.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                    finally
                    {
                        Scheduler.Running = false;
                        Scheduler.WakeUp();
                    }
                }

                Task.Continuation = Entry;
                Scheduler.Running = true;
                Scheduler.Schedule(Task);
                Scheduler.Run();
                ResetSchedulerThreadBinding();
            });

            return tcs.Task;
        }

        public Task InvokeOnSchedulerAsync(Action action)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Scheduler.ScheduleFromAnyThread(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
        }

        public Task<T> InvokeOnSchedulerAsync<T>(Func<T> action)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            Scheduler.ScheduleFromAnyThread(() =>
            {
                try
                {
                    tcs.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
        }

        public Task PostSignalAsync(int signal)
        {
            return InvokeOnSchedulerAsync(() => Task.PostSignal(signal));
        }

        public async Task WaitForBackgroundSchedulerAsync(int maxIterations = 50)
        {
            for (var i = 0; i < maxIterations && Scheduler.OwnerThreadId == 0; i++)
                await System.Threading.Tasks.Task.Delay(5);

            Assert.NotEqual(0, Scheduler.OwnerThreadId);

            await InvokeOnSchedulerAsync(() => { });
        }

        private void ResetSchedulerThreadBinding()
        {
            OwnerThreadIdField.SetValue(Scheduler, 0);
        }
    }
}