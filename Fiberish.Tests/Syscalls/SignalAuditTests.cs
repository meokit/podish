using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public partial class SignalSyscallTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignalAudit_FullMaskDoesNotOverwriteCr2(bool realtime)
    {
        using var env = new SignalEnv();
        ConfigureSignalStacks(env);
        InstallOnStackHandlers(env, realtime);
        const ulong savedMask = 0x1000000000000800;
        env.TargetTask.SignalMask = savedMask;
        DeliverTestSignal(env, Signal.SIGUSR1);
        uint frame = env.TargetEngine.RegRead(Reg.ESP);
        Assert.Equal(12u, frame & 15);
        uint context = realtime
            ? BinaryPrimitives.ReadUInt32LittleEndian(env.Read(frame + 12, 4)) + 20
            : frame + 8;
        Assert.Equal(0x800u, BinaryPrimitives.ReadUInt32LittleEndian(env.Read(context + 80, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(env.Read(context + 84, 4)));
        if (realtime)
        {
            Assert.Equal(frame + 164, context);
            // rt_sigreturn derives the context from the actual frame, not puc.
            env.Write(frame + 12, new byte[4]);
        }
        await ReturnTestSignal(env, realtime);
        Assert.Equal(savedMask, env.TargetTask.SignalMask);
    }

    [Theory]
    [InlineData(false, 1, 4)]
    [InlineData(true, 2, 7)]
    public void SignalAudit_PageFaultReportsClassificationAndMachineContext(bool protection, int code, uint error)
    {
        using var env = new SignalEnv();
        ConfigureSignalStacks(env);
        const uint address = 0x42000000;
        if (protection)
        {
            env.MapUserPage(address);
            Assert.Equal(0, env.TargetGroupProcess.Mem.Mprotect(address, 4096, Protection.Read,
                env.TargetEngine, out _));
        }
        env.TargetGroupProcess.SignalActions[(int)Signal.SIGSEGV] = new SigAction
        {
            Handler = 0x401000,
            Flags = LinuxConstants.SA_ONSTACK | LinuxConstants.SA_SIGINFO
        };
        InvokeTaskMethod(env.TargetTask, "HandlePageFault", address, protection);
        InvokeTaskMethod(env.TargetTask, "ProcessPendingSignals");
        uint frame = env.TargetEngine.RegRead(Reg.ESP);
        uint info = BinaryPrimitives.ReadUInt32LittleEndian(env.Read(frame + 8, 4));
        uint context = BinaryPrimitives.ReadUInt32LittleEndian(env.Read(frame + 12, 4)) + 20;
        Assert.Equal(code, BinaryPrimitives.ReadInt32LittleEndian(env.Read(info + 8, 4)));
        Assert.Equal(address, BinaryPrimitives.ReadUInt32LittleEndian(env.Read(info + 12, 4)));
        Assert.Equal(14u, BinaryPrimitives.ReadUInt32LittleEndian(env.Read(context + 48, 4)));
        Assert.Equal(error, BinaryPrimitives.ReadUInt32LittleEndian(env.Read(context + 52, 4)));
        Assert.Equal(address, BinaryPrimitives.ReadUInt32LittleEndian(env.Read(context + 84, 4)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignalAudit_UnwritableSignalStackCannotEnterHandler(bool realtime)
    {
        using var env = new SignalEnv();
        InstallOnStackHandlers(env, realtime);
        env.TargetEngine.RegWrite(Reg.ESP, 0x43001000); // unmapped, no alternate stack
        DeliverTestSignal(env, Signal.SIGUSR1);
        Assert.True(env.TargetTask.Exited);
        Assert.Equal((int)Signal.SIGSEGV, env.TargetGroupProcess.TermSignal);
        Assert.NotEqual(0x401000u, env.TargetEngine.Eip);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignalAudit_BadSigreturnFrameTerminatesInsteadOfResumingRestorer(bool realtime)
    {
        using var env = new SignalEnv();
        env.TargetEngine.RegWrite(Reg.ESP, 0x43001000);
        await CallSignal(env.TargetSys, env.TargetEngine, realtime ? "SysRtSigReturn" : "SysSigReturn");
        Assert.True(env.TargetTask.Exited);
        Assert.Equal((int)Signal.SIGSEGV, env.TargetGroupProcess.TermSignal);
    }
}
