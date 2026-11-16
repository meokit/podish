using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Simd;

public sealed class TruncatingConversionTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void ScalarCvtt_HandlesInvalidBoundsAndFractionsRegardlessOfRoundingMode(int width)
    {
        foreach (ulong bits in Inputs(width))
        foreach (uint rounding in new uint[] { 0, 0x2000, 0x4000, 0x6000 })
        foreach (bool memory in new[] { false, true })
        {
            using var cpu = SimdTestSupport.Create();
            byte[] input = cpu.ReadXmm(1);
            SimdTestSupport.WriteLane(input, 0, width, bits);
            cpu.WriteXmm(1, input);
            cpu[Reg.EAX] = CpuSandbox.DataAddress + 1;
            cpu.WriteMemory(cpu[Reg.EAX], input);
            cpu.Mxcsr = 0x1F80 | rounding;
            // CVTTSS2SI/CVTTSD2SI edx,xmm1 or edx,[eax].
            byte[] code = { width == 32 ? (byte)0xF3 : (byte)0xF2, 0x0F, 0x2C, memory ? (byte)0x10 : (byte)0xD1 };
            SimdTestSupport.Execute(cpu, code, registerDestination: Reg.EDX, registerValue: Reference(bits, width),
                detail: $"cvtt width={width} bits={bits:X16} rounding={rounding:X4} memory={memory}");
            Assert.True((cpu.Mxcsr & ~0x3Fu) == (0x1F80u | rounding),
                $"bytes={Convert.ToHexString(code)} width={width} input={bits:X16}; MXCSR control bits={cpu.Mxcsr:X8}");
        }
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void PackedCvtt_ConvertsEachLaneAndDoubleFormClearsUpperQword(int width)
    {
        ulong[] inputs = Inputs(width);
        for (int start = 0; start < inputs.Length; start++)
        foreach (bool memory in new[] { false, true })
        {
            using var cpu = SimdTestSupport.Create();
            var input = new byte[16];
            var expected = new byte[16];
            for (int lane = 0; lane < 128 / width; lane++)
            {
                ulong bits = inputs[(start + lane) % inputs.Length];
                SimdTestSupport.WriteLane(input, lane, width, bits);
                SimdTestSupport.WriteLane(expected, lane, 32, Reference(bits, width));
            }
            cpu.WriteXmm(1, input);
            cpu[Reg.EAX] = CpuSandbox.DataAddress;
            cpu.WriteMemory(CpuSandbox.DataAddress, input);
            cpu.Mxcsr = 0x5F80; // Round upward; CVTT must still truncate toward zero.
            SimdTestSupport.Execute(cpu,
                SimdTestSupport.Instruction(width == 32 ? (byte)0xF3 : (byte)0x66, width == 32 ? (byte)0x5B : (byte)0xE6, memory),
                expected, detail: $"packed cvtt width={width} memory={memory} input={Convert.ToHexString(input)}");
        }
    }

    [Theory]
    [InlineData(32, 0x7FC12345UL)]
    [InlineData(32, 0x7F800000UL)]
    [InlineData(32, 0x4F000000UL)]
    [InlineData(64, 0x7FF8123456789ABCUL)]
    [InlineData(64, 0x7FF0000000000000UL)]
    [InlineData(64, 0x41E0000000000000UL)]
    public void MaskedInvalidScalarCvtt_SetsMxcsrInvalidStickyBit(int width, ulong bits)
    {
        using var cpu = SimdTestSupport.Create();
        cpu.WriteXmm(1, SimdTestSupport.Lanes(width, bits));
        cpu.Mxcsr = 0x1F80;
        byte[] code = { width == 32 ? (byte)0xF3 : (byte)0xF2, 0x0F, 0x2C, 0xD1 };
        SimdTestSupport.Execute(cpu, code, registerDestination: Reg.EDX, registerValue: 0x80000000,
            detail: $"masked invalid width={width} bits={bits:X16}");
        Assert.True((cpu.Mxcsr & 1) != 0,
            $"bytes={Convert.ToHexString(code)} width={width} input={bits:X16}; masked invalid must set MXCSR.IE, actual={cpu.Mxcsr:X8}");
    }

    private static uint Reference(ulong bits, int width)
    {
        double value = width == 32 ? BitConverter.UInt32BitsToSingle((uint)bits) : BitConverter.UInt64BitsToDouble(bits);
        double truncated = Math.Truncate(value);
        return !double.IsFinite(value) || truncated < int.MinValue || truncated > int.MaxValue
            ? 0x80000000 : unchecked((uint)(int)truncated);
    }

    private static ulong[] Inputs(int width) => width == 32
        ? new ulong[]
        {
            0, 0x80000000, 0x3FF33333, 0xBFF33333, 0x3F000000, 0xBF000000,
            0x4EFFFFFF, 0x4F000000, 0xCF000000, 0xCF000001,
            0x7F800000, 0xFF800000, 0x7FC12345, 0x7F812345
        }
        : new ulong[]
        {
            0, 0x8000000000000000, 0x3FFE666666666666, 0xBFFE666666666666,
            BitConverter.DoubleToUInt64Bits(2147483647.75), BitConverter.DoubleToUInt64Bits(2147483648.0),
            BitConverter.DoubleToUInt64Bits(-2147483648.0), BitConverter.DoubleToUInt64Bits(-2147483648.75),
            BitConverter.DoubleToUInt64Bits(-2147483649.0), 0x7FF0000000000000, 0xFFF0000000000000,
            0x7FF8123456789ABC, 0x7FF0123456789ABC
        };
}
