using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

public sealed class OperandFusionGuardTests
{
    [Fact]
    public unsafe void AddedPairCannotConsumeTheHeadOfAnExistingCompareBranchFusion()
    {
        using var cpu = new CpuSandbox();
        cpu[Reg.EBX] = 0x1234;
        cpu[Reg.EAX] = cpu[Reg.EBP] = 7;
        // mov edi,ebx; cmp eax,ebp; jne rel32; inc ecx; inc edx.
        // The MOV/CMP shape is covered, but accepting it would split the base CMP/JNE pair.
        byte[] code = [0x89, 0xDF, 0x39, 0xE8, 0x0F, 0x85, 1, 0, 0, 0, 0x41, 0x42];
        cpu.WriteCode(code);
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(0x1234u, cpu[Reg.EDI]);
        Assert.Equal(1u, cpu[Reg.ECX]);
        Assert.Equal(1u, cpu[Reg.EDX]);
        if (!HasShapedOperands(37)) return;
        int count = X86Native.GetBlockCount(cpu.State);
        var blocks = new IntPtr[count];
        fixed (IntPtr* p = blocks) X86Native.GetBlockList(cpu.State, p, count);
        foreach (var pointer in blocks)
        {
            var block = (X86Native.BasicBlock*)pointer;
            if (block->start_eip != CpuSandbox.CodeAddress) continue;
            string symbol = X86Native.GetHandlerSymbolById(X86Native.GetHandlerId(block->entry))!;
            Assert.DoesNotContain("_OpMov_EvGv_Ebx__OpCmp_EvGv_32_ModReg", symbol);
            return;
        }
        Assert.Fail("Root block was not retained");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void AddedFusionRequiresCoveredOperandsWithoutChangingInstructionSemantics(bool uncovered)
    {
        using var cpu = new CpuSandbox();
        cpu[Reg.EDX] = 4;
        cpu[Reg.ESI] = 3;
        cpu[Reg.EDI] = 7;
        cpu[Reg.EBX] = 10;
        cpu[Reg.EAX] = cpu[Reg.EBP] = 1;
        // imul edx,esi/edi; add ebx,edx; cmp ebp,eax; jne +1; inc ecx; inc edx
        byte[] code = [0x0F, 0xAF, uncovered ? (byte)0xD7 : (byte)0xD6,
            0x01, 0xD3, 0x39, 0xC5, 0x75, 1, 0x41, 0x42];
        cpu.WriteCode(code);
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(uncovered ? 29u : 13u, cpu[Reg.EDX]);
        Assert.Equal(uncovered ? 38u : 22u, cpu[Reg.EBX]);
        Assert.Equal(1u, cpu[Reg.ECX]);
        if (!HasShapedOperands(6)) return;
        int count = X86Native.GetBlockCount(cpu.State);
        var blocks = new IntPtr[count];
        fixed (IntPtr* p = blocks) X86Native.GetBlockList(cpu.State, p, count);
        foreach (var pointer in blocks)
        {
            var block = (X86Native.BasicBlock*)pointer;
            if (block->start_eip != CpuSandbox.CodeAddress) continue;
            string symbol = X86Native.GetHandlerSymbolById(X86Native.GetHandlerId(block->entry))!;
            if (uncovered) Assert.DoesNotContain("SuperOpcode_4_131", symbol);
            else Assert.Contains("OperandClone_005_SuperOpcode_4_131", symbol);
            return;
        }
        Assert.Fail("Root block was not retained");
    }
    private static bool HasShapedOperands(uint minimumBudget)
    {
        var config = X86Native.GetOptimizationConfig();
        return (X86Native.GetBuildFeatures() & 1) != 0 && config.Contains("fusion=SHAPED;") &&
            uint.Parse(config.Split(';').Single(part => part.StartsWith("budget="))[7..]) >= minimumBudget;
    }

}
