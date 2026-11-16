using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

/// <summary>
/// Legacy operand/address semantics that differ from the common 32-bit forms:
/// segment-register stores are 16-bit in memory, 0x66 near control transfers
/// move two stack bytes, and the implicit stack uses the SS base.
/// </summary>
public sealed class LegacyAddressingRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovMemoryFromSegmentRegister_WritesExactlyTwoBytes(bool operandSizePrefix)
    {
        using var cpu = new CpuSandbox();
        uint target = CpuSandbox.DataAddress + 0x100;
        cpu.WriteMemory(target, [0xAA, 0xBB, 0xCC, 0xDD]);

        // 8C /r: mov r/m16, Sreg with [EDI] as destination. The operand-size
        // prefix does not widen the memory destination.
        byte[] code = operandSizePrefix ? [0x66, 0x8C, 0x1F] : [0x8C, 0x1F];
        cpu.WriteCode(code);
        cpu[Reg.EDI] = target;
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);

        Assert.Equal(new byte[] { 0x00, 0x00, 0xCC, 0xDD }, cpu.ReadMemory(target, 4));
    }

    [Fact]
    public void MovRegisterFromSegmentRegister_ZeroExtendsSelector()
    {
        using var cpu = new CpuSandbox();
        byte[] code = [0x8C, 0xD8]; // mov eax, ds
        cpu.WriteCode(code);
        cpu[Reg.EAX] = 0xFFFF_FFFF;
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);

        Assert.Equal(0u, cpu[Reg.EAX]);
    }

    [Fact]
    public void Ret16_PopsTwoBytesAndBranchesToWord()
    {
        using var cpu = new CpuSandbox();
        uint oldEsp = cpu[Reg.ESP];
        cpu.WriteMemory(oldEsp, [0x34, 0x12, 0xAA, 0xAA]);
        cpu.WriteCode([0x66, 0xC3]); // retw

        cpu.Step();

        Assert.Equal(oldEsp + 2, cpu[Reg.ESP]);
        Assert.Equal(0x1234u, cpu.Eip);
    }

    [Fact]
    public void Call16_PushesTwoByteReturnAddress()
    {
        using var cpu = new CpuSandbox();
        uint oldEsp = cpu[Reg.ESP];
        byte[] code = [0x66, 0xE8, 0x00, 0x00]; // callw next
        cpu.WriteCode(code);

        cpu.Step();

        Assert.Equal(oldEsp - 2, cpu[Reg.ESP]);
        var returnAddress = BitConverter.ToUInt16(cpu.ReadMemory(oldEsp - 2, 2));
        Assert.Equal((ushort)((CpuSandbox.CodeAddress + 4) & 0xFFFF), returnAddress);
        Assert.Equal((CpuSandbox.CodeAddress + 4) & 0xFFFFu, cpu.Eip);
    }

    [Fact]
    public void Push_UsesStackSegmentBase()
    {
        using var cpu = new CpuSandbox();
        uint ssBase = CpuSandbox.DataAddress;
        X86Native.SegBaseWrite(cpu.State, (int)Seg.SS, ssBase);
        cpu[Reg.ESP] = 0x100;

        // push 0x12345678
        byte[] code = [0x68, 0x78, 0x56, 0x34, 0x12];
        cpu.WriteCode(code);
        cpu.Step();

        Assert.Equal(0xFCu, cpu[Reg.ESP]);
        Assert.Equal(0x1234_5678u, BitConverter.ToUInt32(cpu.ReadMemory(ssBase + 0xFC, 4)));
    }

    [Fact]
    public void RepMovsb_WithAddressSizeOverride_UsesSixteenBitIndexes()
    {
        using var cpu = new CpuSandbox();
        X86Native.SegBaseWrite(cpu.State, (int)Seg.DS, CpuSandbox.DataAddress);
        X86Native.SegBaseWrite(cpu.State, (int)Seg.ES, CpuSandbox.DataAddress);
        cpu.WriteMemory(CpuSandbox.DataAddress + 0x100, [0x77]);

        cpu[Reg.ESI] = 0xDEAD_0100;
        cpu[Reg.EDI] = 0xBEEF_0200;
        cpu[Reg.ECX] = 0xCAFE_0001;

        byte[] code = [0x67, 0xF3, 0xA4]; // addr16; rep movsb
        cpu.WriteCode(code);
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);

        Assert.Equal((byte)0x77, cpu.ReadMemory(CpuSandbox.DataAddress + 0x200, 1)[0]);
        Assert.Equal(0xDEAD_0101u, cpu[Reg.ESI]);
        Assert.Equal(0xBEEF_0201u, cpu[Reg.EDI]);
        Assert.Equal(0xCAFE_0000u, cpu[Reg.ECX]);
    }

    [Fact]
    public void RepStosb_WithAddressSizeOverride_WrapsWithinSixteenBits()
    {
        using var cpu = new CpuSandbox();
        X86Native.SegBaseWrite(cpu.State, (int)Seg.ES, CpuSandbox.DataAddress);
        cpu[Reg.EAX] = 0x5A;
        cpu[Reg.EDI] = 0x1234_0300;
        cpu[Reg.ECX] = 0xAAAA_0002;

        byte[] code = [0x67, 0xF3, 0xAA]; // addr16; rep stosb
        cpu.WriteCode(code);
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);

        Assert.Equal(new byte[] { 0x5A, 0x5A }, cpu.ReadMemory(CpuSandbox.DataAddress + 0x300, 2));
        Assert.Equal(0x1234_0302u, cpu[Reg.EDI]);
        Assert.Equal(0xAAAA_0000u, cpu[Reg.ECX]);
    }

    [Fact]
    public void Lodsb_WithAddressSizeOverride_AdvancesOnlyLowHalf()
    {
        using var cpu = new CpuSandbox();
        X86Native.SegBaseWrite(cpu.State, (int)Seg.DS, CpuSandbox.DataAddress);
        cpu.WriteMemory(CpuSandbox.DataAddress + 0x400, [0x42]);
        cpu[Reg.ESI] = 0x9999_0400;
        cpu[Reg.EAX] = 0xFFFF_FF00;

        byte[] code = [0x67, 0xAC]; // addr16; lodsb
        cpu.WriteCode(code);
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);

        Assert.Equal(0x42u, cpu[Reg.EAX] & 0xFF);
        Assert.Equal(0x9999_0401u, cpu[Reg.ESI]);
    }

    [Fact]
    public void Scasb_WithAddressSizeOverride_ComparesAtEsBase()
    {
        using var cpu = new CpuSandbox();
        X86Native.SegBaseWrite(cpu.State, (int)Seg.ES, CpuSandbox.DataAddress);
        cpu.WriteMemory(CpuSandbox.DataAddress + 0x500, [0x11]);
        cpu[Reg.EDI] = 0x7777_0500;
        cpu[Reg.EAX] = 0x11;

        byte[] code = [0x67, 0xAE]; // addr16; scasb
        cpu.WriteCode(code);
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);

        Assert.Equal(0x7777_0501u, cpu[Reg.EDI]);
        Assert.Equal(0x40u, cpu.Eflags & 0x40); // ZF
    }

    [Fact]
    public void Stosb_WithoutOverride_StillUsesEsBase()
    {
        using var cpu = new CpuSandbox();
        X86Native.SegBaseWrite(cpu.State, (int)Seg.ES, CpuSandbox.DataAddress);
        cpu[Reg.EAX] = 0x5A;
        cpu[Reg.EDI] = 0x100;

        byte[] code = [0xAA]; // stosb
        cpu.WriteCode(code);
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);

        Assert.Equal((byte)0x5A, cpu.ReadMemory(CpuSandbox.DataAddress + 0x100, 1)[0]);
        Assert.Equal(0x101u, cpu[Reg.EDI]);
    }
}
