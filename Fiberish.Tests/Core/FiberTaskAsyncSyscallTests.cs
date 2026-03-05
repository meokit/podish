using System.Reflection;
using Fiberish.Core;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Core;

public class FiberTaskAsyncSyscallTests
{
    [Fact]
    public async Task HandleAsyncSyscall_WhenPendingSyscallThrows_ClearsPendingSyscall()
    {
        var scheduler = new KernelScheduler();
        var process = new Process(400, null!, null!);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(401, process, new MockEngine(), scheduler);
        task.PendingSyscall = () => new ValueTask<int>(Task.FromException<int>(new InvalidOperationException("boom")));

        var method = typeof(FiberTask).GetMethod("HandleAsyncSyscall", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(task, null);

        for (var i = 0; i < 100 && task.PendingSyscall != null; i++)
            await Task.Delay(10);

        Assert.Null(task.PendingSyscall);
    }

    private sealed class MockEngine : Engine
    {
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
            return 0;
        }

        public override void RegWrite(Reg reg, uint val)
        {
        }
    }
}
