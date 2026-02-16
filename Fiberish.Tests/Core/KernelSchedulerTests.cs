using Xunit;
using Bifrost.Core;
using Bifrost.Memory;
using Bifrost.Syscalls;
using System.Text;
using Bifrost.Native;

namespace Fiberish.Tests.Core;

public class KernelSchedulerTests
{
    private class MockEngine : Engine
    {
        public MockEngine() : base() // Real handle
        {
        }
        
        protected override void Dispose(bool disposing) {}
    }

    // Mock Process using nulls where acceptable for tests
    private FiberProcess CreateMockProcess(int pid)
    {
        // We can't easily mock VMAManager/SyscallManager without real Engine, unless we mock them too.
        // For Scheduler tests, we might get away with nulls if we don't access them.
        // FiberTask checks Process.Mem for faults.
        return new FiberProcess(pid, null!, null!);
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
            await new YieldAwaitable();
            sb.Append("T1-Mid;");
            await new YieldAwaitable();
            sb.Append("T1-End;");
        }

        async void RunTask2()
        {
            sb.Append("T2-Start;");
            await new YieldAwaitable();
            sb.Append("T2-Mid;");
            await new YieldAwaitable();
            sb.Append("T2-End;");
        }
        
        // Register tasks
        // When we call the async method (RunTask1), it runs synchronously until the first await.
        // The first await (YieldAwaitable) will capture the continuation and register it with the Kernel.
        // BUT, YieldAwaitable.UnsafeOnCompleted needs KernelScheduler.Current to be set!
        
        // So we must invoke the initial calls INSIDE the kernel run loop or with Current set.
        
        // Register tasks with initial entry points
        // The first execution will happen when Kernel picks them up.
        // Since RunTask1 is async void, passing it as Action works.
        t1.Continuation = RunTask1;
        kernel.Register(t1);
        
        t2.Continuation = RunTask2;
        kernel.Register(t2);
        
        // Run the kernel
        kernel.Run();
        // Expected order:
        // Init -> RunTask1 (runs to yield) -> Yield (schedules T1 continuation)
        //      -> RunTask2 (runs to yield) -> Yield (schedules T2 continuation)
        // Loop 1: T1 (T1-Mid) -> Yield
        // Loop 2: T2 (T2-Mid) -> Yield
        // Loop 3: T1 (T1-End) -> Done
        // Loop 4: T2 (T2-End) -> Done
        
        // Actually, RunTask1 is "async void".
        // When called, it executes:
        // "T1-Start;"
        // await new YieldAwaitable(); -> calls OnCompleted -> Registers T1 continuation -> Returns.
        
        // Then Register(t2).
        // Then RunTask2();
        // "T2-Start;"
        // await new YieldAwaitable(); -> Registers T2 continuation -> Returns.
        
        // Then requestInit finishes.
        
        // Kernel Loop:
        // Queue: [T1, T2]
        
        kernel.Run();
        
        string result = sb.ToString();
        Assert.Equal("T1-Start;T2-Start;T1-Mid;T2-Mid;T1-End;T2-End;", result);
    }
}
