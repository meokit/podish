using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class WaitChildStateRegressionTests
{
    [Fact]
    public async Task Wait4_WithStaleSignaledChildEvent_MustBlockUntilNewStateChange()
    {
        using var env = new WaitEnv();
        env.Child.StateChangeEvent.Set(); // stale signaled state from a previous transition

        var pending = InvokeSys("SysWait4", env.ParentEngine.State, (uint)env.Child.TGID, 0, 0, 0, 0, 0).AsTask();
        Assert.False(pending.IsCompleted);

        var drainedBeforeSignal = 0;
        for (var i = 0; i < 5; i++)
            if (env.DrainEvents())
                drainedBeforeSignal++;

        Assert.False(pending.IsCompleted);
        Assert.InRange(drainedBeforeSignal, 0, 1);

        env.Child.State = ProcessState.Zombie;
        env.Child.ExitStatus = 7;
        env.Child.StateChangeEvent.Set();

        for (var i = 0; i < 20 && !pending.IsCompleted; i++)
        {
            env.DrainEvents();
            await Task.Delay(1);
        }

        Assert.True(pending.IsCompleted);
        Assert.Equal(env.Child.TGID, await pending);
    }

    [Fact]
    public async Task WaitId_WithStaleSignaledChildEvent_MustBlockUntilNewStateChange()
    {
        using var env = new WaitEnv();
        env.Child.StateChangeEvent.Set(); // stale signaled state from a previous transition

        var pending = InvokeSys("SysWaitId", env.ParentEngine.State, (uint)IdType.P_PID, (uint)env.Child.TGID, 0, 4, 0, 0)
            .AsTask();
        Assert.False(pending.IsCompleted);

        var drainedBeforeSignal = 0;
        for (var i = 0; i < 5; i++)
            if (env.DrainEvents())
                drainedBeforeSignal++;

        Assert.False(pending.IsCompleted);
        Assert.InRange(drainedBeforeSignal, 0, 1);

        env.Child.State = ProcessState.Zombie;
        env.Child.ExitStatus = 9;
        env.Child.StateChangeEvent.Set();

        for (var i = 0; i < 20 && !pending.IsCompleted; i++)
        {
            env.DrainEvents();
            await Task.Delay(1);
        }

        Assert.True(pending.IsCompleted);
        Assert.Equal(0, await pending);
    }

    private static ValueTask<int> InvokeSys(string methodName, IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (ValueTask<int>)method!.Invoke(null, [state, a1, a2, a3, a4, a5, a6])!;
    }

    private sealed class WaitEnv : IDisposable
    {
        private static readonly MethodInfo DrainEventsMethod =
            typeof(KernelScheduler).GetMethod("DrainEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public WaitEnv()
        {
            Scheduler = new KernelScheduler();
            KernelScheduler.Current = Scheduler;

            ParentEngine = new Engine();
            ParentMem = new VMAManager();
            ParentSys = new SyscallManager(ParentEngine, ParentMem, 0);
            Parent = new Process(600, ParentMem, ParentSys)
            {
                PGID = 600,
                SID = 600
            };
            Scheduler.RegisterProcess(Parent);
            ParentTask = new FiberTask(600, Parent, ParentEngine, Scheduler);
            ParentEngine.Owner = ParentTask;

            ChildEngine = new Engine();
            ChildMem = new VMAManager();
            ChildSys = new SyscallManager(ChildEngine, ChildMem, 0);
            Child = new Process(601, ChildMem, ChildSys)
            {
                PPID = Parent.TGID,
                PGID = Parent.PGID,
                SID = Parent.SID
            };
            Scheduler.RegisterProcess(Child);
            Parent.Children.Add(Child.TGID);

            Scheduler.CurrentTask = ParentTask;
        }

        public KernelScheduler Scheduler { get; }
        public Engine ParentEngine { get; }
        public VMAManager ParentMem { get; }
        public SyscallManager ParentSys { get; }
        public Process Parent { get; }
        public FiberTask ParentTask { get; }
        public Engine ChildEngine { get; }
        public VMAManager ChildMem { get; }
        public SyscallManager ChildSys { get; }
        public Process Child { get; }

        public bool DrainEvents()
        {
            return (bool)DrainEventsMethod.Invoke(Scheduler, null)!;
        }

        public void Dispose()
        {
            Scheduler.CurrentTask = null;
            KernelScheduler.Current = null;
        }
    }
}
