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
}
