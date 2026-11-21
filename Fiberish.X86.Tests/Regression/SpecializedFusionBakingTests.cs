using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

public sealed class SpecializedFusionBakingTests
{
    [Theory]
    [InlineData(8, true)]
    [InlineData(7, false)]
    public void StockFusionRequiresTheBakedShiftImmediate(byte shift, bool baked)
    {
        using var cpu = new CpuSandbox();
        cpu[Reg.EAX] = 0x10000;
        cpu[Reg.EDX] = 41;
        cpu[Reg.ESI] = 0x5a;
        // INC EDX; XOR ESI,EAX; SHR EAX,imm8; MOV ECX,ESI.
        byte[] code = [0x42, 0x31, 0xC6, 0xC1, 0xE8, shift, 0x89, 0xF1];
        cpu.WriteCode(code);
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(0x10000u >> shift, cpu[Reg.EAX]);
        Assert.Equal(42u, cpu[Reg.EDX]);
        Assert.Equal(0x1005au, cpu[Reg.ECX]);
        AssertHead(cpu, "OpInc_Reg__OpXor_EvGv__OpGroup2_EvIb__OpMov_EvGv", baked);
    }

    [Fact]
    public void FaultInsideStockFusionRestartsAtTheStoreWithoutRepeatingCompletedOps()
    {
        using var cpu = new CpuSandbox();
        const uint data = CpuSandbox.DataAddress;
        cpu[Reg.ESI] = data;
        cpu[Reg.ECX] = 0x11223344;
        cpu.WriteMemory(data, BitConverter.GetBytes(0x12345678u));
        cpu.ProtectMemory(data, CpuSandbox.PageSize, 1);
        // MOV EDX,[ESI]; MOV [ESI],ECX; MOV ECX,ESI; TEST EDX,EDX.
        byte[] code = [0x8B, 0x16, 0x89, 0x0E, 0x89, 0xF1, 0x85, 0xD2];
        cpu.WriteCode(code);
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Fault, cpu.Status);
        Assert.Equal(14, cpu.FaultVector);
        Assert.Equal(CpuSandbox.CodeAddress + 2u, cpu.Eip);
        Assert.Equal(data, cpu.FaultAddress);
        Assert.Equal(0x12345678u, cpu[Reg.EDX]);
        Assert.Equal(0x12345678u, BitConverter.ToUInt32(cpu.ReadMemory(data, 4)));
        AssertHead(cpu, "OpMov_GvEv__OpMov_EvGv__OpMov_EvGv__OpTest_EvGv");
        cpu.ProtectMemory(data, CpuSandbox.PageSize, 3);
        cpu.WriteMemory(data, BitConverter.GetBytes(0xdeadbeefu)); // Detect replay of the completed load.
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(0x12345678u, cpu[Reg.EDX]);
        Assert.Equal(data, cpu[Reg.ECX]);
        Assert.Equal(0x11223344u, BitConverter.ToUInt32(cpu.ReadMemory(data, 4)));
    }

    private static unsafe void AssertHead(CpuSandbox cpu, string operationSequence, bool baked = true)
    {
        // The functional and fault checks also run in the CI configuration
        // that disables superopcodes; only the handler assertion needs fusion.
        if ((X86Native.GetBuildFeatures() & 1) == 0) return;
        int count = X86Native.GetBlockCount(cpu.State);
        var blocks = new IntPtr[count];
        fixed (IntPtr* p = blocks) X86Native.GetBlockList(cpu.State, p, count);
        foreach (IntPtr p in blocks)
        {
            var b = (X86Native.BasicBlock*)p;
            if (b->start_eip != CpuSandbox.CodeAddress) continue;
            string symbol = X86Native.GetHandlerSymbolById(X86Native.GetHandlerId(b->entry))!;
            if (baked)
                Assert.Contains(operationSequence, symbol);
            else
                Assert.DoesNotContain(operationSequence, symbol);
            return;
        }
        Assert.Fail("Root block not retained");
    }
}
