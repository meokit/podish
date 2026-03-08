using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using Fiberish.Core;
using Fiberish.Diagnostics;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class HostSocketReadinessTests
{
    private const int TestTimeoutMs = 1000;

    private static readonly MethodInfo DrainEventsMethod =
        typeof(KernelScheduler).GetMethod("DrainEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact(Timeout = TestTimeoutMs)]
    public void Poll_ConnectedReadableSocket_ReturnsPollIn()
    {
        using var env = new ReadinessEnv();
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var ep = (IPEndPoint)listener.LocalEndPoint!;

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(ep);
        using var server = listener.Accept();
        server.Blocking = false;

        var inode = new HostSocketInode(3001, env.SyscallManager.MemfdSuperBlock, server);
        var file = new LinuxFile(new Dentry("readiness-readable", inode, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR | FileFlags.O_NONBLOCK, env.SyscallManager.AnonMount);
        using var readiness = new HostSocketReadiness(inode, inode.NativeSocket,
            Logging.CreateLogger<HostSocketReadinessTests>());

        _ = client.Send([0x41]);
        short revents = 0;
        for (var i = 0; i < 100; i++)
        {
            revents = readiness.Poll(file, PollEvents.POLLIN);
            if ((revents & PollEvents.POLLIN) != 0)
                break;
            Thread.Sleep(2);
        }

        Assert.True((revents & PollEvents.POLLIN) != 0);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task WaitForSocketEventAsync_ConnectedReadable_CompletesTrue()
    {
        using var env = new ReadinessEnv();
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var ep = (IPEndPoint)listener.LocalEndPoint!;

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(ep);
        using var server = listener.Accept();
        server.Blocking = false;

        var inode = new HostSocketInode(3002, env.SyscallManager.MemfdSuperBlock, server);
        var file = new LinuxFile(new Dentry("readiness-wait", inode, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        using var readiness = new HostSocketReadiness(inode, inode.NativeSocket,
            Logging.CreateLogger<HostSocketReadinessTests>());

        _ = client.Send([0x42]);
        var completed = await readiness.WaitForSocketEventAsync(file, PollEvents.POLLIN);
        Assert.True(completed);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RegisterWaitHandle_ListenSocket_QueuesAcceptedConnection()
    {
        using var env = new ReadinessEnv();
        var inode = new HostSocketInode(3003, env.SyscallManager.MemfdSuperBlock, AddressFamily.InterNetwork,
            SocketType.Stream, ProtocolType.Tcp);
        inode.NativeSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        inode.NativeSocket.Listen(16);
        var ep = (IPEndPoint)inode.NativeSocket.LocalEndPoint!;

        var file = new LinuxFile(new Dentry("readiness-listen", inode, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        using var readiness = new HostSocketReadiness(inode, inode.NativeSocket,
            Logging.CreateLogger<HostSocketReadinessTests>());

        var fired = 0;
        using var reg = readiness.RegisterWaitHandle(file, () => fired++, PollEvents.POLLIN);
        Assert.NotNull(reg);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(ep);

        await DrainUntil(() => fired > 0 || readiness.HasBufferedAcceptedSocket(), env);

        Assert.True(readiness.HasBufferedAcceptedSocket());
        Assert.True(readiness.TryDequeueAcceptedSocket(out var accepted));
        accepted.Dispose();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public void Poll_NonBlockingConnectPending_DoesNotReturnPollErr()
    {
        using var env = new ReadinessEnv();
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var ep = (IPEndPoint)listener.LocalEndPoint!;

        var inode = new HostSocketInode(3004, env.SyscallManager.MemfdSuperBlock, AddressFamily.InterNetwork,
            SocketType.Stream, ProtocolType.Tcp);
        var file = new LinuxFile(new Dentry("readiness-connect", inode, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR | FileFlags.O_NONBLOCK, env.SyscallManager.AnonMount);
        using var readiness = new HostSocketReadiness(inode, inode.NativeSocket,
            Logging.CreateLogger<HostSocketReadinessTests>());

        try
        {
            inode.NativeSocket.Connect(ep);
        }
        catch (SocketException ex)
        {
            Assert.Contains(ex.SocketErrorCode, [SocketError.WouldBlock, SocketError.IOPending, SocketError.InProgress, SocketError.AlreadyInProgress]);
        }

        var revents = readiness.Poll(file, PollEvents.POLLOUT);
        Assert.True((revents & PollEvents.POLLERR) == 0);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RegisterWaitHandle_DisposedBeforeEvent_DoesNotInvokeCallback()
    {
        using var env = new ReadinessEnv();
        var inode = new HostSocketInode(3005, env.SyscallManager.MemfdSuperBlock, AddressFamily.InterNetwork,
            SocketType.Stream, ProtocolType.Tcp);
        inode.NativeSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        inode.NativeSocket.Listen(8);
        var ep = (IPEndPoint)inode.NativeSocket.LocalEndPoint!;

        var file = new LinuxFile(new Dentry("readiness-dispose", inode, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        using var readiness = new HostSocketReadiness(inode, inode.NativeSocket,
            Logging.CreateLogger<HostSocketReadinessTests>());

        var fired = 0;
        var reg = readiness.RegisterWaitHandle(file, () => fired++, PollEvents.POLLIN);
        Assert.NotNull(reg);
        reg.Dispose();

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(ep);
        await DrainUntil(() => readiness.HasBufferedAcceptedSocket(), env);

        Assert.Equal(0, fired);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RegisterWaitHandle_TwoRegistrations_ArmedAndReceiveEvent()
    {
        using var env = new ReadinessEnv();
        var inode = new HostSocketInode(3006, env.SyscallManager.MemfdSuperBlock, AddressFamily.InterNetwork,
            SocketType.Stream, ProtocolType.Tcp);
        inode.NativeSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        inode.NativeSocket.Listen(8);
        var ep = (IPEndPoint)inode.NativeSocket.LocalEndPoint!;

        var file = new LinuxFile(new Dentry("readiness-double", inode, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        using var readiness = new HostSocketReadiness(inode, inode.NativeSocket,
            Logging.CreateLogger<HostSocketReadinessTests>());

        using var reg1 = readiness.RegisterWaitHandle(file, () => { }, PollEvents.POLLIN);
        using var reg2 = readiness.RegisterWaitHandle(file, () => { }, PollEvents.POLLIN);
        Assert.NotNull(reg1);
        Assert.NotNull(reg2);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(ep);
        await DrainUntil(() => readiness.HasBufferedAcceptedSocket(), env);
        Assert.True(readiness.TryDequeueAcceptedSocket(out var accepted));
        accepted.Dispose();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RegisterWaitHandle_ConnectedRead_NoData_DoesNotSpuriouslyFire()
    {
        using var env = new ReadinessEnv();
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var ep = (IPEndPoint)listener.LocalEndPoint!;

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(ep);
        using var server = listener.Accept();
        server.Blocking = false;

        var inode = new HostSocketInode(3007, env.SyscallManager.MemfdSuperBlock, server);
        var file = new LinuxFile(new Dentry("readiness-no-spurious", inode, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        using var readiness = new HostSocketReadiness(inode, inode.NativeSocket,
            Logging.CreateLogger<HostSocketReadinessTests>());

        var fired = 0;
        using var reg = readiness.RegisterWaitHandle(file, () => Interlocked.Increment(ref fired), PollEvents.POLLIN);
        Assert.NotNull(reg);

        for (var i = 0; i < 20; i++)
        {
            env.DrainEvents();
            await Task.Delay(5);
        }

        Assert.Equal(0, Volatile.Read(ref fired));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RegisterWaitHandle_ConnectedRead_DataArrival_FiresPromptly()
    {
        using var env = new ReadinessEnv();
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var ep = (IPEndPoint)listener.LocalEndPoint!;

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(ep);
        using var server = listener.Accept();
        server.Blocking = false;

        var inode = new HostSocketInode(3011, env.SyscallManager.MemfdSuperBlock, server);
        var file = new LinuxFile(new Dentry("readiness-data-arrival", inode, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        using var readiness = new HostSocketReadiness(inode, inode.NativeSocket,
            Logging.CreateLogger<HostSocketReadinessTests>());

        var fired = 0;
        using var reg = readiness.RegisterWaitHandle(file, () => Interlocked.Increment(ref fired), PollEvents.POLLIN);
        Assert.NotNull(reg);

        _ = client.Send([0x33]);
        await DrainUntil(() => Volatile.Read(ref fired) > 0, env, 300);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ConnectAsync_NonBlockingToClosedPort_ReturnsInProgressOrConnectionRefused()
    {
        using var env = new ReadinessEnv();
        int closedPort;
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            closedPort = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }

        var inode = new HostSocketInode(3008, env.SyscallManager.MemfdSuperBlock, AddressFamily.InterNetwork,
            SocketType.Stream, ProtocolType.Tcp);
        var file = new LinuxFile(new Dentry("host-connect-refused", inode, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR | FileFlags.O_NONBLOCK, env.SyscallManager.AnonMount);

        var rc = await inode.ConnectAsync(file, new IPEndPoint(IPAddress.Loopback, closedPort));
        Assert.Contains(rc, [-(int)Errno.EINPROGRESS, -(int)Errno.ECONNREFUSED]);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RegisterWaitHandle_NonBlockingConnectPending_PollOut_IsArmedOrImmediatelyReady()
    {
        using var env = new ReadinessEnv();
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var ep = (IPEndPoint)listener.LocalEndPoint!;

        var inode = new HostSocketInode(3009, env.SyscallManager.MemfdSuperBlock, AddressFamily.InterNetwork,
            SocketType.Stream, ProtocolType.Tcp);
        var file = new LinuxFile(new Dentry("readiness-connect-arm", inode, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR | FileFlags.O_NONBLOCK, env.SyscallManager.AnonMount);
        using var readiness = new HostSocketReadiness(inode, inode.NativeSocket,
            Logging.CreateLogger<HostSocketReadinessTests>());

        try
        {
            inode.NativeSocket.Connect(ep);
        }
        catch (SocketException ex)
        {
            Assert.Contains(ex.SocketErrorCode,
                [SocketError.WouldBlock, SocketError.IOPending, SocketError.InProgress, SocketError.AlreadyInProgress]);
        }

        using var reg = readiness.RegisterWaitHandle(file, static () => { }, PollEvents.POLLOUT);
        if (reg == null)
        {
            await DrainUntil(() =>
            {
                var revents = readiness.Poll(file, PollEvents.POLLOUT);
                return (revents & PollEvents.POLLOUT) != 0 || (revents & PollEvents.POLLERR) != 0;
            }, env, 300);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RegisterWaitHandle_NonBlockingConnectPending_PollOut_CallbackFiresOnCompletion()
    {
        using var env = new ReadinessEnv();
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var ep = (IPEndPoint)listener.LocalEndPoint!;

        var inode = new HostSocketInode(3010, env.SyscallManager.MemfdSuperBlock, AddressFamily.InterNetwork,
            SocketType.Stream, ProtocolType.Tcp);
        var file = new LinuxFile(new Dentry("readiness-connect-callback", inode, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR | FileFlags.O_NONBLOCK, env.SyscallManager.AnonMount);
        using var readiness = new HostSocketReadiness(inode, inode.NativeSocket,
            Logging.CreateLogger<HostSocketReadinessTests>());

        try
        {
            inode.NativeSocket.Connect(ep);
        }
        catch (SocketException ex)
        {
            Assert.Contains(ex.SocketErrorCode,
                [SocketError.WouldBlock, SocketError.IOPending, SocketError.InProgress, SocketError.AlreadyInProgress]);
        }

        var fired = 0;
        using var accepted = listener.Accept();
        using var reg = readiness.RegisterWaitHandle(file, () => Interlocked.Increment(ref fired), PollEvents.POLLOUT);
        if (reg != null)
        {
            await DrainUntil(() => Volatile.Read(ref fired) > 0, env, 300);
            return;
        }

        await DrainUntil(() =>
        {
            var revents = readiness.Poll(file, PollEvents.POLLOUT);
            return (revents & PollEvents.POLLOUT) != 0 || (revents & PollEvents.POLLERR) != 0;
        }, env, 300);
    }

    private static async Task DrainUntil(Func<bool> done, ReadinessEnv env, int timeoutMs = TestTimeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs)
        {
            env.DrainEvents();
            await Task.Delay(5);
        }

        Assert.True(done(), "timed out waiting for condition");
    }

    private sealed class ReadinessEnv : IDisposable
    {
        public ReadinessEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
            Process = new Process(200, Vma, null!);
            Scheduler = new KernelScheduler();
            Task = new FiberTask(200, Process, Engine, Scheduler);
            Engine.Owner = Task;
            KernelScheduler.Current = Scheduler;
            Scheduler.CurrentTask = Task;

            SyscallManager = new SyscallManager(Engine, Vma, 0);
            SyscallManager.MountRootHostfs(".");
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public Process Process { get; }
        public FiberTask Task { get; }
        public KernelScheduler Scheduler { get; }
        public SyscallManager SyscallManager { get; }

        public void DrainEvents()
        {
            DrainEventsMethod.Invoke(Scheduler, null);
        }

        public void Dispose()
        {
            KernelScheduler.Current = null;
            GC.KeepAlive(Task);
        }
    }
}
