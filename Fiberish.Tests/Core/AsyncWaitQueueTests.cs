using Fiberish.Core;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Core;

public class AsyncWaitQueueTests
{
    [Fact]
    public void RegisterCancelable_DisposeBeforeSignal_DoesNotInvokeContinuation()
    {
        var scheduler = new KernelScheduler();
        var process = new Process(100, null!, null!);
        scheduler.RegisterProcess(process);

        var queue = new AsyncWaitQueue();
        var fired = 0;
        var task = new FiberTask(101, process, new MockEngine(), scheduler);

        void Entry()
        {
            var reg = queue.RegisterCancelable(() => fired++, KernelScheduler.Current!.CurrentTask);
            Assert.NotNull(reg);
            reg!.Dispose();

            queue.Signal();

            task.Exited = true;
            task.Status = FiberTaskStatus.Terminated;
        }

        task.Continuation = Entry;
        scheduler.RegisterTask(task);
        scheduler.Run(100);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void RegisterCancelable_DisposeOneOfMultiple_OnlyActiveContinuationRuns()
    {
        var scheduler = new KernelScheduler();
        var process = new Process(200, null!, null!);
        scheduler.RegisterProcess(process);

        var queue = new AsyncWaitQueue();
        var firedA = 0;
        var firedB = 0;
        var task = new FiberTask(201, process, new MockEngine(), scheduler);

        void Entry()
        {
            var regA = queue.RegisterCancelable(() => firedA++, KernelScheduler.Current!.CurrentTask);
            var regB = queue.RegisterCancelable(() => firedB++, KernelScheduler.Current!.CurrentTask);

            Assert.NotNull(regA);
            Assert.NotNull(regB);

            regA!.Dispose();
            queue.Signal();

            task.Exited = true;
            task.Status = FiberTaskStatus.Terminated;
        }

        task.Continuation = Entry;
        scheduler.RegisterTask(task);
        scheduler.Run(100);

        Assert.Equal(0, firedA);
        Assert.Equal(1, firedB);
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
