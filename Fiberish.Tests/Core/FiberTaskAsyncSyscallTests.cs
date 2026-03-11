using System.Reflection;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Core;

public class FiberTaskAsyncSyscallTests
{
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