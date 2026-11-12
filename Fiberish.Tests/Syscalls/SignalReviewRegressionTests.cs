using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public partial class SignalSyscallTests
{
    private const uint SignalBuffer = 0x12000;
    private const uint AltStackBase = 0x71000000;
    private const uint AltStackLength = 0x4000;
    private const int RealtimeSignal = 34;

    [Theory]
    [InlineData(1000, 1999, 1000, 1888, 0)]
    [InlineData(1000, 1999, 1888, 1000, 0)]
    [InlineData(1999, 1000, 1000, 1888, 0)]
    [InlineData(1999, 1000, 1888, 1000, 0)]
    [InlineData(1999, 1998, 1000, 1888, -(int)Errno.EPERM)]
    public async Task Kill_AnyRealOrEffectiveUidMatchingTargetRealOrSavedUid_IsPermitted(
        int uid, int euid, int targetUid, int targetSavedUid, int expected)
    {
        using var env = new SignalEnv();
        env.CallerProcess.UID = uid;
        env.CallerProcess.EUID = euid;
        env.TargetGroupProcess.UID = targetUid;
        env.TargetGroupProcess.SUID = targetSavedUid;

        Assert.Equal(expected, await CallSignal(env.CallerSys, env.CallerEngine, "SysKill", 200, 0));
    }

    [Theory]
    [InlineData("SysKill", 0)]
    [InlineData("SysTkill", -6)]
    [InlineData("SysTgkill", -6)]
    public async Task SignalSend_UsesRealUidAndCorrectSourceCode(string syscall, int code)
    {
        using var env = new SignalEnv();
        env.TargetGroupProcess.UID = 1000;
        env.TargetGroupProcess.EUID = 1001;
        env.TargetTask.SignalMask = SignalBit((int)Signal.SIGUSR1);

        var args = syscall == "SysTgkill"
            ? new uint[] { 200, 200, (uint)Signal.SIGUSR1 }
            : [200, (uint)Signal.SIGUSR1];
        Assert.Equal(0, await CallSignal(env.TargetSys, env.TargetEngine, syscall, args));
        Assert.True(env.TargetTask.TryTakeVisibleSignalForSignalfd(ulong.MaxValue, out var info));
        Assert.Equal(1000u, info.Uid);
        Assert.Equal(200, info.Pid);
        Assert.Equal(code, info.Code);
    }

    [Theory]
    [InlineData("SysRtSigQueueInfo", 0)]
    [InlineData("SysRtSigQueueInfo", -6)]
    [InlineData("SysRtSigQueueInfo", 128)]
    [InlineData("SysRtTgSigQueueInfo", 0)]
    [InlineData("SysRtTgSigQueueInfo", -6)]
    [InlineData("SysRtTgSigQueueInfo", 128)]
    public async Task QueueInfo_SelfAllowsRestrictedSiCodes_ButAnotherTaskDoesNot(string syscall, int code)
    {
        using var env = new SignalEnv();
        WriteQueueInfo(env, code);
        env.TargetTask.SignalMask = SignalBit((int)Signal.SIGUSR1);
        var selfArgs = syscall == "SysRtSigQueueInfo"
            ? new uint[] { 200, (uint)Signal.SIGUSR1, SignalBuffer }
            : [200, 200, (uint)Signal.SIGUSR1, SignalBuffer];
        Assert.Equal(0, await CallSignal(env.TargetSys, env.TargetEngine, syscall, selfArgs));

        var otherArgs = syscall == "SysRtSigQueueInfo"
            ? new uint[] { 100, (uint)Signal.SIGUSR1, SignalBuffer }
            : [100, 100, (uint)Signal.SIGUSR1, SignalBuffer];
        Assert.Equal(-(int)Errno.EPERM,
            await CallSignal(env.TargetSys, env.TargetEngine, syscall, otherArgs));
    }

    [Fact]
    public async Task QueueInfo_UsesSharedPendingQueue_EvenWhenPidIsNonLeaderTid()
    {
        using var env = new SignalEnv();
        WriteQueueInfo(env, -1);
        env.TargetTask.SignalMask = SignalBit((int)Signal.SIGUSR1);
        var peer = await env.TargetTask.Clone((int)(LinuxConstants.CLONE_VM | LinuxConstants.CLONE_THREAD),
            0, 0, 0, 0);

        Assert.Equal(0, await CallSignal(env.TargetSys, env.TargetEngine, "SysRtSigQueueInfo",
            (uint)peer.TID, (uint)Signal.SIGUSR1, SignalBuffer));
        Assert.Equal(0UL, peer.PendingSignals);
        Assert.NotEqual(0UL, env.TargetGroupProcess.PendingProcessSignals);
        Assert.True(env.TargetTask.TryTakeVisibleSignalForSignalfd(ulong.MaxValue, out var info));
        Assert.Equal(777UL, info.Value);
        Assert.Equal(0UL, peer.GetVisiblePendingSignals());
    }

    [Theory]
    [InlineData(ulong.MaxValue)]
    [InlineData(0x8000000000000000UL)]
    public async Task RealtimeSignal_UnsignedLargeLimitsDoNotRejectEmptyQueue(ulong limit)
    {
        using var env = new SignalEnv();
        env.TargetTask.SignalMask = SignalBit(RealtimeSignal);
        Assert.True(env.TargetGroupProcess.TrySetResourceLimit(LinuxConstants.RLIMIT_SIGPENDING,
            new ResourceLimit(limit, limit)));

        Assert.Equal(0, await CallSignal(env.TargetSys, env.TargetEngine, "SysTgkill", 200, 200, RealtimeSignal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealtimeSignal_LimitCountsSameUidAcrossProcesses_AndReleasesOnDequeue(bool threadQueue)
    {
        using var env = new SignalEnv();
        env.CallerProcess.UID = env.TargetGroupProcess.UID = 1000;
        env.TargetTask.SignalMask = SignalBit(RealtimeSignal);
        env.CallerTask.SignalMask = SignalBit(RealtimeSignal);
        var queued = new SigInfo { Signo = RealtimeSignal, Code = -1 };
        if (threadQueue) env.CallerTask.PostSignalInfo(queued);
        else env.CallerProcess.EnqueueProcessSignal(queued);
        Assert.True(env.TargetGroupProcess.TrySetResourceLimit(LinuxConstants.RLIMIT_SIGPENDING,
            new ResourceLimit(1, 1)));

        Assert.Equal(-(int)Errno.EAGAIN, await CallSignal(env.TargetSys, env.TargetEngine,
            "SysTgkill", 200, 200, RealtimeSignal));
        Assert.True(env.CallerTask.TryTakeVisibleSignalForSignalfd(ulong.MaxValue, out _));
        Assert.Equal(0, await CallSignal(env.TargetSys, env.TargetEngine,
            "SysTgkill", 200, 200, RealtimeSignal));
    }

    [Fact]
    public async Task RealtimeSignal_PendingChargeKeepsOriginalUidAfterCredentialChange()
    {
        using var env = new SignalEnv();
        env.CallerProcess.UID = env.TargetGroupProcess.UID = 1000;
        env.CallerProcess.EnqueueProcessSignal(new SigInfo { Signo = RealtimeSignal, Code = -1 });
        env.CallerProcess.UID = 1001;
        env.TargetTask.SignalMask = SignalBit(RealtimeSignal);
        Assert.True(env.TargetGroupProcess.TrySetResourceLimit(LinuxConstants.RLIMIT_SIGPENDING,
            new ResourceLimit(1, 1)));

        Assert.Equal(-(int)Errno.EAGAIN, await CallSignal(env.TargetSys, env.TargetEngine,
            "SysTgkill", 200, 200, RealtimeSignal));
    }

    [Fact]
    public async Task RealtimeSignal_OtherUidDoesNotConsumeLimit()
    {
        using var env = new SignalEnv();
        env.CallerProcess.UID = 1001;
        env.TargetGroupProcess.UID = 1000;
        env.CallerProcess.EnqueueProcessSignal(new SigInfo { Signo = RealtimeSignal, Code = -1 });
        env.TargetTask.SignalMask = SignalBit(RealtimeSignal);
        Assert.True(env.TargetGroupProcess.TrySetResourceLimit(LinuxConstants.RLIMIT_SIGPENDING,
            new ResourceLimit(1, 1)));

        Assert.Equal(0, await CallSignal(env.TargetSys, env.TargetEngine,
            "SysTgkill", 200, 200, RealtimeSignal));
    }

    [Fact]
    public async Task Kill_FullRealtimeQueueStillNotifies_WithoutAllocatingMoreQueueEntries()
    {
        using var env = new SignalEnv();
        env.TargetTask.SignalMask = SignalBit(RealtimeSignal);
        Assert.True(env.TargetGroupProcess.TrySetResourceLimit(LinuxConstants.RLIMIT_SIGPENDING,
            new ResourceLimit(0, 0)));

        for (var i = 0; i < 3; i++)
            Assert.Equal(0, await CallSignal(env.TargetSys, env.TargetEngine, "SysKill", 200, RealtimeSignal));
        Assert.Equal(0, env.TargetGroupProcess.GetPendingSignalCount());
        Assert.True(env.TargetTask.TryTakeVisibleSignalForSignalfd(ulong.MaxValue, out var info));
        Assert.Equal(RealtimeSignal, info.Signo);
        Assert.Equal(0, info.Pid);
        Assert.False(env.TargetTask.TryTakeVisibleSignalForSignalfd(ulong.MaxValue, out _));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(LinuxConstants.SS_AUTODISARM)]
    public async Task SigAltStack_AcceptsLinuxFlags_AndReportsDisabledBeforeInstall(uint flags)
    {
        using var env = new SignalEnv();
        env.MapUserPage(SignalBuffer);
        Assert.Equal(LinuxConstants.SS_DISABLE, await QueryAltStackFlags(env));
        WriteAltStack(env, AltStackBase, flags, AltStackLength);
        Assert.Equal(0, await CallSignal(env.TargetSys, env.TargetEngine, "SysSigAltStack", SignalBuffer));
        Assert.Equal(unchecked((int)(flags & LinuxConstants.SS_AUTODISARM)), await QueryAltStackFlags(env));
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(2u | LinuxConstants.SS_AUTODISARM)]
    public async Task SigAltStack_DisableIgnoresAddressAndSize(uint flags)
    {
        using var env = new SignalEnv();
        env.MapUserPage(SignalBuffer);
        WriteAltStack(env, AltStackBase, flags, AltStackLength);
        Assert.Equal(0, await CallSignal(env.TargetSys, env.TargetEngine, "SysSigAltStack", SignalBuffer));
        Assert.Equal(0u, env.TargetTask.AltStackSp);
        Assert.Equal(0u, env.TargetTask.AltStackSize);
        Assert.Equal(unchecked((int)flags), await QueryAltStackFlags(env));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SigAltStack_RejectsChangesWhileRunningOnStack(bool disable)
    {
        using var env = new SignalEnv();
        env.MapUserPage(SignalBuffer);
        ConfigureSignalStacks(env);
        env.TargetEngine.RegWrite(Reg.ESP, AltStackBase + AltStackLength - 64);
        WriteAltStack(env, AltStackBase, disable ? 2u : 0u, AltStackLength);
        Assert.Equal(-(int)Errno.EPERM,
            await CallSignal(env.TargetSys, env.TargetEngine, "SysSigAltStack", SignalBuffer));
        Assert.Equal(LinuxConstants.SS_ONSTACK, await QueryAltStackFlags(env));
        Assert.Equal(AltStackBase, env.TargetTask.AltStackSp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignalReturn_NestedAltStackKeepsOuterFrameAndOnStackState(bool realtimeFrame)
    {
        using var env = new SignalEnv();
        env.MapUserPage(SignalBuffer);
        ConfigureSignalStacks(env);
        InstallOnStackHandlers(env, realtimeFrame);
        DeliverTestSignal(env, Signal.SIGUSR1);
        var outerSp = env.TargetEngine.RegRead(Reg.ESP);
        var outerFrame = env.Read(outerSp, 128);
        DeliverTestSignal(env, Signal.SIGUSR2);
        Assert.True(env.TargetEngine.RegRead(Reg.ESP) < outerSp);
        await ReturnTestSignal(env, realtimeFrame);
        Assert.Equal(outerSp, env.TargetEngine.RegRead(Reg.ESP));
        Assert.Equal(LinuxConstants.SS_ONSTACK, await QueryAltStackFlags(env));

        DeliverTestSignal(env, Signal.SIGUSR2);
        Assert.True(env.TargetEngine.RegRead(Reg.ESP) < outerSp);
        Assert.Equal(outerFrame, env.Read(outerSp, 128));
        await ReturnTestSignal(env, realtimeFrame);
        await ReturnTestSignal(env, realtimeFrame);
        Assert.Equal(0, await QueryAltStackFlags(env));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignalAutodisarm_DisablesOnEntry_AndRtReturnRestoresSavedStack(bool realtimeFrame)
    {
        using var env = new SignalEnv();
        env.MapUserPage(SignalBuffer);
        ConfigureSignalStacks(env);
        env.TargetTask.AltStackFlags = unchecked((int)LinuxConstants.SS_AUTODISARM);
        InstallOnStackHandlers(env, realtimeFrame);
        DeliverTestSignal(env, Signal.SIGUSR1);
        Assert.Equal(LinuxConstants.SS_DISABLE, await QueryAltStackFlags(env));
        Assert.Equal(0u, env.TargetTask.AltStackSp);
        await ReturnTestSignal(env, realtimeFrame);
        Assert.Equal(realtimeFrame ? unchecked((int)LinuxConstants.SS_AUTODISARM) : LinuxConstants.SS_DISABLE,
            await QueryAltStackFlags(env));
        Assert.Equal(realtimeFrame ? AltStackBase : 0u, env.TargetTask.AltStackSp);
    }

    private static ulong SignalBit(int sig) => 1UL << (sig - 1);

    private static ValueTask<int> CallSignal(SyscallManager manager, Engine engine, string name, params uint[] args)
    {
        var a = new uint[6];
        args.CopyTo(a, 0);
        return InvokeSys(manager, engine, name, a[0], a[1], a[2], a[3], a[4], a[5]);
    }

    private static void WriteQueueInfo(SignalEnv env, int code)
    {
        env.MapUserPage(SignalBuffer);
        var bytes = new byte[128];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), code);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), 777);
        env.Write(SignalBuffer, bytes);
    }

    private static void WriteAltStack(SignalEnv env, uint sp, uint flags, uint size)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, sp);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), size);
        env.Write(SignalBuffer, bytes);
    }

    private static async Task<int> QueryAltStackFlags(SignalEnv env)
    {
        Assert.Equal(0, await CallSignal(env.TargetSys, env.TargetEngine, "SysSigAltStack", 0, SignalBuffer + 128));
        return BinaryPrimitives.ReadInt32LittleEndian(env.Read(SignalBuffer + 132, 4));
    }

    private static void ConfigureSignalStacks(SignalEnv env)
    {
        env.MapUserPage(StackBase);
        for (uint offset = 0; offset < AltStackLength; offset += LinuxConstants.PageSize)
            env.MapUserPage(AltStackBase + offset);
        env.TargetEngine.RegWrite(Reg.ESP, StackBase + LinuxConstants.PageSize - 64);
        env.TargetTask.AltStackSp = AltStackBase;
        env.TargetTask.AltStackSize = AltStackLength;
        env.TargetTask.AltStackFlags = 0;
    }

    private static void InstallOnStackHandlers(SignalEnv env, bool realtimeFrame)
    {
        foreach (var sig in new[] { Signal.SIGUSR1, Signal.SIGUSR2 })
            env.TargetGroupProcess.SignalActions[(int)sig] = new SigAction
            {
                Handler = 0x401000,
                Flags = LinuxConstants.SA_ONSTACK | (realtimeFrame ? LinuxConstants.SA_SIGINFO : 0)
            };
    }

    private static void DeliverTestSignal(SignalEnv env, Signal sig)
    {
        env.TargetTask.PostSignal((int)sig);
        InvokeTaskMethod(env.TargetTask, "ProcessPendingSignals");
    }

    private static async Task ReturnTestSignal(SignalEnv env, bool realtimeFrame)
    {
        var frame = env.TargetEngine.RegRead(Reg.ESP);
        env.TargetEngine.RegWrite(Reg.ESP, frame + (realtimeFrame ? 4u : 8u));
        await CallSignal(env.TargetSys, env.TargetEngine, realtimeFrame ? "SysRtSigReturn" : "SysSigReturn");
    }
}
