using System.Text;
using Fiberish.Core;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Core;

public class KernelSchedulerTests
{
    // Mock Process using nulls where acceptable for tests
    private Process CreateMockProcess(int pid)
    {
        // We can't easily mock VMAManager/SyscallManager without real Engine, unless we mock them too.
        // For Scheduler tests, we might get away with nulls if we don't access them.
        // FiberTask checks Process.Mem for faults.
        return new Process(pid, null!, null!);
    }

    [Fact]
    public void PingPong_Scheduling_Works()
    {
        var kernel = new KernelScheduler();
        var sb = new StringBuilder();

        // We need a way to run async logic on FiberTask without real CPU execution
        // Since FiberTask logic is determined by the Continuation (Action), 
        // we can manually set the Initial Continuation to our async method state machine.

        // However, C# compiler generates the state machine.
        // We will simulate "Entry Point" by just registering a task and an action.

        var t1 = new FiberTask(101, CreateMockProcess(100), new MockEngine(), kernel);
        var t2 = new FiberTask(102, CreateMockProcess(100), new MockEngine(), kernel);

        async void RunTask1()
        {
            sb.Append("T1-Start;");
            await new YieldAwaitable(t1);
            sb.Append("T1-Mid;");
            await new YieldAwaitable(t1);
            sb.Append("T1-End;");
            t1.Exited = true; // Mark as exited
            t1.Status = FiberTaskStatus.Terminated;
        }

        async void RunTask2()
        {
            sb.Append("T2-Start;");
            await new YieldAwaitable(t2);
            sb.Append("T2-Mid;");
            await new YieldAwaitable(t2);
            sb.Append("T2-End;");
            t2.Exited = true;
            t2.Status = FiberTaskStatus.Terminated;
        }

        // Register tasks
        // When we call the async method (RunTask1), it runs synchronously until the first await.
        // The first await (YieldAwaitable) will capture the continuation and register it with the Kernel.
        // BUT, YieldAwaitable.UnsafeOnCompleted needs null to be set!

        // So we must invoke the initial calls INSIDE the kernel run loop or with Current set.

        // Register tasks with initial entry points
        // The first execution will happen when Kernel picks them up.
        // Since RunTask1 is async void, passing it as Action works.
        t1.Continuation = RunTask1;
        kernel.RegisterTask(t1);

        t2.Continuation = RunTask2;
        kernel.RegisterTask(t2);

        // Run (AsyncLocal sets context)
        kernel.Run(100);

        var result = sb.ToString();
        Assert.Equal("T1-Start;T2-Start;T1-Mid;T2-Mid;T1-End;T2-End;", result);
    }

    [Fact]
    public void Run_WhenIngressEventsFloodQueue_TaskStillGetsASliceBeforeAllEventsDrain()
    {
        var kernel = new KernelScheduler();
        var sb = new StringBuilder();

        var task = new FiberTask(201, CreateMockProcess(200), new MockEngine(), kernel);
        task.Continuation = () =>
        {
            sb.Append("TASK;");
            task.Exited = true;
            task.Status = FiberTaskStatus.Terminated;
        };

        for (var i = 0; i < 256; i++)
        {
            var capture = i;
            kernel.ScheduleFromAnyThread(() => sb.Append($"E{capture};"));
        }

        kernel.RegisterTask(task);
        kernel.Run(100);

        var result = sb.ToString();
        var taskIndex = result.IndexOf("TASK;", StringComparison.Ordinal);
        Assert.True(taskIndex >= 0);
        Assert.Contains("E0;", result);
        Assert.Contains("E255;", result);
        Assert.True(taskIndex < result.IndexOf("E255;", StringComparison.Ordinal),
            "TASK should run before the scheduler drains every ingress event.");
    }

    private class MockEngine : Engine
    {
        public MockEngine() : base(true)
        {
        } // Mock handle

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
