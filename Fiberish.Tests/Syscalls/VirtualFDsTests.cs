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
        var inode = new EventFdInode(0, env.MemfdSuperBlock, env.Task.CommonKernel, 5, FileFlags.O_RDWR);
        var efd = new LinuxFile(new Dentry(FsName.FromString("eventfd"), inode, null, env.MemfdSuperBlock), FileFlags.O_RDWR,
            null!);

        // Read initial value 5
        var buf = new byte[8];
        var readLen = inode.ReadToHost(null, efd, buf, 0);
        Assert.Equal(8, readLen);
        Assert.Equal(5UL, BinaryPrimitives.ReadUInt64LittleEndian(buf));

        // Read again should return 0 (simulated block) or EAGAIN if NONBLOCK
        efd.Flags |= FileFlags.O_NONBLOCK;
        readLen = inode.ReadToHost(null, efd, buf, 0);
        Assert.Equal(-(int)Errno.EAGAIN, readLen);

        // Write 10
        BinaryPrimitives.WriteUInt64LittleEndian(buf, 10UL);
        var writeLen = inode.WriteFromHost(null, efd, buf, 0);
        Assert.Equal(8, writeLen);

        // Read should return 10
        readLen = inode.ReadToHost(null, efd, buf, 0);
        Assert.Equal(8, readLen);
        Assert.Equal(10UL, BinaryPrimitives.ReadUInt64LittleEndian(buf));
    }

    [Fact]
    public void EventFd_Semaphore_Semantics()
    {
        using var env = new TestEnv();
        var inode = new EventFdInode(0, env.MemfdSuperBlock, env.Task.CommonKernel, 5,
            (FileFlags)LinuxConstants.EFD_SEMAPHORE);
        var efd = new LinuxFile(new Dentry(FsName.FromString("eventfd"), inode, null, env.MemfdSuperBlock),
            (FileFlags)LinuxConstants.EFD_SEMAPHORE, null!);

        var buf = new byte[8];
        // Read should return 1 and decrement counter
        for (var i = 0; i < 5; i++)
        {
            var readLen = inode.ReadToHost(null, efd, buf, 0);
            Assert.Equal(8, readLen);
            Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(buf));
        }

        // 6th read should block/EAGAIN
        efd.Flags |= FileFlags.O_NONBLOCK;
        var err = inode.ReadToHost(null, efd, buf, 0);
        Assert.Equal(-(int)Errno.EAGAIN, err);

        // Write 2
        BinaryPrimitives.WriteUInt64LittleEndian(buf, 2UL);
        inode.WriteFromHost(null, efd, buf, 0);

        // Poll should show POLLIN
        Assert.Equal(LinuxConstants.POLLIN | LinuxConstants.POLLOUT,
            inode.Poll(efd, LinuxConstants.POLLIN | LinuxConstants.POLLOUT));
    }

    [Fact]
    public void TimerFd_SetAndGetTime()
    {
        using var env = new TestEnv();
        var inode = new TimerFdInode(0, env.MemfdSuperBlock);
        var tfd = new LinuxFile(new Dentry(FsName.FromString("timerfd"), inode, null, env.MemfdSuperBlock), FileFlags.O_RDWR,
            null!);

        inode.SetTime(env.Task, 2000, 5000, false);
        inode.GetTime(env.Task, out var interval, out var value);

        Assert.Equal(2000L, interval);
        Assert.Equal(5000L, value);
    }

    [Fact]
    public void TimerFd_Expiration_Read()
    {
        using var env = new TestEnv();
        var inode = new TimerFdInode(0, env.MemfdSuperBlock);
        var tfd = new LinuxFile(new Dentry(FsName.FromString("timerfd"), inode, null, env.MemfdSuperBlock),
            FileFlags.O_NONBLOCK, null!);

        // Manually invoke the callback to simulate expiration
        var method = typeof(TimerFdInode).GetMethod("TimerCallback",
            BindingFlags.NonPublic | BindingFlags.Instance);

        method!.Invoke(inode, null);
        method.Invoke(inode, null);

        // Should have 2 expirations
        var buf = new byte[8];
        var readLen = inode.ReadToHost(null, tfd, buf, 0);
        Assert.Equal(8, readLen);
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(buf));

        // Next read should EAGAIN
        readLen = inode.ReadToHost(null, tfd, buf, 0);
        Assert.Equal(-(int)Errno.EAGAIN, readLen);
    }

    [Fact]
    public void SignalFd_Read_SigInfo()
    {
        using var env = new TestEnv();
        var inode = new SignalFdInode(0, env.MemfdSuperBlock, 1UL << ((int)Signal.SIGUSR1 - 1));
        var sfd = new LinuxFile(new Dentry(FsName.FromString("signalfd"), inode, null, env.MemfdSuperBlock),
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
        var readLen = inode.ReadToHost(env.Task, sfd, buf, 0);
        Assert.Equal(128, readLen);

        Assert.Equal((uint)Signal.SIGUSR1, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8, 4)));
        Assert.Equal(1234U, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(12, 4)));
        Assert.Equal(1000U, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16, 4)));
        Assert.Equal(42UL, BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(56, 8)));

        // Next read should EAGAIN
        readLen = inode.ReadToHost(env.Task, sfd, buf, 0);
        Assert.Equal(-(int)Errno.EAGAIN, readLen);
    }

    [Fact]
    public void EventFd_RegisterWaitHandle_AlreadyReadable_ShouldInvokeImmediately()
    {
        using var env = new TestEnv();
        var inode = new EventFdInode(0, env.MemfdSuperBlock, env.Task.CommonKernel, 1, FileFlags.O_RDWR);
        var efd = new LinuxFile(new Dentry(FsName.FromString("eventfd"), inode, null, env.MemfdSuperBlock), FileFlags.O_RDWR,
            null!);

        var fired = 0;
        using var reg = inode.RegisterWaitHandle(efd, () => Interlocked.Increment(ref fired), LinuxConstants.POLLIN);

        Assert.Equal(1, Volatile.Read(ref fired));
    }

    [Fact]
    public void EventFd_Close_FinalizesInodeAndReturnsWaitQueues()
    {
        using var env = new TestEnv();
        var poolField = typeof(KernelScheduler).GetField("_asyncWaitQueuePool",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pool = Assert.IsType<Stack<AsyncWaitQueue>>(poolField.GetValue(env.Task.CommonKernel));
        var before = pool.Count;

        var inode = new EventFdInode(0, env.MemfdSuperBlock, env.Task.CommonKernel, 1, FileFlags.O_RDWR);
        var file = new LinuxFile(new Dentry(FsName.FromString("eventfd"), inode, null, env.MemfdSuperBlock),
            FileFlags.O_RDWR, null!);

        Assert.Equal(before, pool.Count);

        file.Close();

        Assert.True(inode.IsFinalized);
        Assert.True(inode.IsCacheEvicted);
        Assert.Equal(before + 2, pool.Count);
        Assert.Empty(inode.Dentries);
    }

    [Fact]
    public void SignalFd_RegisterWaitHandle_PostSignal_ShouldInvokeCallback()
    {
        using var env = new TestEnv();
        var inode = new SignalFdInode(0, env.MemfdSuperBlock, 1UL << ((int)Signal.SIGUSR1 - 1));
        var sfd = new LinuxFile(new Dentry(FsName.FromString("signalfd"), inode, null, env.MemfdSuperBlock),
            FileFlags.O_NONBLOCK, null!);

        var fired = 0;
        using var reg =
            inode.RegisterWaitHandle(env.Task, () => Interlocked.Increment(ref fired), LinuxConstants.POLLIN);

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

    [Fact]
    public void SignalFd_WaitAsync_UnrelatedSignalMustNotWakeAwaiter()
    {
        using var env = new TestEnv();
        var inode = new SignalFdInode(0, env.MemfdSuperBlock, 1UL << ((int)Signal.SIGUSR1 - 1));
        var awaiter = inode.WaitAsync(env.Task).GetAwaiter();
        var resumed = 0;
        env.Task.SignalMask |= 1UL << ((int)Signal.SIGUSR2 - 1);

        awaiter.OnCompleted(() => Interlocked.Increment(ref resumed));

        env.Task.PostSignalInfo(new SigInfo { Signo = (int)Signal.SIGUSR2, Code = 0 });
        env.DrainEvents();

        Assert.Equal(0, Volatile.Read(ref resumed));
        Assert.True((env.Task.PendingSignals & (1UL << ((int)Signal.SIGUSR2 - 1))) != 0);

        env.Task.PostSignalInfo(new SigInfo { Signo = (int)Signal.SIGUSR1, Code = 0, Value = 9 });
        env.DrainEvents();

        Assert.Equal(1, Volatile.Read(ref resumed));
        Assert.Equal(AwaitResult.Completed, awaiter.GetResult());
    }

    [Fact]
    public void SignalFd_Read_ConsumesOnlyMaskedSignalAndLeavesUnrelatedPending()
    {
        using var env = new TestEnv();
        var inode = new SignalFdInode(0, env.MemfdSuperBlock, 1UL << ((int)Signal.SIGUSR1 - 1));
        var sfd = new LinuxFile(new Dentry(FsName.FromString("signalfd"), inode, null, env.MemfdSuperBlock),
            FileFlags.O_NONBLOCK, null!);
        env.Task.SignalMask |= 1UL << ((int)Signal.SIGUSR2 - 1);

        env.Task.PostSignalInfo(new SigInfo { Signo = (int)Signal.SIGUSR2, Code = 0, Value = 1 });
        env.Task.PostSignalInfo(new SigInfo { Signo = (int)Signal.SIGUSR1, Code = 0, Value = 2 });

        var buf = new byte[128];
        var readLen = inode.ReadToHost(env.Task, sfd, buf, 0);
        Assert.Equal(128, readLen);
        Assert.Equal((uint)Signal.SIGUSR1, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4)));
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(56, 8)));

        Assert.True((env.Task.PendingSignals & (1UL << ((int)Signal.SIGUSR2 - 1))) != 0);
        Assert.Equal(-(int)Errno.EAGAIN, inode.ReadToHost(env.Task, sfd, buf, 0));
    }

    [Fact]
    public async Task SignalFd_SysRead_UnrelatedSignalMustNotInterruptAndMaskedSignalMustComplete()
    {
        using var env = new TestEnv();
        const uint bufPtr = 0x10000;
        env.MapUserPage(bufPtr);

        var inode = new SignalFdInode(0, env.MemfdSuperBlock, 1UL << ((int)Signal.SIGUSR1 - 1));
        var file = new LinuxFile(new Dentry(FsName.FromString("signalfd"), inode, null, env.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        var fd = env.SyscallManager.AllocFD(file);
        env.Task.SignalMask |= 1UL << ((int)Signal.SIGUSR2 - 1);

        var pending = env.StartOnScheduler(() => env.Invoke("SysRead", (uint)fd, bufPtr, 128, 0, 0, 0));
        Assert.False(pending.IsCompleted);

        await env.WaitForBackgroundSchedulerAsync();
        await env.PostSignalAsync((int)Signal.SIGUSR2);
        Assert.False(pending.IsCompleted);
        Assert.True((env.Task.PendingSignals & (1UL << ((int)Signal.SIGUSR2 - 1))) != 0);

        await env.PostSignalAsync((int)Signal.SIGUSR1);

        var rc = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(128, rc);
        Assert.Equal((uint)Signal.SIGUSR1, BinaryPrimitives.ReadUInt32LittleEndian(env.Read(bufPtr, 128).AsSpan(0, 4)));
        Assert.True((env.Task.PendingSignals & (1UL << ((int)Signal.SIGUSR2 - 1))) != 0);
    }

    private class TestEnv : IDisposable
    {
        private static readonly FieldInfo OwnerThreadIdField =
            typeof(KernelScheduler).GetField("_ownerThreadId", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private static readonly MethodInfo DrainEventsMethod =
            typeof(KernelScheduler).GetMethod("DrainEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private readonly TestRuntimeFactory _runtime = new();

        public TestEnv()
        {
            Scheduler = new KernelScheduler();

            var fs = new Tmpfs();
            MemfdSuperBlock = fs.ReadSuper(new FileSystemType { Name = "tmpfs" }, 0, "", null);

            Vma = _runtime.CreateAddressSpace();
            Engine = _runtime.CreateEngine();
            Process = new Process(100, Vma, null!);
            Scheduler.RegisterProcess(Process);
            Task = new FiberTask(100, Process, Engine, Scheduler);
            Engine.Owner = Task;
            Task.Status = FiberTaskStatus.Waiting;

            SyscallManager = new SyscallManager(Engine, Vma, 0);
            SyscallManager.MountRootHostfs(".");

            typeof(KernelScheduler).GetProperty("CurrentTask")!.SetValue(Scheduler, Task);
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public KernelScheduler Scheduler { get; }
        public Process Process { get; }
        public FiberTask Task { get; }
        public SuperBlock MemfdSuperBlock { get; }
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

        public Task<int> StartOnScheduler(Func<ValueTask<int>> action)
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

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

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public byte[] Read(uint addr, int count)
        {
            var buffer = new byte[count];
            Assert.True(Engine.CopyFromUser(addr, buffer));
            return buffer;
        }

        public void DrainEvents()
        {
            _ = (bool)DrainEventsMethod.Invoke(Scheduler, null)!;
        }

        private void ResetSchedulerThreadBinding()
        {
            OwnerThreadIdField.SetValue(Scheduler, 0);
        }
    }
}
