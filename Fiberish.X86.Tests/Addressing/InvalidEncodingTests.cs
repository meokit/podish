using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Addressing;

public sealed class InvalidEncodingTests
{
    public static IEnumerable<object[]> InvalidInstructions()
    {
        yield return new object[] { "UD2", new byte[] { 0x0F, 0x0B } };
        yield return new object[] { "LOCK register ADD", new byte[] { 0xF0, 0x01, 0xC8 } };
        yield return new object[] { "LOCK memory MOV", new byte[] { 0xF0, 0x89, 0x08 } };
        yield return new object[] { "LOCK NOP", new byte[] { 0xF0, 0x90 } };
    }

    [Theory]
    [MemberData(nameof(InvalidInstructions))]
    public void IllegalEncoding_RaisesUdBeforeChangingRegistersOrMemory(string name, byte[] code)
    {
        using var cpu = AddressingTestSupport.Create();
        cpu[Reg.EAX] = CpuSandbox.DataAddress;
        cpu[Reg.ECX] = 0x89ABCDEF;
        byte[] before = AddressingTestSupport.Word(0x12345678);
        cpu.WriteMemory(CpuSandbox.DataAddress, before);
        cpu.WriteCode(code);
        AddressingTestSupport.Fault(cpu, code, 6, null, $"{name} width=32");
        AddressingTestSupport.Bytes(before, cpu.ReadMemory(CpuSandbox.DataAddress, 4),
            $"bytes={Convert.ToHexString(code)} {name} #UD must not store");
    }
}
