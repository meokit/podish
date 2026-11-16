using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Simd;

public sealed class FloatingArithmeticTests
{
    [Theory]
    [InlineData(32, false)]
    [InlineData(32, true)]
    [InlineData(64, false)]
    [InlineData(64, true)]
    public void FiniteExactArithmetic_PreservesScalarUpperLanesAndFlags(int width, bool scalar)
    {
        foreach (byte opcode in new byte[] { 0x58, 0x5C, 0x59, 0x5E, 0x51 })
        foreach (bool memory in new[] { false, true })
        {
            using var cpu = SimdTestSupport.Create();
            double[] left = { 6, -6, 0.5, -0.0 };
            double[] right = opcode == 0x51 ? new double[] { 4, 9, 0.25, 0 } : new double[] { 2, 2, -2, 4 };
            byte[] a = cpu.ReadXmm(0), b = cpu.ReadXmm(1);
            int lanes = scalar ? 1 : 128 / width;
            for (int lane = 0; lane < lanes; lane++)
            {
                SimdTestSupport.WriteLane(a, lane, width, Bits(left[lane], width));
                SimdTestSupport.WriteLane(b, lane, width, Bits(right[lane], width));
            }
            byte[] expected = a.ToArray();
            for (int lane = 0; lane < lanes; lane++)
            {
                double result = width == 32 ? Single((float)left[lane], (float)right[lane], opcode)
                    : Double(left[lane], right[lane], opcode);
                SimdTestSupport.WriteLane(expected, lane, width, Bits(result, width));
            }
            cpu.WriteXmm(0, a);
            cpu.WriteXmm(1, b);
            cpu[Reg.EAX] = CpuSandbox.DataAddress;
            cpu.WriteMemory(CpuSandbox.DataAddress, b);
            uint mxcsr = cpu.Mxcsr;
            byte prefix = scalar ? (width == 32 ? (byte)0xF3 : (byte)0xF2) : (width == 32 ? (byte)0 : (byte)0x66);
            string detail = $"finite arithmetic width={width} scalar={scalar} opcode={opcode:X2} memory={memory} source={Convert.ToHexString(b)}";
            SimdTestSupport.Execute(cpu, SimdTestSupport.Instruction(prefix, opcode, memory), expected, detail: detail);
            Assert.True(cpu.Mxcsr == mxcsr, $"{detail}; exact arithmetic changed MXCSR {mxcsr:X8}->{cpu.Mxcsr:X8}");
        }
    }

    private static ulong Bits(double value, int width) => width == 32
        ? BitConverter.SingleToUInt32Bits((float)value) : BitConverter.DoubleToUInt64Bits(value);

    private static float Single(float a, float b, byte opcode) => opcode switch
    {
        0x58 => a + b, 0x5C => a - b, 0x59 => a * b, 0x5E => a / b, 0x51 => MathF.Sqrt(b),
        _ => throw new ArgumentOutOfRangeException(nameof(opcode))
    };

    private static double Double(double a, double b, byte opcode) => opcode switch
    {
        0x58 => a + b, 0x5C => a - b, 0x59 => a * b, 0x5E => a / b, 0x51 => Math.Sqrt(b),
        _ => throw new ArgumentOutOfRangeException(nameof(opcode))
    };
}
