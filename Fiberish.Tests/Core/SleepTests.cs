using Xunit;
using Bifrost.Core;
using Bifrost.Memory;
using Bifrost.Syscalls;
using System.Text;
using Bifrost.Native;

namespace Fiberish.Tests.Core;

public class SleepTests
{
    private class MockEngine : Engine
    {
        public MockEngine() : base() {}
        protected override void Dispose(bool disposing) {}
    }

    private FiberProcess CreateMockProcess(int pid)
    {
        return new FiberProcess(pid, null!, null!);
    }
    
    [Fact]
    public void Sleep_PausesTask_And_Resumes()
    {
        var kernel = new KernelScheduler();
        var sb = new StringBuilder();
        
        var tSleep = new FiberTask(101, CreateMockProcess(100), new MockEngine(), kernel);
        var tIdle = new FiberTask(999, CreateMockProcess(999), new MockEngine(), kernel);

        async void RunSleepTask()
        {
            sb.Append($"SleepStart@{kernel.Ticks};");
            await new SleepAwaitable(5); 
            sb.Append($"SleepEnd@{kernel.Ticks};");
        }

        async void RunIdleTask()
        {
            // Run for at most 20 ticks then exit
            while (kernel.Ticks < 20)
            {
                await new YieldAwaitable();
            }
            sb.Append("IdleDone;");
        }

        tSleep.Continuation = RunSleepTask;
        tIdle.Continuation = RunIdleTask;
        
        kernel.Register(tSleep);
        kernel.Register(tIdle);
        
        kernel.Run();
        
        string result = sb.ToString();
        // Ticks start at 0.
        // tSleep runs first (tick 0). Prints SleepStart@0. Sleeps for 5. Wakeup at 5.
        // tIdle runs (tick 0). Yields.
        // Tick becomes 1.
        // tIdle runs (tick 1). Yields.
        // ...
        // tIdle runs (tick 4). Yields.
        // Tick becomes 5. 
        // tSleep wakes up. Enqueued.
        // tIdle runs (tick 5). Yields. Enqueued.
        // tSleep runs (tick 5). Prints SleepEnd@5. Finishes.
        // tIdle keeps running until tick 20.
        
        Assert.Contains("SleepStart@0;", result);
        Assert.Contains("SleepEnd@6;", result);
        Assert.Contains("IdleDone;", result);
    }
}
