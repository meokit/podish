using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Core;

public class KernelSchedulerSignalRoutingTests
{
    [Fact]
    public void SignalProcess_LeaderGone_FallsBackToAnotherThread()
    {
        var scheduler = new KernelScheduler();
        var leaderEngine = new Engine();
        var vma = new VMAManager();
        var sm = new SyscallManager(leaderEngine, vma, 0);
        var process = new Process(500, vma, sm)
        {
            PGID = 500,
            SID = 500
        };
        scheduler.RegisterProcess(process);

        var leader = new FiberTask(500, process, leaderEngine, scheduler);
        var workerEngine = new Engine();
        var worker = new FiberTask(501, process, workerEngine, scheduler);

        scheduler.DetachTask(leader);

        var delivered = scheduler.SignalProcess(process.TGID, (int)Signal.SIGUSR1);
        Assert.True(delivered);

        var sigMask = 1UL << ((int)Signal.SIGUSR1 - 1);
        Assert.True((worker.PendingSignals & sigMask) != 0);
    }
}