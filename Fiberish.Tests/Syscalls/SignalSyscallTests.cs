using System.Buffers.Binary;
using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class SignalSyscallTests
{
    private const uint StackBase = 0x70000000;

    [Fact]
    public async Task Kill_NegativeProcessGroup_IgnoresCallerSessionForExistingGroup()
    {
        using var env = new SignalEnv();

        var result = await InvokeSys(env.CallerSys, env.CallerEngine, "SysKill",
            unchecked((uint)-env.TargetGroupProcess.PGID), (uint)Signal.SIGHUP, 0, 0, 0, 0);

        Assert.Equal(0, result);

        var sigMask = 1UL << ((int)Signal.SIGHUP - 1);
        Assert.True((env.TargetGroupProcess.PendingProcessSignals & sigMask) != 0);
        Assert.True((env.TargetTask.GetVisiblePendingSignals() & sigMask) != 0);
    }

    [Fact]
    public async Task SigPending_LegacyI386WritesOnlyOldSigsetWidth()
    {
        using var env = new SignalEnv();
        const uint pendingPtr = 0x10000;
        env.MapUserPage(pendingPtr);
        env.Write(pendingPtr, new byte[] { 0, 0, 0, 0, 0xAA, 0xBB, 0xCC, 0xDD });

        env.TargetTask.PostSignal((int)Signal.SIGUSR1);
        env.TargetGroupProcess.EnqueueProcessSignal(new SigInfo { Signo = (int)Signal.SIGHUP, Code = 0 });

        var rc = await InvokeSys(env.TargetSys, env.TargetEngine, "SysSigPending", pendingPtr, 0, 0, 0, 0, 0);

        Assert.Equal(0, rc);
        var result = env.Read(pendingPtr, 8);
        Assert.Equal(0x00000201u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0, 4)));
        Assert.Equal(0xDDCCBBAAu, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4, 4)));
    }

    [Fact]
    public async Task RtSigPending_ReportsVisibleThreadAndProcessPendingSet()
    {
        using var env = new SignalEnv();
        const uint pendingPtr = 0x11000;
        env.MapUserPage(pendingPtr);

        env.TargetTask.PostSignal((int)Signal.SIGUSR1);
        env.TargetGroupProcess.EnqueueProcessSignal(new SigInfo { Signo = (int)Signal.SIGRTMIN, Code = 0 });

        var rc = await InvokeSys(env.TargetSys, env.TargetEngine, "SysRtSigPending", pendingPtr, 8, 0, 0, 0, 0);

        Assert.Equal(0, rc);
        var visible = BinaryPrimitives.ReadUInt64LittleEndian(env.Read(pendingPtr, 8));
        Assert.Equal((1UL << ((int)Signal.SIGUSR1 - 1)) | (1UL << ((int)Signal.SIGRTMIN - 1)), visible);
    }

    [Fact]
    public void ProcessPendingSignals_SaResetHand_ResetsDisposition_AndDoesNotAutoBlockSameSignal()
    {
        using var env = new SignalEnv();
        env.MapUserPage(StackBase);
        env.TargetEngine.RegWrite(Reg.ESP, StackBase + LinuxConstants.PageSize - 0x20);
        env.TargetEngine.Eip = 0x401000;

        env.TargetGroupProcess.SignalActions[(int)Signal.SIGINT] = new SigAction
        {
            Handler = 0x40254F50,
            Flags = LinuxConstants.SA_RESETHAND,
            Restorer = 0,
            Mask = 0
        };

        env.TargetTask.PostSignal((int)Signal.SIGINT);
        InvokeTaskMethod(env.TargetTask, "ProcessPendingSignals");

        Assert.False(env.TargetGroupProcess.SignalActions.ContainsKey((int)Signal.SIGINT));
        Assert.Equal(0UL, env.TargetTask.SignalMask & (1UL << ((int)Signal.SIGINT - 1)));
        Assert.Equal(0x40254F50u, env.TargetEngine.Eip);
    }

    [Fact]
    public void DeliverSignalForRestart_SaResetHand_ResetsDisposition_AndDoesNotAutoBlockSameSignal()
    {
        using var env = new SignalEnv();
        env.MapUserPage(StackBase);
        env.TargetEngine.RegWrite(Reg.ESP, StackBase + LinuxConstants.PageSize - 0x20);
        env.TargetEngine.Eip = 0x565BBE9B;

        var action = new SigAction
        {
            Handler = 0x40254F50,
            Flags = LinuxConstants.SA_RESETHAND,
            Restorer = 0,
            Mask = 0
        };
        env.TargetGroupProcess.SignalActions[(int)Signal.SIGINT] = action;

        env.TargetTask.PostSignal((int)Signal.SIGINT);
        InvokeTaskMethod(env.TargetTask, "DeliverSignalForRestart", (int)Signal.SIGINT, action);

        Assert.False(env.TargetGroupProcess.SignalActions.ContainsKey((int)Signal.SIGINT));
        Assert.Equal(0UL, env.TargetTask.SignalMask & (1UL << ((int)Signal.SIGINT - 1)));
        Assert.Equal(0x40254F50u, env.TargetEngine.Eip);
    }

    [Fact]
    public void StopBySignal_SaNoCldStop_SuppressesSigchldNotification_ButKeepsWaitableStop()
    {
        using var env = new SignalEnv();
        env.TargetGroupProcess.PPID = env.CallerProcess.TGID;
        env.CallerProcess.Children.Add(env.TargetGroupProcess.TGID);
        env.CallerProcess.SignalActions[(int)Signal.SIGCHLD] = new SigAction
        {
            Handler = 0x401000,
            Flags = LinuxConstants.SA_NOCLDSTOP,
            Restorer = 0,
            Mask = 0
        };

        InvokeTaskMethod(env.TargetTask, "StopBySignal", (int)Signal.SIGSTOP);

        var sigchldMask = 1UL << ((int)Signal.SIGCHLD - 1);
        Assert.Equal(0UL, env.CallerProcess.PendingProcessSignals & sigchldMask);
        Assert.True(env.TargetGroupProcess.HasWaitableStop);
        Assert.Equal(ProcessState.Stopped, env.TargetGroupProcess.State);
    }

    [Fact]
    public void FinalizeProcessExit_SaNoCldWait_AutoReapsChild_ButStillQueuesSigchldForHandler()
    {
        using var env = new SignalEnv();
        env.TargetGroupProcess.PPID = env.CallerProcess.TGID;
        env.CallerProcess.Children.Add(env.TargetGroupProcess.TGID);
        env.CallerProcess.SignalActions[(int)Signal.SIGCHLD] = new SigAction
        {
            Handler = 0x401000,
            Flags = LinuxConstants.SA_NOCLDWAIT,
            Restorer = 0,
            Mask = 0
        };

        InvokeStaticSysMethod("FinalizeProcessExit", env.TargetTask, 0, false, 0, false);

        var sigchldMask = 1UL << ((int)Signal.SIGCHLD - 1);
        Assert.True((env.CallerProcess.PendingProcessSignals & sigchldMask) != 0);
        Assert.Equal(ProcessState.Dead, env.TargetGroupProcess.State);
        Assert.DoesNotContain(env.TargetGroupProcess.TGID, env.CallerProcess.Children);
        Assert.Null(env.Scheduler.GetProcess(env.TargetGroupProcess.TGID));
    }

    private static ValueTask<int> InvokeSys(SyscallManager sm, Engine engine, string methodName, uint a1, uint a2,
        uint a3, uint a4, uint a5, uint a6)
    {
        var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (ValueTask<int>)method!.Invoke(sm, [engine, a1, a2, a3, a4, a5, a6])!;
    }

    private static void InvokeTaskMethod(FiberTask task, string methodName, params object[] args)
    {
        var method = typeof(FiberTask).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(task, args);
    }

    private static void InvokeStaticSysMethod(string methodName, params object[] args)
    {
        var method = typeof(SyscallManager).GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
        Assert.NotNull(method);
        method!.Invoke(null, args);
    }

    private sealed class SignalEnv : IDisposable
    {
        public SignalEnv()
        {
            Scheduler = new KernelScheduler();
            var runtime = new TestRuntimeFactory();

            CallerEngine = runtime.CreateEngine();
            CallerMem = runtime.CreateAddressSpace();
            CallerSys = new SyscallManager(CallerEngine, CallerMem, 0);
            CallerProcess = new Process(100, CallerMem, CallerSys)
            {
                PGID = 100,
                SID = 100
            };
            Scheduler.RegisterProcess(CallerProcess);
            CallerTask = new FiberTask(100, CallerProcess, CallerEngine, Scheduler);
            CallerEngine.Owner = CallerTask;

            TargetEngine = runtime.CreateEngine();
            TargetMem = runtime.CreateAddressSpace();
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

        public void MapUserPage(uint addr)
        {
            TargetMem.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", TargetEngine);
            Assert.True(TargetMem.HandleFault(addr, true, TargetEngine));
        }

        public void Write(uint addr, ReadOnlySpan<byte> data)
        {
            Assert.True(TargetEngine.CopyToUser(addr, data));
        }

        public byte[] Read(uint addr, int count)
        {
            var buffer = new byte[count];
            Assert.True(TargetEngine.CopyFromUser(addr, buffer));
            return buffer;
        }
    }
}
