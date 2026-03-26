using System.Reflection;
using System.Runtime.CompilerServices;
using Fiberish.Core;
using Fiberish.Core.VFS.TTY;
using Fiberish.Native;
using Fiberish.Syscalls;
using Microsoft.Extensions.Logging.Abstractions;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Core;

public class FiberTaskAsyncSyscallTests
{
    [Fact]
    public void Schedule_WhenTaskReusesReservedRunQueueToken_MustEmitFreshSchedulerWake()
    {
        var scheduler = new KernelScheduler();
        var process = new Process(390, null!, null!);
        scheduler.RegisterProcess(process);
        var engine = new MockEngine();
        var task = new FiberTask(391, process, engine, scheduler);

        scheduler.Schedule(task);
        Assert.True(DrainSchedulerEvents(scheduler));

        task.Status = FiberTaskStatus.Waiting;

        scheduler.Schedule(task);

        Assert.Equal(FiberTaskStatus.Ready, task.Status);
        Assert.True(DrainSchedulerEvents(scheduler));
        Assert.False(DrainSchedulerEvents(scheduler));
    }

    [Fact]
    public void Schedule_WhenReadyQueuedStateDriftsWithoutActualRunQueueEntry_MustRepairByReenqueuingTask()
    {
        var scheduler = new KernelScheduler();
        var process = new Process(392, null!, null!);
        scheduler.RegisterProcess(process);
        var engine = new MockEngine();
        var task = new FiberTask(393, process, engine, scheduler);

        SetIsReadyQueued(task, true);
        task.Status = FiberTaskStatus.Waiting;

        scheduler.Schedule(task);

        Assert.Equal(FiberTaskStatus.Ready, task.Status);
        Assert.True(DrainSchedulerEvents(scheduler));

        var dequeued = TryDequeueTask(scheduler);
        Assert.Same(task, dequeued);
        Assert.False(task.IsReadyQueued);
    }

    [Fact]
    public async Task HandleAsyncSyscall_WhenPendingSyscallThrows_MapsToEfaultAndClearsPendingSyscall()
    {
        var scheduler = new KernelScheduler();
        var process = new Process(400, null!, null!);
        scheduler.RegisterProcess(process);
        var engine = new MockEngine();
        var task = new FiberTask(401, process, engine, scheduler);
        task.PendingSyscall = () => new ValueTask<int>(Task.FromException<int>(new InvalidOperationException("boom")));

        await InvokeAndDrainAsyncSyscall(task, scheduler);

        Assert.Null(task.PendingSyscall);
        Assert.Equal(unchecked((uint)-(int)Errno.EFAULT), engine.RegRead(Reg.EAX));
    }

    [Fact]
    public async Task HandleAsyncSyscall_WhenPendingSyscallThrowsOutOfMemory_MapsToEnomemAndClearsPendingSyscall()
    {
        var scheduler = new KernelScheduler();
        var process = new Process(410, null!, null!);
        scheduler.RegisterProcess(process);
        var engine = new MockEngine();
        var task = new FiberTask(411, process, engine, scheduler);
        task.PendingSyscall = () => new ValueTask<int>(Task.FromException<int>(new OutOfMemoryException("oom")));

        await InvokeAndDrainAsyncSyscall(task, scheduler);

        Assert.Null(task.PendingSyscall);
        Assert.Equal(unchecked((uint)-(int)Errno.ENOMEM), engine.RegRead(Reg.EAX));
    }

    [Fact]
    public void FinalizeAsyncSyscall_WhenStaleReadyQueuedFlagExists_MustForceFreshRunQueueEntry()
    {
        var scheduler = new KernelScheduler();
        var engine = new MockEngine();
        var fakeSyscalls = CreateFakeSyscallManager();
        var process = new Process(420, null!, fakeSyscalls);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(421, process, engine, scheduler)
        {
            PendingSyscall = () => new ValueTask<int>(0),
            Status = FiberTaskStatus.Waiting
        };

        SetIsReadyQueued(task, true);

        var finalize = typeof(FiberTask).GetMethod("FinalizeAsyncSyscall",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(finalize);

        finalize!.Invoke(task, [123, null]);

        Assert.Null(task.PendingSyscall);
        Assert.Equal(FiberTaskStatus.Ready, task.Status);
        Assert.True(DrainSchedulerEvents(scheduler));

        var dequeued = TryDequeueTask(scheduler);
        Assert.Same(task, dequeued);
        Assert.False(task.IsReadyQueued);
        Assert.Equal(123u, engine.RegRead(Reg.EAX));
    }

    private static async Task InvokeAndDrainAsyncSyscall(FiberTask task, KernelScheduler scheduler)
    {
        var method =
            typeof(FiberTask).GetMethod("HandleAsyncSyscallAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? typeof(FiberTask).GetMethod("HandleAsyncSyscall", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(task, null);
        if (result is ValueTask vt)
            await vt;

        var drainEvents =
            typeof(KernelScheduler).GetMethod("DrainEvents", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(drainEvents);

        for (var i = 0; i < 100 && task.PendingSyscall != null; i++)
        {
            _ = (bool)drainEvents!.Invoke(scheduler, null)!;
            await Task.Delay(10);
        }
    }

    private static bool DrainSchedulerEvents(KernelScheduler scheduler)
    {
        var drainEvents =
            typeof(KernelScheduler).GetMethod("DrainEvents", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(drainEvents);
        return (bool)drainEvents!.Invoke(scheduler, null)!;
    }

    private static FiberTask? TryDequeueTask(KernelScheduler scheduler)
    {
        var method = typeof(KernelScheduler).GetMethod("TryDequeue", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var args = new object?[] { null };
        var dequeued = (bool)method!.Invoke(scheduler, args)!;
        Assert.True(dequeued);
        return Assert.IsType<FiberTask>(args[0]);
    }

    private static void SetIsReadyQueued(FiberTask task, bool value)
    {
        var property = typeof(FiberTask).GetProperty("IsReadyQueued",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(property);
        property!.SetValue(task, value);
    }

    private static SyscallManager CreateFakeSyscallManager()
    {
        var syscalls = (SyscallManager)RuntimeHelpers.GetUninitializedObject(typeof(SyscallManager));
        syscalls.Strace = false;

        var ptyManagerField = typeof(SyscallManager).GetField("<PtyManager>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(ptyManagerField);
        ptyManagerField!.SetValue(syscalls, new PtyManager(NullLogger.Instance));

        return syscalls;
    }

    private sealed class MockEngine : Engine
    {
        private readonly Dictionary<Reg, uint> _regs = new();

        public MockEngine() : base(true)
        {
        }

        public override EmuStatus Status => EmuStatus.Running;
        public override uint Eip { get; set; }
        public override uint Eflags { get; set; }

        protected override void Dispose(bool disposing)
        {
        }

        public override void Run(uint endEip = 0, ulong maxInsts = 0)
        {
        }

        public override uint RegRead(Reg reg)
        {
            return _regs.TryGetValue(reg, out var value) ? value : 0;
        }

        public override void RegWrite(Reg reg, uint val)
        {
            _regs[reg] = val;
        }
    }
}
