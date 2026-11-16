using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Simd;

public sealed class FloatingMinMaxTests
{
    [Theory]
    [InlineData(32, false, false)]
    [InlineData(32, false, true)]
    [InlineData(32, true, false)]
    [InlineData(32, true, true)]
    [InlineData(64, false, false)]
    [InlineData(64, false, true)]
    [InlineData(64, true, false)]
    [InlineData(64, true, true)]
    public void MinMax_EqualZerosAndUnorderedInputsSelectSecondOperand(int width, bool scalar, bool maximum)
    {
        ulong negativeZero = width == 32 ? 0x80000000UL : 0x8000000000000000UL;
        ulong nanA = width == 32 ? 0x7FC12345UL : 0x7FF8123456789ABCUL;
        ulong nanB = width == 32 ? 0xFFC54321UL : 0xFFF8ABCDEF012345UL;
        ulong one = width == 32 ? 0x3F800000UL : 0x3FF0000000000000UL;
        ulong minusTwo = width == 32 ? 0xC0000000UL : 0xC000000000000000UL;
        (ulong A, ulong B)[] pairs =
        {
            (0, negativeZero), (negativeZero, 0), (nanA, one), (one, nanB),
            (nanA, nanB), (nanB, nanA), (one, minusTwo), (minusTwo, one), (one, one)
        };
        foreach (var pair in pairs)
        foreach (bool memory in new[] { false, true })
        {
            using var cpu = SimdTestSupport.Create();
            byte[] a = cpu.ReadXmm(0), b = cpu.ReadXmm(1);
            int lanes = scalar ? 1 : 128 / width;
            for (int lane = 0; lane < lanes; lane++)
            {
                SimdTestSupport.WriteLane(a, lane, width, pair.A);
                SimdTestSupport.WriteLane(b, lane, width, pair.B);
            }
            byte[] expected = a.ToArray();
            double left = Number(pair.A, width), right = Number(pair.B, width);
            // Intel MIN/MAX are not Math.Min/Max: equal or unordered always chooses src2 bits.
            ulong selected = maximum ? (left > right ? pair.A : pair.B) : (left < right ? pair.A : pair.B);
            for (int lane = 0; lane < lanes; lane++) SimdTestSupport.WriteLane(expected, lane, width, selected);
            cpu.WriteXmm(0, a);
            cpu.WriteXmm(1, b);
            cpu[Reg.EAX] = CpuSandbox.DataAddress;
            cpu.WriteMemory(CpuSandbox.DataAddress, b);
            byte prefix = scalar ? (width == 32 ? (byte)0xF3 : (byte)0xF2) : (width == 32 ? (byte)0 : (byte)0x66);
            SimdTestSupport.Execute(cpu, SimdTestSupport.Instruction(prefix, maximum ? (byte)0x5F : (byte)0x5D, memory), expected,
                detail: $"min/max width={width} scalar={scalar} maximum={maximum} memory={memory} a={pair.A:X16} b={pair.B:X16}");
        }
    }

    private static double Number(ulong bits, int width) => width == 32
        ? BitConverter.UInt32BitsToSingle((uint)bits)
        : BitConverter.UInt64BitsToDouble(bits);
}
