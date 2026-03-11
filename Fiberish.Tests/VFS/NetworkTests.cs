using System.Buffers.Binary;
using System.Net.Sockets;
using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class NetworkTests
{
    [Fact]
    public void EpollInode_ShouldRegisterAndTriggerEvents()
    {
        using var env = new TestEnv();
        var epoll = new EpollInode(1, env.MemfdSuperBlock);

        // Dummy eventfd as the target
        var eventFd = new EventFdInode(2, env.MemfdSuperBlock, 0, FileFlags.O_RDWR);
        var fileDentry = new Dentry("eventfd", eventFd, null, env.MemfdSuperBlock);
        var file = new LinuxFile(fileDentry, FileFlags.O_RDWR | FileFlags.O_NONBLOCK, null!);

        // Register file into epoll via EPOLL_CTL_ADD
        var events = (uint)LinuxConstants.POLLIN;
        ulong data = 42;
        var res = epoll.Ctl(LinuxConstants.EPOLL_CTL_ADD, 8, file, events, data);
        Assert.Equal(0, res);

        // Trigger eventfd
        var writeBuf = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(writeBuf, 1);
        eventFd.Write(file, writeBuf, 0); // This signals POLLIN

        // Read Epoll
        var buffer = new byte[16];
        var readEvents = 0;

        // Since eventfd is already signaled, wait should complete immediately
        var awaiter = epoll.WaitAsync(buffer, 1, 0).GetAwaiter();
        awaiter.OnCompleted(() => { readEvents = awaiter.GetResult(); });

        Assert.Equal(1, readEvents);

        var eventsRead = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, 4));
        var dataRead = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(4, 8));

        Assert.Equal((uint)LinuxConstants.POLLIN, eventsRead);
        Assert.Equal(42ul, dataRead);
    }

    [Fact]
    public async Task UnixSocketInode_ShouldPassDataAndFds()
    {
        using var env = new TestEnv();
        var sock1 = new UnixSocketInode(1, env.MemfdSuperBlock, SocketType.Dgram);
        var sock2 = new UnixSocketInode(2, env.MemfdSuperBlock, SocketType.Dgram);
        sock1.ConnectPair(sock2);
        sock2.ConnectPair(sock1);

        var file1 = new LinuxFile(new Dentry("s1", sock1, null, env.MemfdSuperBlock), FileFlags.O_RDWR,
            null!);
        var file2 = new LinuxFile(new Dentry("s2", sock2, null, env.MemfdSuperBlock), FileFlags.O_RDWR,
            null!);

        // Dummy FD to pass
        var dummyFdNode = new EventFdInode(3, env.MemfdSuperBlock, 0, FileFlags.O_RDWR);
        var dummyFile = new LinuxFile(new Dentry("d", dummyFdNode, null, env.MemfdSuperBlock),
            FileFlags.O_RDWR, null!);

        var fdsToPass = new List<LinuxFile> { dummyFile };
        byte[] payload = { 1, 2, 3, 4 };

        var sent = await sock1.SendMessageAsync(file1, payload, fdsToPass, 0);
        Assert.Equal(4, sent);

        var recvBuf = new byte[10];
        // The receive is async, but data is already there so it completes synchronously essentially
        var recvRes = await sock2.RecvMessageAsync(file2, recvBuf, 0);
        Assert.Equal(4, recvRes.BytesRead);
        Assert.NotNull(recvRes.Fds);
        Assert.Single(recvRes.Fds);
        Assert.Equal(dummyFile, recvRes.Fds[0]);
    }

    [Fact]
    public async Task UnixSocketInode_RecvMessageAsync_ShouldResetReadWaitQueueWhenDrained()
    {
        using var env = new TestEnv();
        var sock1 = new UnixSocketInode(1, env.MemfdSuperBlock, SocketType.Dgram);
        var sock2 = new UnixSocketInode(2, env.MemfdSuperBlock, SocketType.Dgram);
        sock1.ConnectPair(sock2);
        sock2.ConnectPair(sock1);

        var file1 = new LinuxFile(new Dentry("s1", sock1, null, env.MemfdSuperBlock), FileFlags.O_RDWR,
            null!);
        var file2 = new LinuxFile(new Dentry("s2", sock2, null, env.MemfdSuperBlock), FileFlags.O_RDWR,
            null!);

        var readWaitField =
            typeof(UnixSocketInode).GetField("_readWaitQueue", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(readWaitField);
        var readWaitQueue = Assert.IsType<AsyncWaitQueue>(readWaitField!.GetValue(sock2));

        var sent = await sock1.SendMessageAsync(file1, [0x2A], null, 0);
        Assert.Equal(1, sent);
        Assert.True(readWaitQueue.IsSignaled);

        var recv = await sock2.RecvMessageAsync(file2, new byte[8], 0);
        Assert.Equal(1, recv.BytesRead);

        // After draining the only queued packet, future waits must block until new data arrives.
        Assert.False(readWaitQueue.IsSignaled);
    }

    [Fact]
    public void UnixSocketInode_Poll_FreshSocket_DoesNotReportHangup()
    {
        using var env = new TestEnv();
        var sock = new UnixSocketInode(1, env.MemfdSuperBlock, SocketType.Stream);
        var file = new LinuxFile(new Dentry("s", sock, null, env.MemfdSuperBlock), FileFlags.O_RDWR,
            null!);

        var revents = sock.Poll(file, PollEvents.POLLIN | PollEvents.POLLOUT | PollEvents.POLLHUP);
        Assert.Equal(0, revents & PollEvents.POLLHUP);
    }

    private class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Scheduler = new KernelScheduler();
            KernelScheduler.Current = Scheduler;

            var fs = new Tmpfs();
            MemfdSuperBlock = fs.ReadSuper(new FileSystemType { Name = "tmpfs" }, 0, "", null);

            var vma = new VMAManager();
            var engine = new Engine();
            Process = new Process(100, vma, null!);
            Task = new FiberTask(100, Process, engine, Scheduler);

            typeof(KernelScheduler).GetProperty("CurrentTask")!.SetValue(Scheduler, Task);
        }

        public KernelScheduler Scheduler { get; }
        public Process Process { get; }
        public FiberTask Task { get; }
        public SuperBlock MemfdSuperBlock { get; }

        public void Dispose()
        {
            KernelScheduler.Current = null;
        }
    }
}