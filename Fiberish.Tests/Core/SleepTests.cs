using System.Text;
using System.Text.RegularExpressions;
using Fiberish.Core;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Core;

public class SleepTests
{
    private Process CreateMockProcess(int pid)
    {
        return new Process(pid, null!, null!);
    }

    [Fact(Timeout = 5000)]
    public void Sleep_PausesTask_And_Resumes()
    {
        var kernel = new KernelScheduler();
        var engine = new MockEngine();
        var sb = new StringBuilder();

        var p = CreateMockProcess(100);
        kernel.RegisterProcess(p);

        // Task 1: Sleeps
        var tSleep = new FiberTask(101, p, engine, kernel);

        async void RunSleepTask()
        {
            sb.Append($"SleepStart@{kernel.CurrentTick};");
            await new SleepAwaitable(5, tSleep);
            sb.Append($"SleepEnd@{kernel.CurrentTick};");
            tSleep.Exited = true;
            tSleep.Status = FiberTaskStatus.Terminated;
        }

        tSleep.Continuation = RunSleepTask;

        kernel.RegisterTask(tSleep);

        // Run(1000) automatically sets null
        kernel.Run(1000);

        var result = sb.ToString();

        var match = Regex.Match(result, @"SleepStart@(\d+);SleepEnd@(\d+);");
        Assert.True(match.Success, $"Regex match failed on result: {result}");

        var start = long.Parse(match.Groups[1].Value);
        var end = long.Parse(match.Groups[2].Value);

        Assert.True(end - start >= 5, $"Expected at least 5 ticks to elapse, but got {end - start}. Result: {result}");
    }

    [Fact(Timeout = 5000)]
    public void Sleep_PendingWait_TaskRetiresWithoutResuming_AndDisposesEngineOnce()
    {
        var kernel = new KernelScheduler();
        var engine = new MockEngine();

        var p = CreateMockProcess(100);
        kernel.RegisterProcess(p);

        var task = new FiberTask(101, p, engine, kernel);
        var awaiter = new SleepAwaitable(5, task).GetAwaiter();
        var resumed = 0;

        awaiter.OnCompleted(() => Interlocked.Increment(ref resumed));
        kernel.DetachTask(task);
        kernel.Run(20);

        Assert.Equal(0, Volatile.Read(ref resumed));
        Assert.Equal(1, engine.DisposeCount);
    }

    private class MockEngine : Engine
    {
        public MockEngine() : base(true)
        {
        }

        public int DisposeCount { get; private set; }

        public override EmuStatus Status => EmuStatus.Running;
        public override uint Eip { get; set; }
        public override uint Eflags { get; set; }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
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