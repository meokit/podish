using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.X86.Tests.Addressing;

public sealed class StackAddressingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PushEsp_StoresOriginalStackPointerBeforeDecrement(bool operand16)
    {
        using var cpu = AddressingTestSupport.Create();
        uint originalEsp = cpu[Reg.ESP];
        int width = operand16 ? 2 : 4;
        byte[] before = Enumerable.Repeat((byte)0xA5, 12).ToArray();
        cpu.WriteMemory(originalEsp - 8, before);
        byte[] expected = before.ToArray();
        AddressingTestSupport.Word(originalEsp).AsSpan(0, width).CopyTo(expected.AsSpan(8 - width));
        byte[] code = operand16 ? new byte[] { 0x66, 0x54 } : new byte[] { 0x54 };
        AddressingTestSupport.Execute(cpu, code, $"PUSH {(operand16 ? "SP" : "ESP")} width={width * 8} originalESP={originalEsp:X8}",
            (Reg.ESP, originalEsp - (uint)width));
        AddressingTestSupport.Bytes(expected, cpu.ReadMemory(originalEsp - 8, 12),
            $"bytes={Convert.ToHexString(code)} originalESP={originalEsp:X8} width={width * 8}");
    }

    [Fact]
    public void PushMemoryEsp_EvaluatesSourceBeforeStackDecrement()
    {
        using var cpu = AddressingTestSupport.Create();
        uint originalEsp = cpu[Reg.ESP];
        cpu.WriteMemory(originalEsp - 4, AddressingTestSupport.Word(0x11111111));
        cpu.WriteMemory(originalEsp, AddressingTestSupport.Word(0x89ABCDEF));
        byte[] code = { 0xFF, 0x34, 0x24 };
        AddressingTestSupport.Execute(cpu, code, $"PUSH [ESP] width=32 originalESP={originalEsp:X8}", (Reg.ESP, originalEsp - 4));
        AddressingTestSupport.Bytes(AddressingTestSupport.Word(0x89ABCDEF), cpu.ReadMemory(originalEsp - 4, 4),
            "bytes=FF3424 PUSH [ESP] must use pre-decrement source address");
        AddressingTestSupport.Bytes(AddressingTestSupport.Word(0x89ABCDEF), cpu.ReadMemory(originalEsp, 4), "PUSH source unchanged");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PopEsp_LoadedValueWinsOverStackIncrement(bool operand16)
    {
        using var cpu = AddressingTestSupport.Create();
        uint originalEsp = cpu[Reg.ESP];
        cpu.WriteMemory(originalEsp, AddressingTestSupport.Word(0x89ABCDEF));
        uint expected = operand16 ? ((originalEsp + 2) & 0xFFFF0000) | 0xCDEF : 0x89ABCDEF;
        byte[] code = operand16 ? new byte[] { 0x66, 0x5C } : new byte[] { 0x5C };
        AddressingTestSupport.Execute(cpu, code, $"POP {(operand16 ? "SP" : "ESP")} width={(operand16 ? 16 : 32)} originalESP={originalEsp:X8}",
            (Reg.ESP, expected));
        AddressingTestSupport.Bytes(AddressingTestSupport.Word(0x89ABCDEF), cpu.ReadMemory(originalEsp, 4),
            $"bytes={Convert.ToHexString(code)} POP source unchanged");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(-8)]
    public void PopMemoryEsp_EvaluatesDestinationAfterStackIncrement(int displacement)
    {
        using var cpu = AddressingTestSupport.Create();
        uint originalEsp = cpu[Reg.ESP];
        byte[] before = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        AddressingTestSupport.Word(0x12345678).CopyTo(before, 12);
        cpu.WriteMemory(originalEsp - 12, before);
        byte[] expected = before.ToArray();
        AddressingTestSupport.Word(0x12345678).CopyTo(expected, 16 + displacement);
        byte[] code = displacement == 0 ? new byte[] { 0x8F, 0x04, 0x24 }
            : new byte[] { 0x8F, 0x44, 0x24, unchecked((byte)displacement) };
        AddressingTestSupport.Execute(cpu, code, $"POP [ESP{displacement:+#;-#;}] width=32 originalESP={originalEsp:X8}",
            (Reg.ESP, originalEsp + 4));
        AddressingTestSupport.Bytes(expected, cpu.ReadMemory(originalEsp - 12, 32),
            $"bytes={Convert.ToHexString(code)} POP [ESP+{displacement}] destination uses post-increment ESP");
    }
}
