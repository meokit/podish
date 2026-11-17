using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

public sealed class SpecializedFusionBakingTests
{
    // SHR EAX/[EAX],3; MOV EDX,ESI; MOVZX EBX,byte[EDI+ESI*2]; MOV ECX,[EDI+ESI*2].
    private static byte[] Code(bool memory) => [0xC1, memory ? (byte)0x28 : (byte)0xE8, 3,
        0x89, 0xF2, 0x0F, 0xB6, 0x1C, 0x77, 0x8B, 0x0C, 0x77];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpecializedRegisterAndGenericMemorySequencesKeepExactFusion(bool memory)
    {
        using var cpu = new CpuSandbox();
        cpu[Reg.EAX] = memory ? CpuSandbox.DataAddress + 8 : 0x80;
        cpu[Reg.ESI] = 0;
        cpu[Reg.EDI] = CpuSandbox.DataAddress;
        cpu.WriteMemory(CpuSandbox.DataAddress, BitConverter.GetBytes(0x1234567au));
        cpu.WriteMemory(CpuSandbox.DataAddress + 8, BitConverter.GetBytes(0x80u));
        var code = Code(memory);
        cpu.WriteCode(code);
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(memory ? CpuSandbox.DataAddress + 8 : 0x10u, cpu[Reg.EAX]);
        Assert.Equal(memory ? 0x10u : 0x80u,
            BitConverter.ToUInt32(cpu.ReadMemory(CpuSandbox.DataAddress + 8, 4)));
        Assert.Equal(0u, cpu[Reg.EDX]);
        Assert.Equal(0x7au, cpu[Reg.EBX]);
        Assert.Equal(0x1234567au, cpu[Reg.ECX]);
        AssertHead(cpu, memory);
    }

    [Fact]
    public void FaultInsideSpecializedFusionRestartsAtTheFaultWithoutRepeatingShift()
    {
        using var cpu = new CpuSandbox();
        const uint unmapped = 0x50000000;
        cpu[Reg.EAX] = 0x80;
        cpu[Reg.ESI] = 0;
        cpu[Reg.EDI] = unmapped;
        var code = Code(false);
        cpu.WriteCode(code);
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Fault, cpu.Status);
        Assert.Equal(14, cpu.FaultVector);
        Assert.Equal(CpuSandbox.CodeAddress + 5u, cpu.Eip);
        Assert.Equal(0x10u, cpu[Reg.EAX]);
        AssertHead(cpu, false);
        cpu.MapMemory(unmapped, CpuSandbox.PageSize);
        cpu.WriteMemory(unmapped, BitConverter.GetBytes(0x1234567au));
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(0x10u, cpu[Reg.EAX]);
        Assert.Equal(0x7au, cpu[Reg.EBX]);
        Assert.Equal(0x1234567au, cpu[Reg.ECX]);
    }

    private static unsafe void AssertHead(CpuSandbox cpu, bool memory)
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
            Assert.Contains("SuperOpcode_4_", symbol);
            if (memory) Assert.DoesNotContain("Shr_ModReg", symbol);
            else Assert.Contains("Shr_ModReg", symbol);
            return;
        }
        Assert.Fail("Root block not retained");
    }
}
