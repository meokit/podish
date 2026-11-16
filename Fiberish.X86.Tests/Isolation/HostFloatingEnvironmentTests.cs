using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Isolation;

public sealed class HostFloatingEnvironmentTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ExecutionRestoresCallingThreadEnvironmentIncludingFaultExits(bool run, bool fault)
    {
        var original = X86Native.DebugGetFloatEnvironment();
        try
        {
            using var cpu = new CpuSandbox();
            cpu.WriteCode(fault ? [0x0F, 0x0B] : [0xD9, 0xEE, 0xD9, 0xFA]); // UD2 or FLDZ; FSQRT
            // Valid but intentionally unusual SoftFloat state; never use arbitrary
            // high MXCSR reserved bits. Keep host rounding nearest so CLR helpers
            // and assertions between native calls need no floating environment.
            var sentinel = (original & 0xFFFF_FFFF_0000_0000UL) | 0x2000_1003UL;
            X86Native.DebugSetFloatEnvironment(sentinel);
            Assert.Equal(sentinel, X86Native.DebugGetFloatEnvironment());
            cpu.Fcw = 0x0B7F;
            cpu.Fsw = 0x0020;
            Assert.Equal(sentinel, X86Native.DebugGetFloatEnvironment());
            if (run) cpu.Run(CpuSandbox.CodeAddress + (fault ? 2u : 4u));
            else cpu.Step();
            Assert.Equal(sentinel, X86Native.DebugGetFloatEnvironment());
            Assert.Equal(fault ? EmuStatus.Fault : run ? EmuStatus.Stopped : EmuStatus.Running, cpu.Status);
        }
        finally
        {
            X86Native.DebugSetFloatEnvironment(original);
        }
    }

    [Fact]
    public void FetchFaultEarlyReturnRestoresCallingThreadEnvironment()
    {
        var original = X86Native.DebugGetFloatEnvironment();
        try
        {
            using var cpu = new CpuSandbox(false);
            var sentinel = (original & 0xFFFF_FFFF_0000_0000UL) | 0x4001_0802UL;
            X86Native.DebugSetFloatEnvironment(sentinel);
            cpu.Eip = CpuSandbox.CodeAddress;
            Assert.Equal(EmuStatus.Fault, cpu.Step());
            Assert.Equal(14, cpu.FaultVector);
            Assert.Equal(CpuSandbox.CodeAddress, cpu.FaultAddress);
            Assert.Equal(sentinel, X86Native.DebugGetFloatEnvironment());
        }
        finally
        {
            X86Native.DebugSetFloatEnvironment(original);
        }
    }
}
