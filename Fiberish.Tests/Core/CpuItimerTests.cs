using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.Tests.Syscalls;
using System.Buffers.Binary;
using Xunit;

namespace Fiberish.Tests.Core;

/// <summary>
///     ITIMER_VIRTUAL and ITIMER_PROF must arm, report and expire against the same
///     CPU clock: user time for VIRTUAL, user + system for PROF.
/// </summary>
public class CpuItimerTests
{
    [Theory]
    [InlineData(1U)]
    [InlineData(2U)]
    public void SubMillisecondTimer_RoundsUp_ExpiresAndRearms(uint which)
    {
        using var env = new SyscallTestEnv();
        env.Scheduler.RegisterProcess(env.Process);
        Span<byte> value = stackalloc byte[16];
        value.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(value[4..], 500); // interval
        BinaryPrimitives.WriteInt32LittleEndian(value[12..], 500); // first expiration
        Assert.True(env.Engine.CopyToUser(0x10000, value));
        SetCpu(env.Process, userNs: 250_000, systemNs: 0);
        Assert.Equal(0, env.Invoke("SysSetitimer", which, 0x10000));
        var state = env.Process.CpuItimers[which - 1]!;
        Assert.NotNull(state);
        Assert.Equal(1, state.ValueMs);
        Assert.Equal(1, state.IntervalMs);
        // Round the starting clock up too, so a fractional CPU millisecond cannot
        // make a positive requested duration expire early.
        Assert.Equal(2, state.NextFireCpuMs);
        Assert.Equal(0, env.Invoke("SysGetitimer", which, 0x11000));
        Span<byte> current = stackalloc byte[16];
        Assert.True(env.Engine.CopyFromUser(0x11000, current));
        Assert.Equal(1000, BinaryPrimitives.ReadInt32LittleEndian(current[4..]));
        Assert.True(BinaryPrimitives.ReadInt32LittleEndian(current[12..]) > 0);
        SyscallManager.CheckCpuItimers(env.Process, env.Scheduler, new CpuTimeSnapshot(1_999_999, 0));
        Assert.Equal(2, state.NextFireCpuMs);
        Assert.Equal(0UL, env.Process.PendingProcessSignals);
        SyscallManager.CheckCpuItimers(env.Process, env.Scheduler, new CpuTimeSnapshot(2_000_000, 0));
        Assert.Equal(3, state.NextFireCpuMs);
        var signal = which == 1 ? Signal.SIGVTALRM : Signal.SIGPROF;
        Assert.NotEqual(0UL, env.Process.PendingProcessSignals & (1UL << ((int)signal - 1)));
        Assert.Same(state, env.Process.CpuItimers[which - 1]);
        value.Clear();
        Assert.True(env.Engine.CopyToUser(0x10000, value));
        Assert.Equal(0, env.Invoke("SysSetitimer", which, 0x10000));
        Assert.Null(env.Process.CpuItimers[which - 1]);
    }

    [Fact]
    public void VirtualTimer_ArmsAndExpiresAgainstUserTimeOnly()
    {
        using var context = new MemoryRuntimeContext();
        var process = CreateProcess(context);
        SetCpu(process, userNs: 2_000_000_000L, systemNs: 5_000_000_000L);

        Arm(process, which: 1, valueMs: 100, intervalMs: 0);

        Assert.Equal(2_100, process.CpuItimers[0]!.NextFireCpuMs);

        SetCpu(process, userNs: 2_099_000_000L, systemNs: 5_000_000_000L);
        Check(process, userNs: 2_099_000_000L, systemNs: 5_000_000_000L);
        Assert.NotNull(process.CpuItimers[0]);

        SetCpu(process, userNs: 2_100_000_000L, systemNs: 5_000_000_000L);
        Check(process, userNs: 2_100_000_000L, systemNs: 5_000_000_000L);
        Assert.Null(process.CpuItimers[0]);
    }

    [Fact]
    public void VirtualTimer_GetRemainingMatchesExpiryClock()
    {
        using var context = new MemoryRuntimeContext();
        var process = CreateProcess(context);
        SetCpu(process, userNs: 2_000_000_000L, systemNs: 5_000_000_000L);

        Arm(process, which: 1, valueMs: 100, intervalMs: 0);
        SetCpu(process, userNs: 2_040_000_000L, systemNs: 5_000_000_000L);

        var remaining = ReadRemaining(process, which: 1);
        Assert.Equal(60, remaining);
    }

    [Fact]
    public void ProfTimer_CountsUserAndSystemTime()
    {
        using var context = new MemoryRuntimeContext();
        var process = CreateProcess(context);
        SetCpu(process, userNs: 2_000_000_000L, systemNs: 5_000_000_000L);

        Arm(process, which: 2, valueMs: 100, intervalMs: 0);
        Assert.Equal(7_100, process.CpuItimers[1]!.NextFireCpuMs);

        SetCpu(process, userNs: 2_000_000_000L, systemNs: 5_099_000_000L);
        Check(process, userNs: 2_000_000_000L, systemNs: 5_099_000_000L);
        Assert.NotNull(process.CpuItimers[1]);

        SetCpu(process, userNs: 2_000_000_000L, systemNs: 5_100_000_000L);
        Check(process, userNs: 2_000_000_000L, systemNs: 5_100_000_000L);
        Assert.Null(process.CpuItimers[1]);
    }

    private static Process CreateProcess(MemoryRuntimeContext context)
    {
        return new Process(100, new VMAManager(context), null!);
    }

    private static void SetCpu(Process process, long userNs, long systemNs)
    {
        typeof(Process).GetProperty("ExitedThreadsUserCpuTimeNs")!.SetValue(process, userNs);
        typeof(Process).GetProperty("ExitedThreadsSystemCpuTimeNs")!.SetValue(process, systemNs);
    }

    private static void Arm(Process process, int which, long valueMs, long intervalMs)
    {
        typeof(SyscallManager).GetMethod("ArmCpuItimer", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [process, which, valueMs, intervalMs]);
    }

    private static void Check(Process process, long userNs, long systemNs)
    {
        using var scheduler = new KernelScheduler();
        typeof(SyscallManager).GetMethod("CheckCpuItimers", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [process, scheduler, new CpuTimeSnapshot(userNs, systemNs)]);
    }

    private static long ReadRemaining(Process process, int which)
    {
        using var scheduler = new KernelScheduler();
        var result = ((long IntervalMs, long RemainingMs))typeof(SyscallManager)
            .GetMethod("ReadItimerState", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [process, scheduler, which])!;
        return result.RemainingMs;
    }
}
