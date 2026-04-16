using Fiberish.Core;
using Fiberish.Native;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Core;

public class BlockingHostOperationAwaitableTests
{
    [Fact(Timeout = 5000)]
    public void BlockingHostOperationAwaitable_CompletionResumesOnSchedulerThread()
    {
        var scheduler = new KernelScheduler();
        var process = TestRuntimeFactory.CreateProcess(900);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(901, process, new MockEngine(), scheduler);

        var result = int.MinValue;
        var resumedOnSchedulerThread = false;

        async void Entry()
        {
            result = await new BlockingHostOperationAwaitable(task, "test", () => 0);
            resumedOnSchedulerThread = scheduler.IsSchedulerThread;
            task.Exited = true;
            task.Status = FiberTaskStatus.Terminated;
        }

        task.Continuation = Entry;
        scheduler.RegisterTask(task);
        scheduler.Run(1000);

        Assert.Equal(0, result);
        Assert.True(resumedOnSchedulerThread);
    }

    [Fact(Timeout = 5000)]
    public void BlockingHostOperationAwaitable_SignalInterruptsWait_AndLateCompletionDoesNotDoubleResume()
    {
        var scheduler = new KernelScheduler();
        var process = TestRuntimeFactory.CreateProcess(910);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(911, process, new MockEngine(), scheduler);

        using var workerStarted = new ManualResetEventSlim(false);
        using var workerRelease = new ManualResetEventSlim(false);
        var result = int.MinValue;
        var resumed = 0;

        async void Entry()
        {
            scheduler.ScheduleTimer(1, () => task.PostSignal((int)Signal.SIGUSR1));
            result = await new BlockingHostOperationAwaitable(task, "test", () =>
            {
                workerStarted.Set();
                workerRelease.Wait();
                return 0;
            });
            Interlocked.Increment(ref resumed);
            workerRelease.Set();
            task.Exited = true;
            task.Status = FiberTaskStatus.Terminated;
        }

        task.Continuation = Entry;
        scheduler.RegisterTask(task);
        scheduler.Run(1000);

        Assert.True(workerStarted.IsSet);
        Assert.Equal(-(int)Errno.ERESTARTSYS, result);
        Assert.Equal(1, Volatile.Read(ref resumed));
    }

    [Fact(Timeout = 5000)]
    public void BlockingHostOperationAwaitable_TaskRetirementCancelsPendingWait()
    {
        var scheduler = new KernelScheduler();
        var process = TestRuntimeFactory.CreateProcess(920);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(921, process, new MockEngine(), scheduler);

        using var workerStarted = new ManualResetEventSlim(false);
        using var workerRelease = new ManualResetEventSlim(false);
        var resumed = 0;

        var awaiter = new BlockingHostOperationAwaitable(task, "test", () =>
        {
            workerStarted.Set();
            workerRelease.Wait();
            return 0;
        }).GetAwaiter();

        awaiter.OnCompleted(() => Interlocked.Increment(ref resumed));
        Assert.True(workerStarted.Wait(1000), "Worker did not start in time.");

        scheduler.DetachTask(task);
        workerRelease.Set();
        scheduler.Run(100);

        Assert.Equal(0, Volatile.Read(ref resumed));
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
