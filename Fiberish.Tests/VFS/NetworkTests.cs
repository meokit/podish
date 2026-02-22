using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Xunit;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;

namespace Fiberish.Tests.VFS;

public class NetworkTests
{
    private class TestEnv : IDisposable
    {
        public KernelScheduler Scheduler { get; }
        public Process Process { get; }
        public FiberTask Task { get; }
        public SuperBlock MemfdSuperBlock { get; }

        public TestEnv()
        {
            Scheduler = new KernelScheduler();
            KernelScheduler.Current = Scheduler;
            
            var fs = new Tmpfs();
            MemfdSuperBlock = fs.ReadSuper(new FileSystemType { Name = "tmpfs" }, 0, "", null);

            var vma = new Fiberish.Memory.VMAManager();
            var engine = new Engine();
            Process = new Process(100, vma, null!);
            Task = new FiberTask(100, Process, engine, Scheduler);
            
            typeof(KernelScheduler).GetProperty("CurrentTask")!.SetValue(Scheduler, Task);
        }

        public void Dispose()
        {
            KernelScheduler.Current = null;
        }
    }

    [Fact]
    public void EpollInode_ShouldRegisterAndTriggerEvents()
    {
        using var env = new TestEnv();
        var epoll = new EpollInode(1, env.MemfdSuperBlock);
        
        // Dummy eventfd as the target
        var eventFd = new EventFdInode(2, env.MemfdSuperBlock, 0, FileFlags.O_RDWR);
        var fileDentry = new Dentry("eventfd", eventFd, null, env.MemfdSuperBlock);
        var file = new Fiberish.VFS.LinuxFile(fileDentry, FileFlags.O_RDWR | FileFlags.O_NONBLOCK);
        
        // Register file into epoll via EPOLL_CTL_ADD
        uint events = (uint)LinuxConstants.POLLIN;
        ulong data = 42;
        int res = epoll.Ctl(LinuxConstants.EPOLL_CTL_ADD, 8, file, events, data);
        Assert.Equal(0, res);
        
        // Trigger eventfd
        var writeBuf = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(writeBuf, 1);
        eventFd.Write(file, writeBuf, 0); // This signals POLLIN
        
        // Read Epoll
        var buffer = new byte[16];
        int readEvents = 0;

        // Since eventfd is already signaled, wait should complete immediately
        var awaiter = epoll.WaitAsync(buffer, 1, 0);
        awaiter.OnCompleted(() => {
            readEvents = awaiter.GetResult();
        });
        
        Assert.Equal(1, readEvents);
        
        uint eventsRead = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, 4));
        ulong dataRead = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(4, 8));
        
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
        
        var file1 = new Fiberish.VFS.LinuxFile(new Dentry("s1", sock1, null, env.MemfdSuperBlock), FileFlags.O_RDWR);
        var file2 = new Fiberish.VFS.LinuxFile(new Dentry("s2", sock2, null, env.MemfdSuperBlock), FileFlags.O_RDWR);
        
        // Dummy FD to pass
        var dummyFdNode = new EventFdInode(3, env.MemfdSuperBlock, 0, FileFlags.O_RDWR);
        var dummyFile = new Fiberish.VFS.LinuxFile(new Dentry("d", dummyFdNode, null, env.MemfdSuperBlock), FileFlags.O_RDWR);
        
        var fdsToPass = new List<Fiberish.VFS.LinuxFile> { dummyFile };
        byte[] payload = { 1, 2, 3, 4 };
        
        int sent = await sock1.SendMessageAsync(file1, payload, fdsToPass, 0);
        Assert.Equal(4, sent);
        
        byte[] recvBuf = new byte[10];
        // The receive is async, but data is already there so it completes synchronously essentially
        var recvRes = await sock2.RecvMessageAsync(file2, recvBuf, 0);
        Assert.Equal(4, recvRes.BytesRead);
        Assert.NotNull(recvRes.Fds);
        Assert.Single(recvRes.Fds);
        Assert.Equal(dummyFile, recvRes.Fds[0]);
    }
}
