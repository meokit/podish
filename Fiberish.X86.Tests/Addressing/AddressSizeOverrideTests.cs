using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.X86.Tests.Addressing;

public sealed class AddressSizeOverrideTests
{
    public static IEnumerable<object[]> Address16Shapes()
    {
        for (int rm = 0; rm < 8; rm++)
        for (int mod = 0; mod < 3; mod++)
        foreach (bool operand16 in new[] { false, true })
            yield return new object[] { rm, mod, operand16 };
    }

    [Theory]
    [MemberData(nameof(Address16Shapes))]
    public void AddressSize67_Uses16BitModRmRegistersDisplacementsAndWrapping(int rm, int mod, bool operand16)
    {
        using var cpu = AddressingTestSupport.Create();
        cpu.MapMemory(0, 0x1000);
        cpu.MapMemory(0x7000, 0x1000); // BP/BX with negative disp8 can land at 0x7FF0.
        cpu.MapMemory(0x8000, 0x1000);
        cpu.MapMemory(0xF000, 0x1000);
        cpu[Reg.EBX] = 0xDEAD8010;
        cpu[Reg.EBP] = 0xBEEF8010;
        cpu[Reg.ESI] = 0xAAAA0010;
        cpu[Reg.EDI] = 0xBBBB0020;
        cpu[Reg.EDX] = 0xABCD1234;
        int effective = rm switch
        {
            0 => 0x8020, 1 => 0x8030, 2 => 0x8020, 3 => 0x8030,
            4 => 0x10, 5 => 0x20, 6 => mod == 0 ? 0x8040 : 0x8010, 7 => 0x8010,
            _ => throw new ArgumentOutOfRangeException(nameof(rm))
        };
        if (mod == 1) effective -= 32;
        if (mod == 2) effective -= 16;
        uint address = (uint)effective & 0xFFFF;
        cpu.WriteMemory(address, new byte[] { 0x80, 0xFE, 0x76, 0x98 });
        var code = new List<byte> { 0x67 };
        if (operand16) code.Add(0x66);
        code.AddRange(new byte[] { 0x8B, (byte)(mod << 6 | 0x10 | rm) });
        if (mod == 0 && rm == 6) code.AddRange(new byte[] { 0x40, 0x80 });
        if (mod == 1) code.Add(0xE0);
        if (mod == 2) code.AddRange(new byte[] { 0xF0, 0xFF });
        AddressingTestSupport.Execute(cpu, code.ToArray(),
            $"67 addressWidth=16 operandWidth={(operand16 ? 16 : 32)} rm={rm} mod={mod} effective={address:X4}",
            (Reg.EDX, operand16 ? 0xABCDFE80u : 0x9876FE80u));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AddressSize67_AdditionWrapsBeforeMemoryOrLea(bool lea, bool operand16)
    {
        using var cpu = AddressingTestSupport.Create();
        cpu.MapMemory(0, 0x1000);
        cpu[Reg.EDX] = 0xCAFEABCD;
        // BX+SI = 0xFFF0+0x0030 modulo 65536 = 0x20.
        cpu[Reg.EBX] = 0xABCDFFF0;
        cpu[Reg.ESI] = 0x12340030;
        cpu.WriteMemory(0x20, AddressingTestSupport.Word(0x89ABCDEF));
        var code = new List<byte> { 0x67 };
        if (operand16) code.Add(0x66);
        code.AddRange(new byte[] { lea ? (byte)0x8D : (byte)0x8B, 0x10 });
        uint result = lea ? 0x20u : 0x89ABCDEF;
        if (operand16) result = 0xCAFE0000 | (result & 0xFFFF);
        AddressingTestSupport.Execute(cpu, code.ToArray(),
            $"67 BX+SI wrap addressWidth=16 operandWidth={(operand16 ? 16 : 32)} lea={lea}", (Reg.EDX, result));
    }

    [Theory]
    [InlineData(0, Seg.DS)] // BX+SI
    [InlineData(2, Seg.SS)] // BP+SI
    [InlineData(3, Seg.SS)] // BP+DI
    [InlineData(7, Seg.DS)] // BX
    public void AddressSize67_DefaultSegmentDependsOnBpBase(int rm, Seg segment)
    {
        using var cpu = AddressingTestSupport.Create();
        cpu[Reg.EBX] = 0xAAAA0100;
        cpu[Reg.EBP] = 0xBBBB0100;
        cpu[Reg.ESI] = 0;
        cpu[Reg.EDI] = 0;
        X86Native.SegBaseWrite(cpu.State, (int)Seg.DS, 0x200000);
        X86Native.SegBaseWrite(cpu.State, (int)Seg.SS, 0x300000);
        cpu.WriteMemory(0x200100, AddressingTestSupport.Word(0xDDDDDDDD));
        cpu.WriteMemory(0x300100, AddressingTestSupport.Word(0x55555555));
        AddressingTestSupport.Execute(cpu, new byte[] { 0x67, 0x8B, (byte)(0x10 | rm) },
            $"67 default segment rm={rm} expected={segment} addressWidth=16 operandWidth=32",
            (Reg.EDX, segment == Seg.DS ? 0xDDDDDDDDu : 0x55555555u));
    }
}
