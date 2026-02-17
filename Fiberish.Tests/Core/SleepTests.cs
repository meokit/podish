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
        public MockEngine() : base(true) {}
        protected override void Dispose(bool disposing) {}

        public override void Run(uint endEip = 0, ulong maxInsts = 0) { }
        public override EmuStatus Status => EmuStatus.Running;
        public override uint RegRead(Reg reg) => 0;
        public override void RegWrite(Reg reg, uint val) { }
        public override uint Eip { get; set; }
        public override uint Eflags { get; set; }
    }

    private Process CreateMockProcess(int pid)
    {
        return new Process(pid, null!, null!);
    }
    
    [Fact(Timeout = 5000)]
    public void Sleep_PausesTask_And_Resumes()
    {
        var kernel = new KernelScheduler();
        var engine = new MockEngine();
        var sb = new System.Text.StringBuilder();

        var p = CreateMockProcess(100);
        kernel.RegisterProcess(p);

        // Task 1: Sleeps
        var tSleep = new FiberTask(101, p, engine, kernel);

        async void RunSleepTask()
        {
            sb.Append($"SleepStart@{kernel.CurrentTick};");
            await new SleepAwaitable(5); 
            sb.Append($"SleepEnd@{kernel.CurrentTick};");
            tSleep.Exited = true;
            tSleep.Status = FiberTaskStatus.Terminated;
        }

        tSleep.Continuation = RunSleepTask;
        
        kernel.RegisterTask(tSleep);
        
        // Run(1000) automatically sets KernelScheduler.Current
        kernel.Run(1000);
        
        string result = sb.ToString();
        // Ticks start at 0.
        
        Assert.Contains("SleepStart@0;", result);
        Assert.Contains("SleepEnd@5;", result); // Start 0 + Sleep 5 = 5.
    }
}
