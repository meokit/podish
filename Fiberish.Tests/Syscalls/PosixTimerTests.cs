using System.Buffers.Binary;
using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class PosixTimerTests
{
    // Call private static SyscallHandlers via reflection for testing
    private static void WriteSigEvent(TestEnv env, uint ptr, int notify, int signo = 0, uint value = 0, int tid = 0)
    {
        var buf = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), value);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), signo);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), notify);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), tid);
        env.Engine.CopyToUser(ptr, buf);
    }

    private static void WriteItimerSpec64(TestEnv env, uint ptr, long intervalSec, long intervalNsec, long valueSec,
        long valueNsec)
    {
        var buf = new byte[32];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), intervalSec);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), intervalNsec);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(16, 8), valueSec);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(24, 8), valueNsec);
        env.Engine.CopyToUser(ptr, buf);
    }

    private ValueTask<int> CallSysTimerCreate(TestEnv env, uint clockId, uint sevpPtr, uint timerIdPtr)
    {
        var method = typeof(SyscallManager).GetMethod("SysTimerCreate", BindingFlags.NonPublic | BindingFlags.Instance);
        var previous = env.Engine.CurrentSyscallManager;
        env.Engine.CurrentSyscallManager = env.SyscallManager;
        try
        {
            return (ValueTask<int>)method!.Invoke(env.SyscallManager,
                [env.Engine, clockId, sevpPtr, timerIdPtr, 0u, 0u, 0u])!;
        }
        finally
        {
            env.Engine.CurrentSyscallManager = previous;
        }
    }

    private ValueTask<int> CallSysTimerSetTime64(TestEnv env, uint timerId, uint flags, uint newPtr, uint oldPtr)
    {
        var method =
            typeof(SyscallManager).GetMethod("SysTimerSetTime64", BindingFlags.NonPublic | BindingFlags.Instance);
        var previous = env.Engine.CurrentSyscallManager;
        env.Engine.CurrentSyscallManager = env.SyscallManager;
        try
        {
            return (ValueTask<int>)method!.Invoke(env.SyscallManager,
                [env.Engine, timerId, flags, newPtr, oldPtr, 0u, 0u])!;
        }
        finally
        {
            env.Engine.CurrentSyscallManager = previous;
        }
    }

    private ValueTask<int> CallSysTimerGetTime64(TestEnv env, uint timerId, uint currPtr)
    {
        var method =
            typeof(SyscallManager).GetMethod("SysTimerGetTime64", BindingFlags.NonPublic | BindingFlags.Instance);
        var previous = env.Engine.CurrentSyscallManager;
        env.Engine.CurrentSyscallManager = env.SyscallManager;
        try
        {
            return (ValueTask<int>)method!.Invoke(env.SyscallManager,
                [env.Engine, timerId, currPtr, 0u, 0u, 0u, 0u])!;
        }
        finally
        {
            env.Engine.CurrentSyscallManager = previous;
        }
    }

    private ValueTask<int> CallSysTimerDelete(TestEnv env, uint timerId)
    {
        var method = typeof(SyscallManager).GetMethod("SysTimerDelete", BindingFlags.NonPublic | BindingFlags.Instance);
        return (ValueTask<int>)method!.Invoke(env.SyscallManager, [env.Engine, timerId, 0u, 0u, 0u, 0u, 0u])!;
    }

    [Fact]
    public async Task TimerCreate_Valid_Succeeds()
    {
        using var env = new TestEnv();
        uint timerIdPtr = 0x10000;
        env.MapUserPage(timerIdPtr);

        var result = await CallSysTimerCreate(env, LinuxConstants.CLOCK_REALTIME, 0, timerIdPtr);
        Assert.Equal(0, result);

        var idBuf = new byte[4];
        env.Engine.CopyFromUser(timerIdPtr, idBuf);
        var timerId = BitConverter.ToInt32(idBuf);

        Assert.Equal(0, timerId); // First timer should be ID 0
        Assert.True(env.Process.PosixTimers.ContainsKey(timerId));

        var timer = env.Process.PosixTimers[timerId];
        Assert.Equal(LinuxConstants.CLOCK_REALTIME, timer.ClockId);
    }

    [Fact]
    public async Task TimerSetTime_ParsesValues_Correctly()
    {
        using var env = new TestEnv();
        uint ptrsPage = 0x10000;
        env.MapUserPage(ptrsPage);

        var timerIdPtr = ptrsPage;
        await CallSysTimerCreate(env, LinuxConstants.CLOCK_REALTIME, 0, timerIdPtr);

        var idBuf = new byte[4];
        env.Engine.CopyFromUser(timerIdPtr, idBuf);
        var timerId = (uint)BitConverter.ToInt32(idBuf);

        var valuePtr = ptrsPage + 0x100;
        var valueBuf = new byte[32];

        // set interval to 2 seconds, 500 ms (2000 + 500 = 2500 ms)
        BinaryPrimitives.WriteInt64LittleEndian(valueBuf.AsSpan(0, 8), 2); // intervalSec = 2
        BinaryPrimitives.WriteInt64LittleEndian(valueBuf.AsSpan(8, 8), 500000000); // intervalNsec = 500M

        // set value to 1 sec, 0 nsec (1000 ms)
        BinaryPrimitives.WriteInt64LittleEndian(valueBuf.AsSpan(16, 8), 1); // valueSec = 1
        BinaryPrimitives.WriteInt64LittleEndian(valueBuf.AsSpan(24, 8), 0); // valueNsec = 0
        env.Engine.CopyToUser(valuePtr, valueBuf);

        var result = await CallSysTimerSetTime64(env, timerId, 0, valuePtr, 0);
        Assert.Equal(0, result);

        var timer = env.Process.PosixTimers[(int)timerId];
        Assert.Equal(2500ul, timer.IntervalMs);
        Assert.Equal(1000ul, timer.ValueMs);
        Assert.NotNull(timer.ActiveTimer);
    }

    [Fact]
    public async Task TimerDelete_RemovesTimer_Correctly()
    {
        using var env = new TestEnv();
        uint timerIdPtr = 0x10000;
        env.MapUserPage(timerIdPtr);

        await CallSysTimerCreate(env, LinuxConstants.CLOCK_REALTIME, 0, timerIdPtr);

        var idBuf = new byte[4];
        env.Engine.CopyFromUser(timerIdPtr, idBuf);
        var timerId = (uint)BitConverter.ToInt32(idBuf);

        Assert.True(env.Process.PosixTimers.ContainsKey((int)timerId));

        var result = await CallSysTimerDelete(env, timerId);
        Assert.Equal(0, result);
        Assert.False(env.Process.PosixTimers.ContainsKey((int)timerId));
    }

    [Fact]
    public async Task TimerCreate_SigEvNone_DoesNotQueueSignalOnExpiration()
    {
        using var env = new TestEnv();
        uint page = 0x10000;
        env.MapUserPage(page);

        var sevpPtr = page;
        var timerIdPtr = page + 0x100;
        var valuePtr = page + 0x200;

        WriteSigEvent(env, sevpPtr, LinuxConstants.SIGEV_NONE);

        var createRc = await CallSysTimerCreate(env, LinuxConstants.CLOCK_REALTIME, sevpPtr, timerIdPtr);
        Assert.Equal(0, createRc);

        var idBuf = new byte[4];
        env.Engine.CopyFromUser(timerIdPtr, idBuf);
        var timerId = (uint)BitConverter.ToInt32(idBuf);

        WriteItimerSpec64(env, valuePtr, 0, 0, 1, 0);
        var setRc = await CallSysTimerSetTime64(env, timerId, 0, valuePtr, 0);
        Assert.Equal(0, setRc);

        var timer = env.Process.PosixTimers[(int)timerId];
        Assert.Equal(LinuxConstants.SIGEV_NONE, timer.SigEvent.Notify);
        Assert.NotNull(timer.ActiveTimer?.Callback);

        timer.ActiveTimer!.Callback!();

        Assert.Equal(0UL, env.Task.PendingSignals);
        env.Task.PendingSignalQueue.Lock(q => Assert.Empty(q));
    }

    [Fact]
    public async Task TimerCreate_SigEvThread_IsRejected()
    {
        using var env = new TestEnv();
        uint page = 0x10000;
        env.MapUserPage(page);

        var sevpPtr = page;
        var timerIdPtr = page + 0x100;
        WriteSigEvent(env, sevpPtr, LinuxConstants.SIGEV_THREAD, (int)Signal.SIGALRM, 123);

        var result = await CallSysTimerCreate(env, LinuxConstants.CLOCK_REALTIME, sevpPtr, timerIdPtr);

        Assert.Equal(-(int)Errno.EINVAL, result);
        Assert.Empty(env.Process.PosixTimers);
    }

    [Fact]
    public async Task TimerCreate_SigEvThreadId_TargetsSpecifiedThread()
    {
        using var env = new TestEnv();
        uint page = 0x10000;
        env.MapUserPage(page);

        var sevpPtr = page;
        var timerIdPtr = page + 0x100;
        var valuePtr = page + 0x200;
        WriteSigEvent(env, sevpPtr, LinuxConstants.SIGEV_THREAD_ID, (int)Signal.SIGUSR1, 77u, env.Task.TID);

        var createRc = await CallSysTimerCreate(env, LinuxConstants.CLOCK_REALTIME, sevpPtr, timerIdPtr);
        Assert.Equal(0, createRc);

        var idBuf = new byte[4];
        env.Engine.CopyFromUser(timerIdPtr, idBuf);
        var timerId = (uint)BitConverter.ToInt32(idBuf);

        WriteItimerSpec64(env, valuePtr, 0, 0, 1, 0);
        var setRc = await CallSysTimerSetTime64(env, timerId, 0, valuePtr, 0);
        Assert.Equal(0, setRc);

        env.Task.PendingSignals = 0;
        env.Task.PendingSignalQueue.Lock(q => q.Clear());

        var timer = env.Process.PosixTimers[(int)timerId];
        timer.ActiveTimer!.Callback!();

        Assert.NotEqual(0UL, env.Task.PendingSignals & (1UL << ((int)Signal.SIGUSR1 - 1)));
        env.Task.PendingSignalQueue.Lock(q =>
        {
            var signal = Assert.Single(q);
            Assert.Equal((int)Signal.SIGUSR1, signal.Signo);
            Assert.Equal((ulong)77, signal.Value);
            Assert.Equal((int)timerId, signal.TimerId);
        });
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly TestRuntimeFactory _runtime = new();

        public TestEnv()
        {
            Engine = _runtime.CreateEngine();
            Vma = _runtime.CreateAddressSpace();
            Process = new Process(100, Vma, null!);
            Scheduler = new KernelScheduler();
            Task = new FiberTask(100, Process, Engine, Scheduler);
            Engine.Owner = Task;

            SyscallManager = new SyscallManager(Engine, Vma, 0);
            SyscallManager.MountRootHostfs(".");
            Manager = new PosixTimerManager();

            // Set static current for tests
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public FiberTask Task { get; }
        public Process Process { get; }
        public PosixTimerManager Manager { get; }
        public SyscallManager SyscallManager { get; }
        public KernelScheduler Scheduler { get; }

        public void Dispose()
        {
            GC.KeepAlive(Task);
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }
    }
}