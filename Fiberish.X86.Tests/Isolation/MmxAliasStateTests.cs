using System.Buffers.Binary;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Isolation;

public sealed class MmxAliasStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MmxDestinationResetsBothTopViewsMarksAllTagsAndSetsHighWord(bool run)
    {
        for (var top = 0; top < 8; top++)
        {
            using var cpu = Create(top);
            cpu[Reg.EAX] = 0x12345678;
            Execute(cpu, [0x0F, 0x6E, 0xD8], run); // MOVD MM3,EAX
            Assert.Equal(0x4100, cpu.Fsw); // Preserve C0/C3, clear TOP.
            Assert.Equal(0, cpu.Ftw);
            var written = cpu.ReadFpu(3); // Physical MM3 is now logical ST(3).
            Assert.Equal(0x12345678UL, BinaryPrimitives.ReadUInt64LittleEndian(written));
            Assert.Equal(0xFFFF, BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(8)));
            Assert.Equal(0xED7u, cpu.Eflags);
            // The two representations must agree during subsequent x87 use too.
            Execute(cpu, [0xD9, 0xF7], run); // FINCSTP
            Assert.Equal(1, cpu.Fsw >> 11 & 7);
            Assert.Equal(written, cpu.ReadFpu(2));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceOnlyMmxAlsoEntersAliasStateWithoutChangingRegisterPayload(bool run)
    {
        using var cpu = Create(5);
        var logicalMm3 = (3 - 5) & 7;
        var payload = cpu.ReadFpu(logicalMm3);
        Execute(cpu, [0x0F, 0x7E, 0xD8], run); // MOVD EAX,MM3
        Assert.Equal(0x4100, cpu.Fsw);
        Assert.Equal(0, cpu.Ftw);
        Assert.Equal(payload, cpu.ReadFpu(3));
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(payload), cpu[Reg.EAX]);
        Assert.Equal(0xED7u, cpu.Eflags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmmsOnlyEmptiesTagsAndPreservesTopAndPayloads(bool run)
    {
        using var cpu = Create(5);
        var before = cpu.Snapshot();
        Execute(cpu, [0x0F, 0x77], run);
        Assert.Equal(0xFFFF, cpu.Ftw);
        Assert.Equal(before.Fsw, cpu.Fsw);
        Assert.Equal(before.Eflags, cpu.Eflags);
        for (var index = 0; index < 8; index++) Assert.Equal(before.Fpu[index], cpu.ReadFpu(index));
    }

    private static CpuSandbox Create(int top)
    {
        var cpu = new CpuSandbox();
        cpu.Fsw = (ushort)(0x4100 | top << 11);
        cpu.Ftw = 0xFFFF;
        cpu.Eflags = 0xED7;
        for (var logical = 0; logical < 8; logical++)
        {
            var payload = new byte[10];
            BinaryPrimitives.WriteUInt64LittleEndian(payload, 0x8000000011110000UL + (uint)logical);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), 0x3FFF);
            cpu.WriteFpu(logical, payload);
        }
        return cpu;
    }

    private static void Execute(CpuSandbox cpu, byte[] code, bool run)
    {
        cpu.WriteCode(code);
        if (run) cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        else cpu.Step();
        Assert.NotEqual(EmuStatus.Fault, cpu.Status);
        Assert.Equal(CpuSandbox.CodeAddress + (uint)code.Length, cpu.Eip);
    }
}
