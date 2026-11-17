using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Isolation;

public sealed class LongSequenceFusionTests
{
    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 2)]
    public void LongestMatchAndShorterFallbackPreserveEveryConstituent(int length, int expectedHeadWidth)
    {
        using var cpu = new CpuSandbox();
        byte[] instructions = [0x40, 0x43, 0x42, 0x47, 0x46];
        cpu.WriteCode(instructions.AsSpan(0, length));
        cpu.Run(CpuSandbox.CodeAddress + (uint)length);
        Assert.Equal(CpuSandbox.CodeAddress + (uint)length, cpu.Eip);
        Assert.Equal(1u, cpu[Reg.EAX]); Assert.Equal(1u, cpu[Reg.EBX]);
        Assert.Equal(length >= 3 ? 1u : 0u, cpu[Reg.EDX]);
        Assert.Equal(length >= 4 ? 1u : 0u, cpu[Reg.EDI]);
        Assert.Equal(length >= 5 ? 1u : 0u, cpu[Reg.ESI]);
        AssertHeadWidth(cpu, expectedHeadWidth);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FaultInSecondThirdOrFourthOpNeverExecutesTailOrRepeatsPrefix(int prefixLength)
    {
        using var cpu = new CpuSandbox();
        byte[] increments = [0x40, 0x43, 0x42];
        var code = increments.Take(prefixLength).Concat(new byte[] { 0x8B, 0x0D, 0, 0, 0x50, 0, 0x47 }).ToArray();
        cpu.WriteCode(code);
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Fault, cpu.Status); Assert.Equal(14, cpu.FaultVector);
        Assert.Equal(0x00500000u, cpu.FaultAddress);
        Assert.Equal(CpuSandbox.CodeAddress + (uint)prefixLength, cpu.Eip);
        Assert.Equal(1u, cpu[Reg.EAX]);
        Assert.Equal(prefixLength >= 2 ? 1u : 0u, cpu[Reg.EBX]);
        Assert.Equal(prefixLength >= 3 ? 1u : 0u, cpu[Reg.EDX]);
        Assert.Equal(0u, cpu[Reg.EDI]);
        AssertHeadWidth(cpu, prefixLength == 1 ? 3 : 4);
        cpu.MapMemory(0x00500000, CpuSandbox.PageSize);
        cpu.WriteMemory(0x00500000, [7, 0, 0, 0]);
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(7u, cpu[Reg.ECX]); Assert.Equal(1u, cpu[Reg.EDI]);
        Assert.Equal(1u, cpu[Reg.EAX]);
        Assert.Equal(prefixLength >= 2 ? 1u : 0u, cpu[Reg.EBX]);
        Assert.Equal(prefixLength >= 3 ? 1u : 0u, cpu[Reg.EDX]);
    }

    [Fact]
    public void ConditionalFourthOpKeepsItsBranchArgumentAndFlags()
    {
        using var cpu = new CpuSandbox();
        cpu.WriteCode([0x40, 0x43, 0x42, 0x75, 1, 0x47, 0x46]);
        cpu.Run(CpuSandbox.CodeAddress + 7);
        Assert.Equal(0u, cpu[Reg.EDI]); Assert.Equal(1u, cpu[Reg.ESI]);
        Assert.Equal(1u, cpu[Reg.EAX]); Assert.Equal(1u, cpu[Reg.EBX]); Assert.Equal(1u, cpu[Reg.EDX]);
        AssertHeadWidth(cpu, 4);
    }

    private static unsafe void AssertHeadWidth(CpuSandbox cpu, int width)
    {
#if FIBERISH_EXTENDED_SUPEROPCODE_PROBES
        Assert.Equal(1u, X86Native.GetBuildFeatures() & 1);
        var count = X86Native.GetBlockCount(cpu.State);
        var blocks = new IntPtr[count];
        fixed (IntPtr* pointer = blocks) Assert.Equal(count, X86Native.GetBlockList(cpu.State, pointer, count));
        foreach (var address in blocks)
        {
            var block = (X86Native.BasicBlock*)address;
            if (block->start_eip != CpuSandbox.CodeAddress) continue;
            var symbol = X86Native.GetHandlerSymbolById(X86Native.GetHandlerId(block->entry));
            Assert.Contains($"SuperOpcode_{width}_", symbol);
            return;
        }
        Assert.Fail("Root block was not retained for matching verification");
#endif
    }
}
