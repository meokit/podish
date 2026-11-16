using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Addressing;

public sealed class ModRmSibTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void RegisterIndirect_HandlesEspSibAndEbpZeroDisplacement(int baseRegister)
    {
        using var cpu = AddressingTestSupport.Create();
        cpu[(Reg)baseRegister] = CpuSandbox.DataAddress + 0x100;
        cpu.WriteMemory(CpuSandbox.DataAddress + 0x100, AddressingTestSupport.Word(0x89ABCDEF));
        byte[] code = baseRegister switch
        {
            4 => new byte[] { 0x8B, 0x14, 0x24 }, // mov edx,[esp]
            5 => new byte[] { 0x8B, 0x55, 0x00 }, // mov edx,[ebp+0]
            _ => new byte[] { 0x8B, (byte)(0x10 | baseRegister) }
        };
        AddressingTestSupport.Execute(cpu, code, $"indirect base={(Reg)baseRegister} width=32", (Reg.EDX, 0x89ABCDEF));
    }

    public static IEnumerable<object[]> SibShapes()
    {
        for (int baseRegister = 0; baseRegister < 8; baseRegister++)
        for (int index = 0; index < 8; index++)
        for (int scale = 0; scale < 4; scale++)
            if (index != 4 && index != baseRegister) yield return new object[] { baseRegister, index, scale };
    }

    [Theory]
    [MemberData(nameof(SibShapes))]
    public void Sib_BaseIndexScaleAndNegativeDisp8(int baseRegister, int index, int scale)
    {
        using var cpu = AddressingTestSupport.Create();
        cpu[(Reg)baseRegister] = CpuSandbox.DataAddress + 0x100;
        cpu[(Reg)index] = 7;
        uint address = CpuSandbox.DataAddress + 0x100 + (7u << scale) - 32;
        cpu.WriteMemory(address, AddressingTestSupport.Word(0xA1B2C3D4));
        byte[] code = { 0x8B, 0x54, (byte)(scale << 6 | index << 3 | baseRegister), 0xE0 };
        AddressingTestSupport.Execute(cpu, code,
            $"SIB base={(Reg)baseRegister} index={(Reg)index} scale={1 << scale} disp=-32 width=32 address={address:X8}",
            (Reg.EDX, 0xA1B2C3D4));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Sib_NoBaseAndNoIndexSpecialFields(int scale)
    {
        foreach (int index in Enumerable.Range(0, 8))
        {
            using var cpu = AddressingTestSupport.Create();
            cpu[(Reg)index] = 7;
            uint address = CpuSandbox.DataAddress + 0x100 + (index == 4 ? 0 : 7u << scale);
            cpu.WriteMemory(address, AddressingTestSupport.Word(0xCAFEF00D));
            byte[] code = AddressingTestSupport.WithDisplacement(
                new byte[] { 0x8B, 0x14, (byte)(scale << 6 | index << 3 | 5) }, CpuSandbox.DataAddress + 0x100);
            AddressingTestSupport.Execute(cpu, code,
                $"SIB no base indexField={index} scale={1 << scale} width=32 address={address:X8}", (Reg.EDX, 0xCAFEF00D));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Sib_NoIndexIgnoresEspAndScale(int scale)
    {
        using var cpu = AddressingTestSupport.Create();
        cpu[Reg.EBX] = CpuSandbox.DataAddress + 0x100;
        cpu[Reg.ESP] = 0xFFFFF123;
        cpu.WriteMemory(CpuSandbox.DataAddress + 0xF0, AddressingTestSupport.Word(0x10203040));
        byte[] code = { 0x8B, 0x54, (byte)(scale << 6 | 4 << 3 | 3), 0xF0 };
        AddressingTestSupport.Execute(cpu, code, $"SIB no index scale={1 << scale} disp=-16 width=32", (Reg.EDX, 0x10203040));
    }

    [Fact]
    public void DirectDisp32_HasNoImplicitBaseRegister()
    {
        using var cpu = AddressingTestSupport.Create();
        cpu.WriteMemory(CpuSandbox.DataAddress + 0x123, AddressingTestSupport.Word(0x01234567));
        byte[] code = AddressingTestSupport.WithDisplacement(new byte[] { 0x8B, 0x15 }, CpuSandbox.DataAddress + 0x123);
        AddressingTestSupport.Execute(cpu, code, "ModRM mod=00 rm=101 disp32 width=32", (Reg.EDX, 0x01234567));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disp32AndScaledAddition_WrapAt32Bits(bool lea)
    {
        using var cpu = AddressingTestSupport.Create();
        cpu[Reg.EBX] = 0xFFFFFFF0;
        cpu[Reg.ECX] = (CpuSandbox.DataAddress + 0x30) / 8;
        uint address = unchecked(cpu[Reg.EBX] + cpu[Reg.ECX] * 8 - 16);
        cpu.WriteMemory(address, AddressingTestSupport.Word(0x76543210));
        byte[] code = AddressingTestSupport.WithDisplacement(new byte[] { lea ? (byte)0x8D : (byte)0x8B, 0x94, 0xCB }, 0xFFFFFFF0);
        AddressingTestSupport.Execute(cpu, code, $"32-bit wrap disp=-16 LEA={lea} address={address:X8}",
            (Reg.EDX, lea ? address : 0x76543210));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OperandSizeOverride_LoadAndLeaPreserveHighRegisterHalf(bool lea)
    {
        using var cpu = AddressingTestSupport.Create();
        cpu[Reg.EBX] = CpuSandbox.DataAddress + 0x123;
        cpu[Reg.EDX] = 0xABCDEF01;
        cpu.WriteMemory(cpu[Reg.EBX], new byte[] { 0x80, 0xFE });
        byte[] code = { 0x66, lea ? (byte)0x8D : (byte)0x8B, 0x13 };
        AddressingTestSupport.Execute(cpu, code, $"66 operand width=16 address width=32 LEA={lea}",
            (Reg.EDX, 0xABCD0000u | (lea ? cpu[Reg.EBX] & 0xFFFF : 0xFE80u)));
    }

    [Fact]
    public void OperandSizeOverride_StoreTouchesOnlyTwoBytes()
    {
        using var cpu = AddressingTestSupport.Create();
        cpu[Reg.EBX] = CpuSandbox.DataAddress + 1;
        cpu[Reg.EDX] = 0xABCDFE80;
        cpu.WriteMemory(CpuSandbox.DataAddress, new byte[] { 0x11, 0x22, 0x33, 0x44 });
        byte[] code = { 0x66, 0x89, 0x13 };
        AddressingTestSupport.Execute(cpu, code, "66 mov [ebx],dx width=16");
        AddressingTestSupport.Bytes(new byte[] { 0x11, 0x80, 0xFE, 0x44 }, cpu.ReadMemory(CpuSandbox.DataAddress, 4),
            $"bytes={Convert.ToHexString(code)} width=16");
    }
}
