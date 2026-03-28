using Fiberish.Core;
using Fiberish.Native;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Core;

public class KernelSchedulerReparentTests
{
    [Fact]
    public void ReparentChildrenToInit_MovesChildrenToInit()
    {
        var scheduler = new KernelScheduler();

        var init = new Process(1, null!, null!) { SID = 1, PGID = 1 };
        var parent = new Process(2, null!, null!) { SID = 2, PGID = 2 };
        var child1 = new Process(3, null!, null!) { PPID = 2, SID = 2, PGID = 3 };
        var child2 = new Process(4, null!, null!) { PPID = 2, SID = 2, PGID = 4 };

        parent.Children.Add(3);
        parent.Children.Add(4);

        scheduler.RegisterProcess(init);
        scheduler.RegisterProcess(parent);
        scheduler.RegisterProcess(child1);
        scheduler.RegisterProcess(child2);
        scheduler.SetInitPid(1);

        var count = scheduler.ReparentChildrenToInit(2);

        Assert.Equal(2, count);
        Assert.Equal(1, child1.PPID);
        Assert.Equal(1, child2.PPID);
        Assert.DoesNotContain(3, parent.Children);
        Assert.DoesNotContain(4, parent.Children);
        Assert.Contains(3, init.Children);
        Assert.Contains(4, init.Children);
    }

    [Fact]
    public void ReparentChildrenToInit_ExitingInit_NoOp()
    {
        var scheduler = new KernelScheduler();
        scheduler.SetInitPid(1);

        var count = scheduler.ReparentChildrenToInit(1);

        Assert.Equal(0, count);
    }

    [Fact]
    public void ReparentChildrenToInit_NewlyOrphanedStoppedGroup_GetsSighupAndSigcont()
    {
        var scheduler = new KernelScheduler();

        var init = new Process(1, null!, null!) { SID = 1, PGID = 1 };
        var parent = new Process(2, null!, null!) { SID = 200, PGID = 2 };
        var child = new Process(3, null!, null!)
        {
            PPID = 2,
            SID = 200,
            PGID = 3,
            State = ProcessState.Stopped
        };
        parent.Children.Add(3);

        scheduler.RegisterProcess(init);
        scheduler.RegisterProcess(parent);
        scheduler.RegisterProcess(child);
        scheduler.SetInitPid(1);

        var childTask = new FiberTask(3, child, new MockEngine(), scheduler);

        var count = scheduler.ReparentChildrenToInit(2);

        Assert.Equal(1, count);
        Assert.Equal(1, child.PPID);
        var hupMask = 1UL << ((int)Signal.SIGHUP - 1);
        var contMask = 1UL << ((int)Signal.SIGCONT - 1);
        Assert.NotEqual(0UL, child.PendingProcessSignals & hupMask);
        Assert.NotEqual(0UL, child.PendingProcessSignals & contMask);
        Assert.NotEqual(0UL, childTask.GetVisiblePendingSignals() & hupMask);
        Assert.NotEqual(0UL, childTask.GetVisiblePendingSignals() & contMask);
    }

    [Fact]
    public void ReparentChildrenToInit_SameSessionParentDoesNotOrphanGroup_NoSighup()
    {
        var scheduler = new KernelScheduler();

        var init = new Process(1, null!, null!) { SID = 200, PGID = 1 };
        var parent = new Process(2, null!, null!) { SID = 200, PGID = 2 };
        var child = new Process(3, null!, null!)
        {
            PPID = 2,
            SID = 200,
            PGID = 3,
            State = ProcessState.Stopped
        };
        parent.Children.Add(3);

        scheduler.RegisterProcess(init);
        scheduler.RegisterProcess(parent);
        scheduler.RegisterProcess(child);
        scheduler.SetInitPid(1);

        var childTask = new FiberTask(3, child, new MockEngine(), scheduler);

        var count = scheduler.ReparentChildrenToInit(2);

        Assert.Equal(1, count);
        Assert.Equal(1, child.PPID);
        var hupMask = 1UL << ((int)Signal.SIGHUP - 1);
        var contMask = 1UL << ((int)Signal.SIGCONT - 1);
        Assert.Equal(0UL, child.PendingProcessSignals & hupMask);
        Assert.Equal(0UL, child.PendingProcessSignals & contMask);
        Assert.Equal(0UL, childTask.GetVisiblePendingSignals() & hupMask);
        Assert.Equal(0UL, childTask.GetVisiblePendingSignals() & contMask);
    }

    [Fact]
    public void TryAutoReapZombie_WhenEngineInitEnabled_ReapsChildOfInit()
    {
        var scheduler = new KernelScheduler();
        var init = new Process(1, null!, null!);
        var child = new Process(2, null!, null!)
        {
            PPID = 1,
            State = ProcessState.Zombie
        };
        init.Children.Add(2);

        scheduler.RegisterProcess(init);
        scheduler.RegisterProcess(child);
        scheduler.SetInitPid(1);
        scheduler.SetEngineInitReaperEnabled(true);

        var reaped = scheduler.TryAutoReapZombie(child);

        Assert.True(reaped);
        Assert.Equal(ProcessState.Dead, child.State);
        Assert.Null(scheduler.GetProcess(2));
        Assert.DoesNotContain(2, init.Children);
    }

    [Fact]
    public void TryAutoReapZombie_WhenDisabled_DoesNotReap()
    {
        var scheduler = new KernelScheduler();
        var init = new Process(1, null!, null!);
        var child = new Process(2, null!, null!)
        {
            PPID = 1,
            State = ProcessState.Zombie
        };
        init.Children.Add(2);

        scheduler.RegisterProcess(init);
        scheduler.RegisterProcess(child);
        scheduler.SetInitPid(1);

        var reaped = scheduler.TryAutoReapZombie(child);

        Assert.False(reaped);
        Assert.Equal(ProcessState.Zombie, child.State);
        Assert.NotNull(scheduler.GetProcess(2));
        Assert.Contains(2, init.Children);
    }

    [Fact]
    public void SignalProcess_InitPid_ForwardsToDirectChildren_WhenEngineInitEnabled()
    {
        var scheduler = new KernelScheduler();

        var init = new Process(1, null!, null!);
        var child = new Process(2, null!, null!) { PPID = 1 };
        init.Children.Add(2);

        scheduler.RegisterProcess(init);
        scheduler.RegisterProcess(child);
        scheduler.SetInitPid(1);
        scheduler.SetEngineInitReaperEnabled(true);

        var childTask = new FiberTask(2, child, new MockEngine(), scheduler);
        Assert.Equal(0UL, childTask.GetVisiblePendingSignals());

        var ok = scheduler.SignalProcess(1, (int)Signal.SIGTERM);

        Assert.True(ok);
        var sigMask = 1UL << ((int)Signal.SIGTERM - 1);
        Assert.NotEqual(0UL, child.PendingProcessSignals & sigMask);
        Assert.NotEqual(0UL, childTask.GetVisiblePendingSignals() & sigMask);
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