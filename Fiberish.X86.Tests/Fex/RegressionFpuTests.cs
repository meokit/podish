using System.Buffers.Binary;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Fex;

// Small architectural cases independent of the imported corpus and its expected data.
public sealed class RegressionFpuTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FscaleTruncatesPositiveAndNegativeExponentsIndependentlyOfRc(int rounding)
    {
        foreach (var negative in new[] { false, true })
        {
            using var cpu = new CpuSandbox();
            cpu.Fcw = (ushort)(0x037F | (rounding << 10));
            cpu.Ftw = 0xFFF0;
            cpu.WriteFpu(0, Ext(0x8000000000000000, 0x4001)); // 4
            cpu.WriteFpu(1, Ext(0xB000000000000000, (ushort)(negative ? 0xC001 : 0x4001))); // +/-5.5
            Step(cpu, 0xD9, 0xFD);
            Assert.Equal(Ext(0x8000000000000000, (ushort)(negative ? 0x3FFC : 0x4006)), cpu.ReadFpu(0)); // 4*2^(-/+5)
            Assert.Equal(0, cpu.Fsw & 0x21); // Discarding exponent fractions is not an inexact conversion exception.
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FpremAndFprem1UseDifferentQuotientRounding(int rounding)
    {
        foreach (var nearest in new[] { false, true })
        {
            using var cpu = new CpuSandbox();
            cpu.Fcw = (ushort)(0x037F | (rounding << 10));
            cpu.Ftw = 0xFFF0;
            cpu.WriteFpu(0, Ext(0xE000000000000000, 0x4001)); // 7
            cpu.WriteFpu(1, Ext(0x8000000000000000, 0x4000)); // 2
            Step(cpu, 0xD9, nearest ? (byte)0xF5 : (byte)0xF8);
            Assert.Equal(Ext(0x8000000000000000, (ushort)(nearest ? 0xBFFF : 0x3FFF)), cpu.ReadFpu(0)); // -1 or +1
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemainderDoesNotRoundItsOperandsThroughDouble(bool nearest)
    {
        using var cpu = new CpuSandbox();
        cpu.Ftw = 0xFFF0;
        cpu.WriteFpu(0, Ext(0x8000000000000003, 0x403E)); // 2^63+3, whose low bits double cannot retain.
        cpu.WriteFpu(1, Ext(0x8000000000000000, 0x4000)); // 2
        Step(cpu, 0xD9, nearest ? (byte)0xF5 : (byte)0xF8);
        Assert.Equal(Ext(0x8000000000000000, (ushort)(nearest ? 0xBFFF : 0x3FFF)), cpu.ReadFpu(0));
    }

    [Fact]
    public void Fldl2eLoadsTheArchitecturalExtendedPrecisionConstant()
    {
        using var cpu = new CpuSandbox();
        Step(cpu, 0xD9, 0xEA);
        // Correctly rounded log2(e), not a binary64 approximation.
        Assert.Equal(Ext(0xB8AA3B295C17F0BC, 0x3FFF), cpu.ReadFpu(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fisttp64IsSse3AndTruncatesThenPops(bool negative)
    {
        using var cpu = new CpuSandbox();
        cpu.Fcw = 0x0B7F; // Round upward: ignored by FISTTP.
        cpu.Ftw = 0xFFFC;
        cpu[Reg.EAX] = CpuSandbox.DataAddress;
        cpu.WriteFpu(0, Ext(0xB000000000000000, (ushort)(negative ? 0xC000 : 0x4000))); // +/-2.75
        Step(cpu, 0xDD, 0x08); // FISTTP qword [EAX], DD /1 (SSE3, not baseline 80387).
        Assert.Equal(negative ? -2L : 2L, BinaryPrimitives.ReadInt64LittleEndian(cpu.ReadMemory(CpuSandbox.DataAddress, 8)));
        Assert.Equal(1, (cpu.Fsw >> 11) & 7);
        Assert.Equal(0xFFFF, cpu.Ftw);
    }

    [Fact]
    public void StackPointerInstructionsRotateLogicalViewWithoutMovingPhysicalDataOrTags()
    {
        using var cpu = new CpuSandbox();
        cpu.Ftw = 0;
        for (var i = 0; i < 8; i++) cpu.WriteFpu(i, Ext(0x8000000000000000UL + (ulong)i, 0x3FFF));
        cpu.Fsw = 0x0200; // C1 must be cleared by both instructions.
        Step(cpu, 0xD9, 0xF6); // FDECSTP: 0 -> 7
        Assert.Equal(7, (cpu.Fsw >> 11) & 7);
        Assert.Equal(0, cpu.Fsw & 0x0200);
        Assert.Equal(Ext(0x8000000000000007, 0x3FFF), cpu.ReadFpu(0));
        Assert.Equal(0, cpu.Ftw);
        cpu.Fsw |= 0x0200;
        Step(cpu, 0xD9, 0xF7); // FINCSTP: 7 -> 0
        Assert.Equal(0, (cpu.Fsw >> 11) & 7);
        Assert.Equal(0, cpu.Fsw & 0x0200);
        for (var i = 0; i < 8; i++) Assert.Equal(Ext(0x8000000000000000UL + (ulong)i, 0x3FFF), cpu.ReadFpu(i));
    }

    [Fact]
    public void FptanOneRoundsTheMathematicalResultToBinary80()
    {
        using var cpu = new CpuSandbox();
        cpu.Ftw = 0xFFFC;
        cpu.WriteFpu(0, Ext(0x8000000000000000, 0x3FFF));
        Step(cpu, 0xD9, 0xF2);
        Assert.Equal(Ext(0x8000000000000000, 0x3FFF), cpu.ReadFpu(0));
        // tan(1) = 1.557407724654902230506974807458..., rounded to 64 bits.
        Assert.Equal(Ext(0xC75922E5F71D2DC5, 0x3FFF), cpu.ReadFpu(1));
    }

    [Theory]
    [InlineData(0xF2)] // FPTAN
    [InlineData(0xFB)] // FSINCOS
    [InlineData(0xFE)] // FSIN
    [InlineData(0xFF)] // FCOS
    public void InfiniteTrigOperandRaisesMaskedInvalidOperation(int opcode)
    {
        using var cpu = new CpuSandbox();
        cpu.Ftw = 0xFFFE; // Special ST0.
        cpu.WriteFpu(0, Ext(0x8000000000000000, 0x7FFF));
        Step(cpu, 0xD9, (byte)opcode);
        Assert.Equal(1, cpu.Fsw & 1);
    }

    [Theory]
    [InlineData(0xF2)]
    [InlineData(0xFB)]
    [InlineData(0xFE)]
    [InlineData(0xFF)]
    public void OutOfRangeFiniteTrigOperandSetsC2WithoutChangingStack(int opcode)
    {
        using var cpu = new CpuSandbox();
        cpu.Ftw = 0xFFFC;
        var input = Ext(0x8000000000000000, 0x403E); // 2^63
        cpu.WriteFpu(0, input);
        Step(cpu, 0xD9, (byte)opcode);
        Assert.Equal(0x0400, cpu.Fsw & 0x3C00); // C2 set; no stack push.
        Assert.Equal(input, cpu.ReadFpu(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacySaveRestoreUsesOperandSizeAndLogicalRegisterOrder(bool operand16)
    {
        using var cpu = new CpuSandbox();
        cpu.Fcw = 0x0B7F;
        cpu.Fsw = 3 << 11;
        cpu.Ftw = 0;
        var registers = Enumerable.Range(0, 8).Select(i => Ext(0x8000000000000000UL + (ulong)i, 0x3FFF)).ToArray();
        for (var i = 0; i < 8; i++) cpu.WriteFpu(i, registers[i]);
        cpu[Reg.EAX] = CpuSandbox.DataAddress;
        Step(cpu, MemoryInstruction(operand16, 0xDD, 0x30)); // FNSAVE [EAX]
        var image = cpu.ReadMemory(CpuSandbox.DataAddress, operand16 ? 94 : 108);
        Assert.Equal(0x0B7F, BinaryPrimitives.ReadUInt16LittleEndian(image));
        Assert.Equal(3 << 11, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(operand16 ? 2 : 4)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(operand16 ? 4 : 8)));
        for (var i = 0; i < 8; i++) Assert.Equal(registers[i], image.AsSpan((operand16 ? 14 : 28) + i * 10, 10).ToArray());
        Assert.Equal(0x037F, cpu.Fcw);
        Assert.Equal(0, cpu.Fsw);
        Assert.Equal(0xFFFF, cpu.Ftw);
        for (var i = 0; i < 8; i++) cpu.WriteFpu(i, new byte[10]);
        Step(cpu, MemoryInstruction(operand16, 0xDD, 0x20)); // FRSTOR [EAX]
        Assert.Equal(0x0B7F, cpu.Fcw);
        Assert.Equal(3, (cpu.Fsw >> 11) & 7);
        Assert.Equal(0, cpu.Ftw);
        for (var i = 0; i < 8; i++) Assert.Equal(registers[i], cpu.ReadFpu(i));
    }

    [Fact]
    public void LegacyRestorePreservesFipUntilDataInstructionAndInitClearsIt()
    {
        using var cpu = new CpuSandbox();
        cpu[Reg.EAX] = CpuSandbox.DataAddress;
        Step(cpu, 0xDD, 0x30); // FNSAVE initial state.
        var restoredFip = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(restoredFip, 0x61626364);
        cpu.WriteMemory(CpuSandbox.DataAddress + 12, restoredFip);
        Step(cpu, 0xDD, 0x20); // FRSTOR
        cpu[Reg.EAX] += 128;
        Step(cpu, 0xD9, 0x30); // FNSTENV must not overwrite restored FIP.
        Assert.Equal(0x61626364U, BinaryPrimitives.ReadUInt32LittleEndian(cpu.ReadMemory(CpuSandbox.DataAddress + 128 + 12, 4)));
        Step(cpu, 0xDB, 0xE3); // FNINIT
        Step(cpu, 0xD9, 0x30);
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(cpu.ReadMemory(CpuSandbox.DataAddress + 128 + 12, 4)));
    }

    [Fact]
    public void EnvironmentRecordsDataInstructionIpOpcodeAndOperandAddress()
    {
        using var cpu = new CpuSandbox();
        var input = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(input, 0x3F800000); // float 1
        cpu.WriteMemory(CpuSandbox.DataAddress, input);
        cpu[Reg.EAX] = CpuSandbox.DataAddress;
        Step(cpu, 0xD9, 0x00); // FLD dword [EAX]
        cpu[Reg.EAX] += 128;
        Step(cpu, 0xD9, 0x30); // FNSTENV
        var env = cpu.ReadMemory(CpuSandbox.DataAddress + 128, 28);
        Assert.Equal(CpuSandbox.CodeAddress, BinaryPrimitives.ReadUInt32LittleEndian(env.AsSpan(12)));
        Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16LittleEndian(env.AsSpan(18)));
        Assert.Equal(CpuSandbox.DataAddress, BinaryPrimitives.ReadUInt32LittleEndian(env.AsSpan(20)));
    }

    [Theory]
    [InlineData(0xF2)] // tan(-0) followed by a push of 1
    [InlineData(0xFE)] // sin(-0)
    public void TrigonometricOddFunctionsPreserveNegativeZero(int opcode)
    {
        using var cpu = new CpuSandbox();
        cpu.Ftw = 0xFFFE;
        cpu.WriteFpu(0, Ext(0, 0x8000));
        Step(cpu, 0xD9, (byte)opcode);
        Assert.Equal(Ext(0, 0x8000), cpu.ReadFpu(opcode == 0xF2 ? 1 : 0));
    }

    [Theory]
    [InlineData(false, false, 0x3FFE)] // +1,+1 -> pi/4
    [InlineData(true, false, 0xBFFE)] // -1,+1 -> -pi/4
    [InlineData(false, true, 0x4000)] // +1,-1 -> 3pi/4
    [InlineData(true, true, 0xC000)] // -1,-1 -> -3pi/4
    public void FpatanPreservesExtendedPrecisionAcrossQuadrants(bool negativeY, bool negativeX, int exponent)
    {
        using var cpu = new CpuSandbox();
        cpu.Ftw = 0xFFF0;
        cpu.WriteFpu(0, Ext(0x8000000000000000, (ushort)(negativeX ? 0xBFFF : 0x3FFF)));
        cpu.WriteFpu(1, Ext(0x8000000000000000, (ushort)(negativeY ? 0xBFFF : 0x3FFF)));
        Step(cpu, 0xD9, 0xF3);
        // Correctly rounded binary80 pi/4 and 3pi/4, independent of the FEX corpus.
        Assert.Equal(Ext(negativeX ? 0x96CBE3F9990E91A8 : 0xC90FDAA22168C235, (ushort)exponent), cpu.ReadFpu(0));
    }

    private static byte[] Ext(ulong significand, ushort signExponent)
    {
        var value = new byte[10];
        BinaryPrimitives.WriteUInt64LittleEndian(value, significand);
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(8), signExponent);
        return value;
    }

    private static byte[] MemoryInstruction(bool operand16, byte opcode, byte modrm) =>
        operand16 ? new[] { (byte)0x66, opcode, modrm } : new[] { opcode, modrm };

    private static void Step(CpuSandbox cpu, params byte[] instruction)
    {
        cpu.WriteCode(instruction);
        Assert.Equal(EmuStatus.Running, cpu.Step());
        Assert.Equal(CpuSandbox.CodeAddress + (uint)instruction.Length, cpu.Eip);
    }
}
