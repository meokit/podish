using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Core;

public class CpuAccountingTests
{
    [Fact]
    public void ExitedThreadCpu_AndLiveBucket_AddIndependently()
    {
        using var context = new MemoryRuntimeContext();
        var process = new Process(100, new VMAManager(context), null!);

        process.AccumulateLiveCpuTime(100, 20);
        Assert.Equal(new CpuTimeSnapshot(100, 20), process.GetSelfCpuTimeSnapshot());

        process.AccumulateExitedThreadCpuTime(new CpuTimeSnapshot(40, 5));
        Assert.Equal(new CpuTimeSnapshot(140, 25), process.GetSelfCpuTimeSnapshot());

        process.AccumulateLiveCpuTime(10, 2);
        Assert.Equal(new CpuTimeSnapshot(150, 27), process.GetSelfCpuTimeSnapshot());
    }

    [Fact]
    public void MovingTaskFromLiveToExitedBucket_KeepsTotal()
    {
        using var context = new MemoryRuntimeContext();
        var process = new Process(100, new VMAManager(context), null!);

        process.AccumulateLiveCpuTime(100, 20);
        var task = new CpuTimeSnapshot(40, 5);
        process.RemoveAccumulatedLiveCpuTime(task);
        process.AccumulateExitedThreadCpuTime(task);

        Assert.Equal(new CpuTimeSnapshot(100, 20), process.GetSelfCpuTimeSnapshot());
    }

    [Fact]
    public void RunningTaskLiveDelta_IsIncludedAndThenCommitted()
    {
        using var context = new MemoryRuntimeContext();
        var engine = new Engine(context);
        var memory = new VMAManager(context);
        var syscalls = new SyscallManager(engine, memory, 0);
        var process = new Process(100, memory, syscalls);
        using var scheduler = new KernelScheduler();
        var task = new FiberTask(100, process, engine, scheduler);
        engine.Owner = task;

        task.BeginGuestCpuAccounting();
        process.RunningTask = task;
        Thread.Sleep(2);
        var live = process.GetSelfCpuTimeSnapshot();
        Assert.True(live.UserNs > 0);

        task.EndGuestCpuAccounting();
        process.RunningTask = null;
        var committed = process.GetSelfCpuTimeSnapshot();
        Assert.True(committed.UserNs >= live.UserNs);

        syscalls.Close();
        engine.Dispose();
    }
}
