using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class SignalSyscallTests
{
    [Fact]
    public async Task Kill_NegativeProcessGroup_IgnoresCallerSessionForExistingGroup()
    {
        using var env = new SignalEnv();

        var result = await InvokeSys(env.CallerSys, env.CallerEngine, "SysKill",
            unchecked((uint)-env.TargetGroupProcess.PGID), (uint)Signal.SIGHUP, 0, 0, 0, 0);

        Assert.Equal(0, result);

        var sigMask = 1UL << ((int)Signal.SIGHUP - 1);
        Assert.True((env.TargetTask.PendingSignals & sigMask) != 0);
    }

    private static ValueTask<int> InvokeSys(SyscallManager sm, Engine engine, string methodName, uint a1, uint a2,
        uint a3, uint a4, uint a5, uint a6)
    {
        var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (ValueTask<int>)method!.Invoke(sm, [engine, a1, a2, a3, a4, a5, a6])!;
    }

    private sealed class SignalEnv : IDisposable
    {
        public SignalEnv()
        {
            Scheduler = new KernelScheduler();

            CallerEngine = new Engine();
            CallerMem = new VMAManager();
            CallerSys = new SyscallManager(CallerEngine, CallerMem, 0);
            CallerProcess = new Process(100, CallerMem, CallerSys)
            {
                PGID = 100,
                SID = 100
            };
            Scheduler.RegisterProcess(CallerProcess);
            CallerTask = new FiberTask(100, CallerProcess, CallerEngine, Scheduler);
            CallerEngine.Owner = CallerTask;

            TargetEngine = new Engine();
            TargetMem = new VMAManager();
            TargetSys = new SyscallManager(TargetEngine, TargetMem, 0);
            TargetGroupProcess = new Process(200, TargetMem, TargetSys)
            {
                PGID = 5,
                SID = 5
            };
            Scheduler.RegisterProcess(TargetGroupProcess);
            TargetTask = new FiberTask(200, TargetGroupProcess, TargetEngine, Scheduler);
            TargetEngine.Owner = TargetTask;

            Scheduler.CurrentTask = CallerTask;
        }

        public KernelScheduler Scheduler { get; }
        public Engine CallerEngine { get; }
        public VMAManager CallerMem { get; }
        public SyscallManager CallerSys { get; }
        public Process CallerProcess { get; }
        public FiberTask CallerTask { get; }
        public Engine TargetEngine { get; }
        public VMAManager TargetMem { get; }
        public SyscallManager TargetSys { get; }
        public Process TargetGroupProcess { get; }
        public FiberTask TargetTask { get; }

        public void Dispose()
        {
            Scheduler.CurrentTask = null;
        }
    }
}
