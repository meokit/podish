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
    private ValueTask<int> CallSysTimerCreate(TestEnv env, uint clockId, uint sevpPtr, uint timerIdPtr)
    {
        var method = typeof(SyscallManager).GetMethod("SysTimerCreate", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null,
            [env.Engine.State, clockId, sevpPtr, timerIdPtr, 0u, 0u, 0u])!;
    }

    private ValueTask<int> CallSysTimerSetTime64(TestEnv env, uint timerId, uint flags, uint newPtr, uint oldPtr)
    {
        var method =
            typeof(SyscallManager).GetMethod("SysTimerSetTime64", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null,
            [env.Engine.State, timerId, flags, newPtr, oldPtr, 0u, 0u])!;
    }

    private ValueTask<int> CallSysTimerGetTime64(TestEnv env, uint timerId, uint currPtr)
    {
        var method =
            typeof(SyscallManager).GetMethod("SysTimerGetTime64", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null,
            [env.Engine.State, timerId, currPtr, 0u, 0u, 0u, 0u])!;
    }

    private ValueTask<int> CallSysTimerDelete(TestEnv env, uint timerId)
    {
        var method = typeof(SyscallManager).GetMethod("SysTimerDelete", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, timerId, 0u, 0u, 0u, 0u, 0u])!;
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

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
            Process = new Process(100, Vma, null!);
            Scheduler = new KernelScheduler();
            Task = new FiberTask(100, Process, Engine, Scheduler);
            Engine.Owner = Task;

            SyscallManager = new SyscallManager(Engine, Vma, 0);
            SyscallManager.MountRootHostfs(".");
            Manager = new PosixTimerManager();

            KernelScheduler.Current = Scheduler; // Set static current for tests
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
            KernelScheduler.Current = null;
            GC.KeepAlive(Task);
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, LinuxConstants.PageSize, "[test]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }
    }
}