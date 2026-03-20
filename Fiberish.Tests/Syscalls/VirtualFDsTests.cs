using System.Buffers.Binary;
using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class VirtualFDsTests
{
    [Fact]
    public void EventFd_Counter_ReadWrite()
    {
        using var env = new TestEnv();
        var inode = new EventFdInode(0, env.MemfdSuperBlock, 5, FileFlags.O_RDWR);
        var efd = new LinuxFile(new Dentry("eventfd", inode, null, env.MemfdSuperBlock), FileFlags.O_RDWR,
            null!);

        // Read initial value 5
        var buf = new byte[8];
        var readLen = inode.Read(efd, buf, 0);
        Assert.Equal(8, readLen);
        Assert.Equal(5UL, BinaryPrimitives.ReadUInt64LittleEndian(buf));

        // Read again should return 0 (simulated block) or EAGAIN if NONBLOCK
        efd.Flags |= FileFlags.O_NONBLOCK;
        readLen = inode.Read(efd, buf, 0);
        Assert.Equal(-(int)Errno.EAGAIN, readLen);

        // Write 10
        BinaryPrimitives.WriteUInt64LittleEndian(buf, 10UL);
        var writeLen = inode.Write(efd, buf, 0);
        Assert.Equal(8, writeLen);

        // Read should return 10
        readLen = inode.Read(efd, buf, 0);
        Assert.Equal(8, readLen);
        Assert.Equal(10UL, BinaryPrimitives.ReadUInt64LittleEndian(buf));
    }

    [Fact]
    public void EventFd_Semaphore_Semantics()
    {
        using var env = new TestEnv();
        var inode = new EventFdInode(0, env.MemfdSuperBlock, 5, (FileFlags)LinuxConstants.EFD_SEMAPHORE);
        var efd = new LinuxFile(new Dentry("eventfd", inode, null, env.MemfdSuperBlock),
            (FileFlags)LinuxConstants.EFD_SEMAPHORE, null!);

        var buf = new byte[8];
        // Read should return 1 and decrement counter
        for (var i = 0; i < 5; i++)
        {
            var readLen = inode.Read(efd, buf, 0);
            Assert.Equal(8, readLen);
            Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(buf));
        }

        // 6th read should block/EAGAIN
        efd.Flags |= FileFlags.O_NONBLOCK;
        var err = inode.Read(efd, buf, 0);
        Assert.Equal(-(int)Errno.EAGAIN, err);

        // Write 2
        BinaryPrimitives.WriteUInt64LittleEndian(buf, 2UL);
        inode.Write(efd, buf, 0);

        // Poll should show POLLIN
        Assert.Equal(LinuxConstants.POLLIN | LinuxConstants.POLLOUT,
            inode.Poll(efd, LinuxConstants.POLLIN | LinuxConstants.POLLOUT));
    }

    [Fact]
    public void TimerFd_SetAndGetTime()
    {
        using var env = new TestEnv();
        var inode = new TimerFdInode(0, env.MemfdSuperBlock, env.Scheduler);
        var tfd = new LinuxFile(new Dentry("timerfd", inode, null, env.MemfdSuperBlock), FileFlags.O_RDWR,
            null!);

        inode.SetTime(2000, 5000, false);
        inode.GetTime(out var interval, out var value);

        Assert.Equal(2000L, interval);
        Assert.Equal(5000L, value);
    }

    [Fact]
    public void TimerFd_Expiration_Read()
    {
        using var env = new TestEnv();
        var inode = new TimerFdInode(0, env.MemfdSuperBlock, env.Scheduler);
        var tfd = new LinuxFile(new Dentry("timerfd", inode, null, env.MemfdSuperBlock),
            FileFlags.O_NONBLOCK, null!);

        // Manually invoke the callback to simulate expiration
        var method = typeof(TimerFdInode).GetMethod("TimerCallback",
            BindingFlags.NonPublic | BindingFlags.Instance);

        method!.Invoke(inode, null);
        method.Invoke(inode, null);

        // Should have 2 expirations
        var buf = new byte[8];
        var readLen = inode.Read(tfd, buf, 0);
        Assert.Equal(8, readLen);
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(buf));

        // Next read should EAGAIN
        readLen = inode.Read(tfd, buf, 0);
        Assert.Equal(-(int)Errno.EAGAIN, readLen);
    }

    [Fact]
    public void SignalFd_Read_SigInfo()
    {
        using var env = new TestEnv();
        var inode = new SignalFdInode(0, env.MemfdSuperBlock, 1UL << ((int)Signal.SIGUSR1 - 1));
        var sfd = new LinuxFile(new Dentry("signalfd", inode, null, env.MemfdSuperBlock),
            FileFlags.O_NONBLOCK, null!);

        // Queue a signal
        env.Task.PostSignalInfo(new SigInfo
        {
            Signo = (int)Signal.SIGUSR1,
            Code = 0, // SI_USER
            Pid = 1234,
            Uid = 1000,
            Value = 42
        });

        // Reading should return siginfo
        var buf = new byte[128];
        var readLen = inode.Read(env.Task, sfd, buf);
        Assert.Equal(128, readLen);

        Assert.Equal((uint)Signal.SIGUSR1, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8, 4)));
        Assert.Equal(1234U, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(12, 4)));
        Assert.Equal(1000U, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16, 4)));
        Assert.Equal(42UL, BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(56, 8)));

        // Next read should EAGAIN
        readLen = inode.Read(env.Task, sfd, buf);
        Assert.Equal(-(int)Errno.EAGAIN, readLen);
    }

    [Fact]
    public void EventFd_RegisterWaitHandle_AlreadyReadable_ShouldInvokeImmediately()
    {
        using var env = new TestEnv();
        var inode = new EventFdInode(0, env.MemfdSuperBlock, 1, FileFlags.O_RDWR);
        var efd = new LinuxFile(new Dentry("eventfd", inode, null, env.MemfdSuperBlock), FileFlags.O_RDWR,
            null!);

        var fired = 0;
        using var reg = inode.RegisterWaitHandle(efd, () => Interlocked.Increment(ref fired), LinuxConstants.POLLIN);

        Assert.Equal(1, Volatile.Read(ref fired));
    }

    [Fact]
    public void SignalFd_RegisterWaitHandle_PostSignal_ShouldInvokeCallback()
    {
        using var env = new TestEnv();
        var inode = new SignalFdInode(0, env.MemfdSuperBlock, 1UL << ((int)Signal.SIGUSR1 - 1));
        var sfd = new LinuxFile(new Dentry("signalfd", inode, null, env.MemfdSuperBlock),
            FileFlags.O_NONBLOCK, null!);

        var fired = 0;
        using var reg = inode.RegisterWaitHandle(env.Task, () => Interlocked.Increment(ref fired), LinuxConstants.POLLIN);

        env.Task.PostSignalInfo(new SigInfo
        {
            Signo = (int)Signal.SIGUSR1,
            Code = 0,
            Pid = 1234,
            Uid = 1000,
            Value = 7
        });

        SpinWait.SpinUntil(() => Volatile.Read(ref fired) > 0, 200);
        Assert.Equal(1, Volatile.Read(ref fired));
    }

    private class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Scheduler = new KernelScheduler();

            var fs = new Tmpfs();
            MemfdSuperBlock = fs.ReadSuper(new FileSystemType { Name = "tmpfs" }, 0, "", null);

            var vma = new VMAManager();
            var engine = new Engine();
            Process = new Process(100, vma, null!);
            Scheduler.RegisterProcess(Process);
            Task = new FiberTask(100, Process, engine, Scheduler);

            typeof(KernelScheduler).GetProperty("CurrentTask")!.SetValue(Scheduler, Task);
        }

        public KernelScheduler Scheduler { get; }
        public Process Process { get; }
        public FiberTask Task { get; }
        public SuperBlock MemfdSuperBlock { get; }

        public void Dispose()
        {
            
        }
    }
}
