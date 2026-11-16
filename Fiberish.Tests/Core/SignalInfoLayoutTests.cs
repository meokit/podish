using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;
using Xunit;

namespace Fiberish.Tests.Core;

/// <summary>
/// The guest siginfo_t union carries si_addr for fault signals and pid/uid for
/// SI_USER. A fault at address 0 must still be reported as an address, not as a
/// pid payload.
/// </summary>
public sealed class SignalInfoLayoutTests
{
    [Fact]
    public void FaultSignal_WritesSiAddrAtOffset12()
    {
        var buffer = new byte[128];
        FiberTask.WriteSigInfo(buffer, new SigInfo
        {
            Signo = (int)Signal.SIGSEGV,
            Code = 1,
            Addr = 0xDEAD_BEEF,
            HasAddr = true
        });

        Assert.Equal(0xDEAD_BEEFu, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16)));
    }

    [Fact]
    public void FaultSignal_AtAddressZero_IsStillReportedAsAddress()
    {
        var buffer = new byte[128];
        FiberTask.WriteSigInfo(buffer, new SigInfo
        {
            Signo = (int)Signal.SIGSEGV,
            Code = 1,
            Pid = 1234,
            Addr = 0,
            HasAddr = true
        });

        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16)));
    }

    [Fact]
    public void UserSignal_KeepsPidAndUidPayload()
    {
        var buffer = new byte[128];
        FiberTask.WriteSigInfo(buffer, new SigInfo
        {
            Signo = (int)Signal.SIGUSR1,
            Code = 0,
            Pid = 42,
            Uid = 7
        });

        Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(12)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16)));
    }
}
