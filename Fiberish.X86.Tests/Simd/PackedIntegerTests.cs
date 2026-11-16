using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Simd;

public sealed class PackedSaturationTests
{
    [Theory]
    [InlineData(8, true, false)]
    [InlineData(16, true, false)]
    [InlineData(8, false, false)]
    [InlineData(16, false, false)]
    [InlineData(8, true, true)]
    [InlineData(16, true, true)]
    [InlineData(8, false, true)]
    [InlineData(16, false, true)]
    public void PackedAddSubtract_SaturatesPerLane(int width, bool signed, bool subtract)
    {
        foreach (bool memory in new[] { false, true })
        {
            using var cpu = SimdTestSupport.Create();
            ulong mask = (1UL << width) - 1;
            ulong sign = 1UL << (width - 1);
            ulong[] aValues = { sign - 1, sign, mask, 0, 1, mask - 1, sign - 2, sign + 1 };
            ulong[] bValues = { 1, mask, 1, 1, 2, 3, sign - 1, sign };
            var a = new byte[16];
            var b = new byte[16];
            var expected = new byte[16];
            for (int lane = 0; lane < 128 / width; lane++)
            {
                ulong av = aValues[lane % aValues.Length], bv = bValues[lane % bValues.Length];
                SimdTestSupport.WriteLane(a, lane, width, av);
                SimdTestSupport.WriteLane(b, lane, width, bv);
                long left = signed ? SimdTestSupport.Signed(av, width) : (long)av;
                long right = signed ? SimdTestSupport.Signed(bv, width) : (long)bv;
                long result = subtract ? left - right : left + right;
                long min = signed ? -(long)sign : 0;
                long max = signed ? (long)sign - 1 : (long)mask;
                SimdTestSupport.WriteLane(expected, lane, width, unchecked((ulong)Math.Clamp(result, min, max)));
            }
            cpu.WriteXmm(0, a);
            cpu.WriteXmm(1, b);
            cpu[Reg.EAX] = CpuSandbox.DataAddress;
            cpu.WriteMemory(CpuSandbox.DataAddress, b);
            int opcode = subtract ? (signed ? 0xE8 : 0xD8) : (signed ? 0xEC : 0xDC);
            if (width == 16) opcode++;
            string detail = $"saturation width={width} signed={signed} subtract={subtract} memory={memory} source={Convert.ToHexString(b)}";
            SimdTestSupport.Execute(cpu, SimdTestSupport.Instruction(0x66, (byte)opcode, memory), expected, detail: detail);
            SimdTestSupport.Bytes(b, cpu.ReadMemory(CpuSandbox.DataAddress, 16), detail);
        }
    }
}

public sealed class PackedUnpackPackTests
{
    [Theory]
    [InlineData(8, false)]
    [InlineData(16, false)]
    [InlineData(32, false)]
    [InlineData(64, false)]
    [InlineData(8, true)]
    [InlineData(16, true)]
    [InlineData(32, true)]
    [InlineData(64, true)]
    public void Unpack_InterleavesSelectedHalf(int width, bool high)
    {
        foreach (bool memory in new[] { false, true })
        {
            using var cpu = SimdTestSupport.Create();
            byte[] a = Enumerable.Range(0, 16).Select(i => (byte)(0x10 + i)).ToArray();
            byte[] b = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();
            var expected = new byte[16];
            int laneBytes = width / 8, halfLanes = 64 / width;
            for (int lane = 0; lane < halfLanes; lane++)
            {
                int source = (lane + (high ? halfLanes : 0)) * laneBytes;
                a.AsSpan(source, laneBytes).CopyTo(expected.AsSpan(lane * 2 * laneBytes));
                b.AsSpan(source, laneBytes).CopyTo(expected.AsSpan((lane * 2 + 1) * laneBytes));
            }
            cpu.WriteXmm(0, a);
            cpu.WriteXmm(1, b);
            cpu[Reg.EAX] = CpuSandbox.DataAddress;
            cpu.WriteMemory(CpuSandbox.DataAddress, b);
            int opcode = width == 64 ? (high ? 0x6D : 0x6C) : (high ? 0x68 : 0x60) + (width == 8 ? 0 : width == 16 ? 1 : 2);
            SimdTestSupport.Execute(cpu, SimdTestSupport.Instruction(0x66, (byte)opcode, memory), expected,
                detail: $"unpack width={width} high={high} memory={memory} source={Convert.ToHexString(b)}");
        }
    }

    [Theory]
    [InlineData(16, true, 0x63)] // PACKSSWB
    [InlineData(16, false, 0x67)] // PACKUSWB: input words are signed, even for unsigned output.
    [InlineData(32, true, 0x6B)] // PACKSSDW
    public void Pack_ConcatenatesAndSaturatesSignedInputs(int inputWidth, bool signedOutput, int opcode)
    {
        foreach (bool memory in new[] { false, true })
        {
            using var cpu = SimdTestSupport.Create();
            int outputWidth = inputWidth / 2, inputLanes = 128 / inputWidth;
            long minimum = signedOutput ? -(1L << (outputWidth - 1)) : 0;
            long maximum = signedOutput ? (1L << (outputWidth - 1)) - 1 : (1L << outputWidth) - 1;
            long[] values = { minimum - 1, minimum, maximum, maximum + 1, -1, 0, 1, long.MinValue >> (64 - inputWidth) };
            var a = new byte[16];
            var b = new byte[16];
            var expected = new byte[16];
            for (int lane = 0; lane < inputLanes * 2; lane++)
            {
                long value = values[(lane + (lane >= inputLanes ? 3 : 0)) % values.Length];
                SimdTestSupport.WriteLane(lane < inputLanes ? a : b, lane % inputLanes, inputWidth, unchecked((ulong)value));
                SimdTestSupport.WriteLane(expected, lane, outputWidth,
                    unchecked((ulong)Math.Clamp(value, minimum, maximum)));
            }
            cpu.WriteXmm(0, a);
            cpu.WriteXmm(1, b);
            cpu[Reg.EAX] = CpuSandbox.DataAddress;
            cpu.WriteMemory(CpuSandbox.DataAddress, b);
            SimdTestSupport.Execute(cpu, SimdTestSupport.Instruction(0x66, (byte)opcode, memory), expected,
                detail: $"pack inputWidth={inputWidth} signedOutput={signedOutput} memory={memory} source={Convert.ToHexString(b)}");
        }
    }
}
