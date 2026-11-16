using System.Buffers.Binary;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

public sealed class AsciiAdjustTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AaaAas_PropagateLowByteCarryOrBorrowThroughAx(bool subtract, bool run)
    {
        foreach (uint high in new uint[] { 0, 0x7F, 0xFF })
        for (uint low = 0; low < 256; low++)
        for (uint incoming = 0; incoming < 4; incoming++)
        {
            using var cpu = new CpuSandbox();
            var initialFlags = 0xEC6u | ((incoming & 1) != 0 ? 1u : 0u) | ((incoming & 2) != 0 ? 0x10u : 0u);
            var ax = (high << 8) | low;
            var adjust = (low & 15) > 9 || (incoming & 2) != 0;
            var mathematical = subtract ? (long)ax - 0x106 : (long)ax + 0x106;
            var expectedAx = (adjust ? unchecked((uint)mathematical) : ax) & 0xFF0F;
            cpu[Reg.EAX] = 0xA5C30000 | ax;
            cpu.Eflags = initialFlags;
            cpu.WriteCode([subtract ? (byte)0x3F : (byte)0x37]);
            if (run) cpu.Run(CpuSandbox.CodeAddress + 1);
            else cpu.Step();
            var context = $"{(subtract ? "AAS" : "AAA")} AX={ax:X4} flags={initialFlags:X8} run={run}";
            Assert.True(cpu.Status != EmuStatus.Fault, context);
            Assert.True(cpu[Reg.EAX] == (0xA5C30000 | expectedAx), context + $" actual EAX={cpu[Reg.EAX]:X8}");
            const uint compared = ~0x8C4u; // SF/ZF/PF/OF are undefined; AF/CF and control flags are checked.
            var expectedFlags = (initialFlags & ~0x11u) | (adjust ? 0x11u : 0u);
            Assert.True((cpu.Eflags & compared) == (expectedFlags & compared), context + $" actual flags={cpu.Eflags:X8}");
        }
    }
}

public sealed class LegacyStackAndRplTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PushWordImmediate_UsesTwoBytesAndSignExtendsByteEncoding(bool run)
    {
        foreach (var byteEncoding in new[] { false, true })
        foreach (var immediate in new ushort[] { 0, 1, 0x7F, 0x80, 0xFF, 0x8000, 0xFFFF })
        {
            if (byteEncoding && immediate > 255) continue;
            using var cpu = new CpuSandbox();
            var oldSp = cpu[Reg.ESP];
            cpu.WriteMemory(oldSp - 4, [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
            var code = byteEncoding ? new byte[] { 0x66, 0x6A, (byte)immediate }
                : new byte[] { 0x66, 0x68, (byte)immediate, (byte)(immediate >> 8) };
            cpu.WriteCode(code);
            if (run) cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
            else cpu.Step();
            Assert.NotEqual(EmuStatus.Fault, cpu.Status);
            Assert.Equal(oldSp - 2, cpu[Reg.ESP]);
            var expected = byteEncoding ? unchecked((ushort)(short)(sbyte)immediate) : immediate;
            var memory = cpu.ReadMemory(oldSp - 4, 6);
            Assert.Equal(new byte[] { 0xAA, 0xBB }, memory[..2]);
            Assert.Equal(expected, BinaryPrimitives.ReadUInt16LittleEndian(memory.AsSpan(2)));
            Assert.Equal(new byte[] { 0xEE, 0xFF }, memory[4..]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Arpl_ChangesOnlyDestinationRplAndZeroFlag(bool run)
    {
        for (uint destination = 0; destination < 4; destination++)
        for (uint source = 0; source < 4; source++)
        {
            using var cpu = new CpuSandbox();
            cpu[Reg.EAX] = 0xA5C3FFF0 | destination;
            cpu[Reg.EBX] = 0xDEADABC0 | source;
            cpu.Eflags = 0xED7;
            cpu.WriteCode([0x63, 0xD8]); // ARPL AX,BX, always 16-bit in IA-32.
            if (run) cpu.Run(CpuSandbox.CodeAddress + 2);
            else cpu.Step();
            Assert.NotEqual(EmuStatus.Fault, cpu.Status);
            Assert.Equal(0xA5C3FFF0 | Math.Max(source, destination), cpu[Reg.EAX]);
            Assert.Equal(0xDEADABC0 | source, cpu[Reg.EBX]);
            Assert.Equal((0xED7u & ~0x40u) | (destination < source ? 0x40u : 0), cpu.Eflags);
        }
    }
}
