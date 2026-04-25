using System.Buffers.Binary;
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

        var pending = InvokeSys(env.ParentSys, env.ParentEngine, "SysWait4", (uint)env.Child.TGID, 0, 0, 0, 0, 0)
            .AsTask();
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

        var pending = InvokeSys(env.ParentSys, env.ParentEngine, "SysWaitId", (uint)IdType.P_PID,
                (uint)env.Child.TGID, 0, 4, 0, 0)
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

    [Fact]
    public async Task Wait4_DefaultIgnoredSignal_MustNotWakeUntilChildChangesState()
    {
        using var env = new WaitEnv();

        var pending = InvokeSys(env.ParentSys, env.ParentEngine, "SysWait4", (uint)env.Child.TGID, 0, 0, 0, 0, 0)
            .AsTask();
        Assert.False(pending.IsCompleted);

        env.ParentTask.PostSignal((int)Signal.SIGWINCH);

        for (var i = 0; i < 5; i++)
        {
            env.DrainEvents();
            await Task.Delay(1);
        }

        Assert.False(pending.IsCompleted);

        env.Child.State = ProcessState.Zombie;
        env.Child.ExitStatus = 11;
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
    public async Task WaitId_DefaultIgnoredSignal_MustNotWakeUntilChildChangesState()
    {
        using var env = new WaitEnv();

        var pending = InvokeSys(env.ParentSys, env.ParentEngine, "SysWaitId", (uint)IdType.P_PID,
                (uint)env.Child.TGID, 0, 4, 0, 0)
            .AsTask();
        Assert.False(pending.IsCompleted);

        env.ParentTask.PostSignal((int)Signal.SIGWINCH);

        for (var i = 0; i < 5; i++)
        {
            env.DrainEvents();
            await Task.Delay(1);
        }

        Assert.False(pending.IsCompleted);

        env.Child.State = ProcessState.Zombie;
        env.Child.ExitStatus = 13;
        env.Child.StateChangeEvent.Set();

        for (var i = 0; i < 20 && !pending.IsCompleted; i++)
        {
            env.DrainEvents();
            await Task.Delay(1);
        }

        Assert.True(pending.IsCompleted);
        Assert.Equal(0, await pending);
    }

    [Fact]
    public async Task Wait4_InterruptingSignal_MustReturnErestartsys()
    {
        using var env = new WaitEnv();

        var pending = InvokeSys(env.ParentSys, env.ParentEngine, "SysWait4", (uint)env.Child.TGID, 0, 0, 0, 0, 0)
            .AsTask();
        Assert.False(pending.IsCompleted);

        env.ParentTask.PostSignal((int)Signal.SIGUSR1);

        for (var i = 0; i < 20 && !pending.IsCompleted; i++)
        {
            env.DrainEvents();
            await Task.Delay(1);
        }

        Assert.True(pending.IsCompleted);
        Assert.Equal(-(int)Errno.ERESTARTSYS, await pending);
    }

    [Fact]
    public async Task Wait4_Wnowait_PreservesChildUsageUntilRealReap()
    {
        using var env = new WaitEnv();
        const uint rusagePtr = 0x10000;
        env.MapParentUserPage(rusagePtr);

        env.Child.AccumulateExitedThreadCpuTime(new CpuTimeSnapshot(30_000_000, 0));
        env.Child.AccumulateChildrenCpuTime(new CpuTimeSnapshot(12_000_000, 0));
        env.Child.State = ProcessState.Zombie;
        env.Child.ExitStatus = 17;
        env.Child.FreezeCpuTimeSnapshot();

        var rc = await InvokeSys(env.ParentSys, env.ParentEngine, "SysWait4", (uint)env.Child.TGID, 0, 0x01000000,
            rusagePtr, 0, 0);
        Assert.Equal(env.Child.TGID, rc);
        Assert.Equal(30_000, env.ReadParentInt32(rusagePtr + 4));
        Assert.Equal(0, env.Parent.ChildrenUserCpuTimeNs);

        rc = await InvokeSys(env.ParentSys, env.ParentEngine, "SysWait4", (uint)env.Child.TGID, 0, 0, rusagePtr, 0, 0);
        Assert.Equal(env.Child.TGID, rc);
        Assert.Equal(42_000_000L, env.Parent.ChildrenUserCpuTimeNs);
    }

    [Fact]
    public async Task WaitId_WritesChildRusage()
    {
        using var env = new WaitEnv();
        const uint rusagePtr = 0x11000;
        env.MapParentUserPage(rusagePtr);

        env.Child.AccumulateExitedThreadCpuTime(new CpuTimeSnapshot(15_000_000, 0));
        env.Child.State = ProcessState.Zombie;
        env.Child.ExitStatus = 19;
        env.Child.FreezeCpuTimeSnapshot();

        var rc = await InvokeSys(env.ParentSys, env.ParentEngine, "SysWaitId", (uint)IdType.P_PID,
            (uint)env.Child.TGID, 0, 4, rusagePtr, 0);

        Assert.Equal(0, rc);
        Assert.Equal(15_000, env.ReadParentInt32(rusagePtr + 4));
        Assert.Equal(15_000_000L, env.Parent.ChildrenUserCpuTimeNs);
    }

    private static ValueTask<int> InvokeSys(SyscallManager sm, Engine engine, string methodName, uint a1, uint a2,
        uint a3, uint a4, uint a5, uint a6)
    {
        var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (ValueTask<int>)method!.Invoke(sm, [engine, a1, a2, a3, a4, a5, a6])!;
    }

    private sealed class WaitEnv : IDisposable
    {
        private static readonly MethodInfo DrainEventsMethod =
            typeof(KernelScheduler).GetMethod("DrainEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private readonly TestRuntimeFactory _parentRuntime = new();
        private readonly TestRuntimeFactory _childRuntime = new();

        public WaitEnv()
        {
            Scheduler = new KernelScheduler();


            ParentEngine = _parentRuntime.CreateEngine();
            ParentMem = _parentRuntime.CreateAddressSpace();
            ParentSys = new SyscallManager(ParentEngine, ParentMem, 0);
            Parent = new Process(600, ParentMem, ParentSys)
            {
                PGID = 600,
                SID = 600
            };
            Scheduler.RegisterProcess(Parent);
            ParentTask = new FiberTask(600, Parent, ParentEngine, Scheduler);
            ParentEngine.Owner = ParentTask;

            ChildEngine = _childRuntime.CreateEngine();
            ChildMem = _childRuntime.CreateAddressSpace();
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

        public void Dispose()
        {
            Scheduler.CurrentTask = null;
        }

        public bool DrainEvents()
        {
            return (bool)DrainEventsMethod.Invoke(Scheduler, null)!;
        }

        public void MapParentUserPage(uint addr)
        {
            ParentMem.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", ParentEngine);
            Assert.True(ParentMem.HandleFault(addr, true, ParentEngine));
        }

        public int ReadParentInt32(uint addr)
        {
            Span<byte> buffer = stackalloc byte[4];
            Assert.True(ParentEngine.CopyFromUser(addr, buffer));
            return BinaryPrimitives.ReadInt32LittleEndian(buffer);
        }
    }
}
