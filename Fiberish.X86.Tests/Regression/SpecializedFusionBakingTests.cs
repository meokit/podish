using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

public sealed class SpecializedFusionBakingTests
{
    [Theory]
    [InlineData(8, true)]
    [InlineData(7, false)]
    public void DefaultPresetFusionRequiresTheBakedShiftImmediate(byte shift, bool baked)
    {
        using var cpu = new CpuSandbox();
        const uint data = CpuSandbox.DataAddress;
        cpu[Reg.EAX] = 0x10000;
        cpu[Reg.EDX] = data;
        cpu.WriteMemory(data, [0x5a]);
        // MOVZX ECX,[EDX]; ADD EDX,1; XOR ECX,EAX; SHR EAX,imm8.
        byte[] code = [0x0F, 0xB6, 0x0A, 0x83, 0xC2, 0x01, 0x31, 0xC1, 0xC1, 0xE8, shift];
        cpu.WriteCode(code);
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(0x10000u >> shift, cpu[Reg.EAX]);
        Assert.Equal(data + 1, cpu[Reg.EDX]);
        Assert.Equal(0x1005au, cpu[Reg.ECX]);
        AssertHead(cpu, "OpMovzx_Byte__OpGroup1_EvIb_Generic__OpXor_EvGv__OpGroup2_EvIb", baked);
    }

    [Fact]
    public void FaultInsideDefaultPresetFusionRestartsAtTheStoreWithoutRepeatingCompletedOps()
    {
        using var cpu = new CpuSandbox();
        const uint data = CpuSandbox.DataAddress;
        const uint dest = data + CpuSandbox.PageSize;
        cpu.MapMemory(dest, CpuSandbox.PageSize);
        cpu[Reg.ESI] = data + 12;
        cpu[Reg.EBX] = dest;
        cpu.WriteMemory(data, BitConverter.GetBytes(0x12345678u));
        cpu.WriteMemory(data + 4, BitConverter.GetBytes(0x11223344u));
        cpu.ProtectMemory(dest, CpuSandbox.PageSize, 1);
        // MOV EAX,[ESI-12]; MOV [EBX+4],EAX; MOV EAX,[ESI-8]; MOV [EBX+8],EAX.
        byte[] code = [0x8B, 0x46, 0xF4, 0x89, 0x43, 0x04, 0x8B, 0x46, 0xF8, 0x89, 0x43, 0x08];
        cpu.WriteCode(code);
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Fault, cpu.Status);
        Assert.Equal(14, cpu.FaultVector);
        Assert.Equal(CpuSandbox.CodeAddress + 3u, cpu.Eip);
        Assert.Equal(dest + 4, cpu.FaultAddress);
        Assert.Equal(0x12345678u, cpu[Reg.EAX]);
        Assert.Equal(0u, BitConverter.ToUInt32(cpu.ReadMemory(dest + 4, 4)));
        AssertHead(cpu, "OpMov_GvEv__OpMov_EvGv__OpMov_GvEv__OpMov_EvGv");
        cpu.ProtectMemory(dest, CpuSandbox.PageSize, 3);
        cpu.WriteMemory(data, BitConverter.GetBytes(0xdeadbeefu)); // Detect replay of the completed load.
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(0x11223344u, cpu[Reg.EAX]);
        Assert.Equal(0x12345678u, BitConverter.ToUInt32(cpu.ReadMemory(dest + 4, 4)));
        Assert.Equal(0x11223344u, BitConverter.ToUInt32(cpu.ReadMemory(dest + 8, 4)));
    }

    private static unsafe void AssertHead(CpuSandbox cpu, string operationSequence, bool baked = true)
    {
        // These handler expectations use the default preset's operand shapes.
        if ((X86Native.GetBuildFeatures() & 1) == 0 ||
            !X86Native.GetOptimizationConfig().Contains("baking_preset=3.5m;")) return;
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
