using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Fiberish.Core;
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

    private static Task<int> StartInvoke(TestEnv env, string methodName, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        return Task.Run(async () => await env.Invoke(methodName, a1, a2, a3, a4, a5, a6));
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

        var pending = StartInvoke(env, "SysPpoll", pollfdPtr, 1, tsPtr, 0, 0, 0);
        Assert.False(pending.IsCompleted);

        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, 1);
        Assert.Equal(8, eventFd.Write(file, payload, 0));
        env.DrainEvents();

        var rc = await pending.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, rc);

        var pfd = env.ReadStruct<PollFd>(pollfdPtr);
        Assert.Equal(LinuxConstants.POLLIN, pfd.Revents);
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
        Assert.Equal(8, eventFd.Write(file, payload, 0));

        var rc = await Invoke(env, "SysEpollPwait2", (uint)epfd, eventsPtr, 1, 0, 0, 0);
        Assert.Equal(1, rc);
        Assert.Equal(LinuxConstants.EPOLLIN,
            BinaryPrimitives.ReadUInt32LittleEndian(env.Read(eventsPtr, 12).AsSpan(0, 4)));
        Assert.Equal(0x1122334455667788UL,
            BinaryPrimitives.ReadUInt64LittleEndian(env.Read(eventsPtr, 12).AsSpan(4, 8)));
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

        var pending = StartInvoke(env, "SysPpoll", pollfdPtr, 1, tsPtr, 0, 0, 0);
        Assert.False(pending.IsCompleted);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listenEp);

        await DrainUntilCompleted(env, pending);
        var rc = await pending;
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

        var pending = StartInvoke(env, "SysPpoll", pollfdPtr, 1, tsPtr, 0, 0, 0);
        Assert.False(pending.IsCompleted);

        var payload = new byte[] { 0x41 };
        _ = client.Send(payload);

        await DrainUntilCompleted(env, pending);
        var rc = await pending;
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

        var pending = StartInvoke(env, "SysPpoll", pollfdPtr, 1, tsPtr, 0, 0, 0);

        await DrainUntilCompleted(env, pending);
        var rc = await pending;
        Assert.Equal(1, rc);

        var pfd = env.ReadStruct<PollFd>(pollfdPtr);
        Assert.True((pfd.Revents & LinuxConstants.POLLOUT) != 0);
        Assert.True((pfd.Revents & PollEvents.POLLERR) == 0);
    }

    private static void WriteTimespecSec(TestEnv env, uint tsPtr, int seconds)
    {
        var ts = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(0, 4), seconds);
        BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(4, 4), 0);
        env.Write(tsPtr, ts);
    }

    private static async Task DrainUntilCompleted(TestEnv env, Task task, int maxIterations = 200)
    {
        for (var i = 0; i < maxIterations && !task.IsCompleted; i++)
        {
            env.DrainEvents();
            await Task.Delay(5);
        }
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
        private static readonly MethodInfo DrainEventsMethod =
            typeof(KernelScheduler).GetMethod("DrainEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;
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
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            async void Entry()
            {
                try
                {
                    var pending = (ValueTask<int>)method!.Invoke(null, [Engine.State, a1, a2, a3, a4, a5, a6])!;
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
            OwnerThreadIdField.SetValue(Scheduler, 0);
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

        public void DrainEvents()
        {
            _ = (bool)DrainEventsMethod.Invoke(Scheduler, null)!;
        }
    }
}
