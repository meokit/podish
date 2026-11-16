using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Simd;

public sealed class ConversionExceptionTests
{
    [Theory]
    [InlineData(32, false)]
    [InlineData(32, true)]
    [InlineData(64, false)]
    [InlineData(64, true)]
    public void ScalarIntegerConversion_UsesGuestRoundingDazAndStickyExceptionFlags(int width, bool truncate)
    {
        foreach (ulong bits in Inputs(width))
        for (int rounding = 0; rounding < 4; rounding++)
        foreach (bool daz in new[] { false, true })
        foreach (bool memory in new[] { false, true })
        {
            using var cpu = SimdTestSupport.Create();
            uint initial = 0x1F84u | ((uint)rounding << 13) | (daz ? 0x40u : 0); // Existing ZE must remain sticky.
            cpu.Mxcsr = initial;
            byte[] input = cpu.ReadXmm(1);
            SimdTestSupport.WriteLane(input, 0, width, bits);
            cpu.WriteXmm(1, input);
            cpu[Reg.EAX] = CpuSandbox.DataAddress + 1;
            cpu.WriteMemory(cpu[Reg.EAX], input);
            var expected = Reference(bits, width, truncate ? 3 : rounding, daz);
            byte[] code = { width == 32 ? (byte)0xF3 : (byte)0xF2, 0x0F, truncate ? (byte)0x2C : (byte)0x2D,
                memory ? (byte)0x10 : (byte)0xD1 };
            string detail = $"convert width={width} truncate={truncate} input={bits:X16} RC={rounding} DAZ={daz} memory={memory}";
            SimdTestSupport.Execute(cpu, code, registerDestination: Reg.EDX, registerValue: expected.Value, detail: detail);
            Assert.True(cpu.Mxcsr == (initial | expected.Flags),
                $"bytes={Convert.ToHexString(code)} {detail}; MXCSR expected={initial | expected.Flags:X8} actual={cpu.Mxcsr:X8}");
        }
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(32, true)]
    [InlineData(64, false)]
    [InlineData(64, true)]
    public void PackedIntegerConversion_AccumulatesInvalidAndPrecisionAcrossLanes(int width, bool truncate)
    {
        foreach (bool memory in new[] { false, true })
        {
            using var cpu = SimdTestSupport.Create();
            byte[] input = MixedInput(width);
            cpu.WriteXmm(1, input);
            cpu[Reg.EAX] = CpuSandbox.DataAddress;
            cpu.WriteMemory(CpuSandbox.DataAddress, input);
            cpu.Mxcsr = 0x5F80; // Upward rounding makes the fractional lane +2, unless CVTT.
            byte[] expected = width == 32 ? SimdTestSupport.Lanes(32, 0x80000000, truncate ? 1UL : 2UL, 2, 0xFFFFFFFE)
                : SimdTestSupport.Lanes(32, 0x80000000, truncate ? 1UL : 2UL);
            byte[] code = PackedCode(width, truncate, memory);
            SimdTestSupport.Execute(cpu, code, expected,
                detail: $"packed exceptions width={width} truncate={truncate} memory={memory} source={Convert.ToHexString(input)}");
            Assert.True(cpu.Mxcsr == 0x5FA1,
                $"bytes={Convert.ToHexString(code)} width={width} source={Convert.ToHexString(input)}; IE|PE must be sticky, MXCSR={cpu.Mxcsr:X8}");
        }
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(32, true)]
    [InlineData(64, false)]
    [InlineData(64, true)]
    public void UnmaskedScalarException_FaultsWithoutCommittingDestination(int width, bool invalid)
    {
        using var cpu = SimdTestSupport.Create();
        ulong bits = invalid ? (width == 32 ? 0x7FC12345UL : 0x7FF8123456789ABCUL)
            : (width == 32 ? 0x3FC00000UL : 0x3FF8000000000000UL);
        cpu.WriteXmm(1, SimdTestSupport.Lanes(width, bits));
        uint initial = 0x1F80u & ~(invalid ? 0x80u : 0x1000u); // IM bit 7, PM bit 12, not status bits 0/5.
        cpu.Mxcsr = initial;
        byte[] code = { width == 32 ? (byte)0xF3 : (byte)0xF2, 0x0F, 0x2C, 0xD1 };
        SimdTestSupport.ConversionFault(cpu, code, initial | (invalid ? 1u : 0x20u),
            $"unmasked scalar width={width} input={bits:X16} invalid={invalid}");
    }

    [Theory]
    [InlineData(32, 0x80u)]
    [InlineData(32, 0x1000u)]
    [InlineData(32, 0x1080u)]
    [InlineData(64, 0x80u)]
    [InlineData(64, 0x1000u)]
    [InlineData(64, 0x1080u)]
    public void UnmaskedPackedException_DoesNotCommitAnyLaneAndInvalidHasPriority(int width, uint clearedMasks)
    {
        using var cpu = SimdTestSupport.Create();
        cpu.WriteXmm(1, MixedInput(width));
        uint initial = 0x1F80u & ~clearedMasks;
        cpu.Mxcsr = initial;
        uint flags = (clearedMasks & 0x80) != 0 ? 1u : 0x21u;
        SimdTestSupport.ConversionFault(cpu, PackedCode(width, true, false), initial | flags,
            $"unmasked packed width={width} clearedMasks={clearedMasks:X4}");
    }

    [Fact]
    public void PreexistingUnmaskedStickyFlags_DoNotFaultOnExactConversion()
    {
        using var cpu = SimdTestSupport.Create();
        cpu.WriteXmm(1, SimdTestSupport.Lanes(32, 0x40000000)); // +2 exactly.
        cpu.Mxcsr = (0x1F80u & ~0x1080u) | 0x21;
        uint initial = cpu.Mxcsr;
        SimdTestSupport.Execute(cpu, new byte[] { 0xF3, 0x0F, 0x2C, 0xD1 }, registerDestination: Reg.EDX, registerValue: 2,
            detail: $"exact conversion with old IE|PE unmasked MXCSR={initial:X8}");
        Assert.Equal(initial, cpu.Mxcsr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Int32ToSingle_UsesGuestRoundingAndReportsOnlyInexact(bool packed)
    {
        foreach (int value in new[] { 0, 1, -1, 16777216, 16777217, -16777217, int.MaxValue, int.MinValue })
        for (int rounding = 0; rounding < 4; rounding++)
        {
            using var cpu = SimdTestSupport.Create();
            uint initial = 0x1F81u | ((uint)rounding << 13); // Preserve old IE.
            cpu.Mxcsr = initial;
            cpu[Reg.EAX] = unchecked((uint)value);
            byte[] source = SimdTestSupport.Lanes(32, unchecked((uint)value), unchecked((uint)value), unchecked((uint)value), unchecked((uint)value));
            cpu.WriteXmm(1, source);
            float rounded = (float)value;
            // C# supplies round-nearest; adjacent IEEE values give the directed results independently.
            if (rounding == 1 && (double)rounded > value) rounded = MathF.BitDecrement(rounded);
            if (rounding == 2 && (double)rounded < value) rounded = MathF.BitIncrement(rounded);
            if (rounding == 3 && value > 0 && (double)rounded > value) rounded = MathF.BitDecrement(rounded);
            if (rounding == 3 && value < 0 && (double)rounded < value) rounded = MathF.BitIncrement(rounded);
            bool inexact = (double)rounded != value;
            byte[] expected = cpu.ReadXmm(0);
            for (int lane = 0; lane < (packed ? 4 : 1); lane++)
                SimdTestSupport.WriteLane(expected, lane, 32, BitConverter.SingleToUInt32Bits(rounded));
            byte[] code = packed ? new byte[] { 0x0F, 0x5B, 0xC1 } : new byte[] { 0xF3, 0x0F, 0x2A, 0xC0 };
            string detail = $"int32->single packed={packed} input={value} bits={unchecked((uint)value):X8} RC={rounding}";
            SimdTestSupport.Execute(cpu, code, expected, detail: detail);
            Assert.True(cpu.Mxcsr == (initial | (inexact ? 0x20u : 0)),
                $"bytes={Convert.ToHexString(code)} {detail}; MXCSR={cpu.Mxcsr:X8}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Int32ToSingle_UnmaskedPrecisionPreservesEveryDestinationLane(bool packed)
    {
        using var cpu = SimdTestSupport.Create();
        cpu[Reg.EAX] = 16777217;
        cpu.WriteXmm(1, SimdTestSupport.Lanes(32, 16777217, 1, 2, 3));
        uint initial = 0x1F80u & ~0x1000u;
        cpu.Mxcsr = initial;
        byte[] code = packed ? new byte[] { 0x0F, 0x5B, 0xC1 } : new byte[] { 0xF3, 0x0F, 0x2A, 0xC0 };
        SimdTestSupport.ConversionFault(cpu, code, initial | 0x20,
            $"int32->single precision unmasked packed={packed} input=01000001 width=32");
    }

    private static byte[] MixedInput(int width) => width == 32
        ? SimdTestSupport.Lanes(32, 0x7FC12345, 0x3FC00000, 0x40000000, 0xC0000000)
        : SimdTestSupport.Lanes(64, 0x7FF8123456789ABC, 0x3FF8000000000000);

    private static byte[] PackedCode(int width, bool truncate, bool memory) => SimdTestSupport.Instruction(
        width == 32 ? (truncate ? (byte)0xF3 : (byte)0x66) : (truncate ? (byte)0x66 : (byte)0xF2),
        width == 32 ? (byte)0x5B : (byte)0xE6, memory);

    private static (uint Value, uint Flags) Reference(ulong bits, int width, int rounding, bool daz)
    {
        double value = width == 32 ? BitConverter.UInt32BitsToSingle((uint)bits) : BitConverter.UInt64BitsToDouble(bits);
        bool denormal = width == 32 ? (bits & 0x7F800000) == 0 && (bits & 0x7FFFFF) != 0
            : (bits & 0x7FF0000000000000) == 0 && (bits & 0xFFFFFFFFFFFFF) != 0;
        if (daz && denormal) value = 0;
        double rounded = rounding switch { 0 => Math.Round(value, MidpointRounding.ToEven), 1 => Math.Floor(value),
            2 => Math.Ceiling(value), _ => Math.Truncate(value) };
        if (!double.IsFinite(value) || rounded < int.MinValue || rounded > int.MaxValue) return (0x80000000, 1);
        return (unchecked((uint)(int)rounded), value == rounded ? 0u : 0x20u);
    }

    private static ulong[] Inputs(int width)
    {
        double[] values = { 0, -0.0, 1.5, 2.5, -1.5, -2.5, 2147483647.5, -2147483648.5, double.PositiveInfinity, double.NaN };
        ulong[] finite = values.Select(value => width == 32 ? BitConverter.SingleToUInt32Bits((float)value)
            : BitConverter.DoubleToUInt64Bits(value)).ToArray();
        return finite.Concat(width == 32 ? new ulong[] { 1, 0x80000001, 0x7F812345 }
            : new ulong[] { 1, 0x8000000000000001, 0x7FF0123456789ABC }).ToArray();
    }
}
