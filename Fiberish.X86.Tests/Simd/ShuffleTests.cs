using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Simd;

public sealed class ShuffleTests
{
    [Theory]
    [InlineData(0x66, 32, 0)] // PSHUFD
    [InlineData(0xF2, 16, 0)] // PSHUFLW
    [InlineData(0xF3, 16, 4)] // PSHUFHW
    public void IntegerShuffle_UsesAllImmediateSelectors(int prefix, int width, int firstLane)
    {
        foreach (byte immediate in new byte[] { 0x00, 0x1B, 0xE4, 0x93, 0xFF })
        foreach (bool memory in new[] { false, true })
        {
            using var cpu = SimdTestSupport.Create();
            byte[] source = SimdTestSupport.Lanes(16, 0x0011, 0x2233, 0x4455, 0x6677, 0x8899, 0xAABB, 0xCCDD, 0xEEFF);
            byte[] expected = source.ToArray();
            for (int lane = 0; lane < 4; lane++)
                SimdTestSupport.WriteLane(expected, firstLane + lane, width,
                    SimdTestSupport.ReadLane(source, firstLane + ((immediate >> (lane * 2)) & 3), width));
            cpu.WriteXmm(1, source);
            cpu[Reg.EAX] = CpuSandbox.DataAddress;
            cpu.WriteMemory(CpuSandbox.DataAddress, source);
            SimdTestSupport.Execute(cpu, SimdTestSupport.Instruction((byte)prefix, 0x70, memory, immediate), expected,
                detail: $"shuffle width={width} firstLane={firstLane} imm={immediate:X2} memory={memory} source={Convert.ToHexString(source)}");
        }
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void FloatingShuffle_SelectsFromBothOperandsBitForBit(int width)
    {
        foreach (byte immediate in new byte[] { 0x00, 0x01, 0x1B, 0xE4, 0xFF })
        foreach (bool memory in new[] { false, true })
        {
            using var cpu = SimdTestSupport.Create();
            byte[] a = SimdTestSupport.Lanes(32, 0x80000000, 0x7FC12345, 0x01234567, 0x89ABCDEF);
            byte[] b = SimdTestSupport.Lanes(32, 0x7FA12345, 0xFFFFFFFF, 0x76543210, 0xFEDCBA98);
            var expected = new byte[16];
            int halfLanes = 64 / width, selectorBits = width == 32 ? 2 : 1;
            for (int lane = 0; lane < 128 / width; lane++)
            {
                int selector = (immediate >> (lane * selectorBits)) & (width == 32 ? 3 : 1);
                SimdTestSupport.WriteLane(expected, lane, width,
                    SimdTestSupport.ReadLane(lane < halfLanes ? a : b, selector, width));
            }
            cpu.WriteXmm(0, a);
            cpu.WriteXmm(1, b);
            cpu[Reg.EAX] = CpuSandbox.DataAddress;
            cpu.WriteMemory(CpuSandbox.DataAddress, b);
            SimdTestSupport.Execute(cpu, SimdTestSupport.Instruction(width == 32 ? (byte)0 : (byte)0x66, 0xC6, memory, immediate),
                expected, detail: $"shuffle width={width} imm={immediate:X2} memory={memory} source={Convert.ToHexString(b)}");
        }
    }
}
