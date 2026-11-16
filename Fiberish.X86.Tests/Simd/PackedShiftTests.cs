using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Simd;

public sealed class PackedShiftTests
{
    [Theory]
    [InlineData(16, "left")]
    [InlineData(32, "left")]
    [InlineData(64, "left")]
    [InlineData(16, "right")]
    [InlineData(32, "right")]
    [InlineData(64, "right")]
    [InlineData(16, "arithmetic")]
    [InlineData(32, "arithmetic")]
    public void VariableShift_UsesEntireLowQwordAndIgnoresHighQword(int width, string operation)
    {
        foreach (ulong count in new ulong[] { 0, 1, (ulong)width - 1, (ulong)width, (ulong)width + 1, 255, 0x100000000, 0x8000000000000000 })
        foreach (bool memory in new[] { false, true })
        {
            using var cpu = SimdTestSupport.Create();
            byte[] input = Input();
            byte[] counts = SimdTestSupport.Lanes(64, count, 0xFEDCBA9876543210);
            cpu.WriteXmm(0, input);
            cpu.WriteXmm(1, counts);
            cpu[Reg.EAX] = CpuSandbox.DataAddress;
            cpu.WriteMemory(CpuSandbox.DataAddress, counts);
            int opcode = (operation == "left" ? 0xF1 : operation == "right" ? 0xD1 : 0xE1) + (width == 16 ? 0 : width == 32 ? 1 : 2);
            SimdTestSupport.Execute(cpu, SimdTestSupport.Instruction(0x66, (byte)opcode, memory),
                Reference(input, width, count, operation), detail: $"shift width={width} operation={operation} count={count:X16} memory={memory}");
        }
    }

    [Theory]
    [InlineData(16, "left")]
    [InlineData(32, "left")]
    [InlineData(64, "left")]
    [InlineData(16, "right")]
    [InlineData(32, "right")]
    [InlineData(64, "right")]
    [InlineData(16, "arithmetic")]
    [InlineData(32, "arithmetic")]
    public void ImmediateShift_DoesNotMaskOversizedCounts(int width, string operation)
    {
        foreach (byte count in new byte[] { 0, 1, (byte)(width - 1), (byte)width, (byte)(width + 1), 255 })
        {
            using var cpu = SimdTestSupport.Create();
            byte[] input = Input();
            cpu.WriteXmm(0, input);
            int group = operation == "left" ? 6 : operation == "right" ? 2 : 4;
            byte[] code = { 0x66, 0x0F, (byte)(width == 16 ? 0x71 : width == 32 ? 0x72 : 0x73), (byte)(0xC0 | group << 3), count };
            SimdTestSupport.Execute(cpu, code, Reference(input, width, count, operation),
                detail: $"immediate shift width={width} operation={operation} count={count}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ByteShift_ClampsAtSixteenRatherThanWrapping(bool left)
    {
        foreach (byte count in new byte[] { 0, 1, 7, 15, 16, 17, 255 })
        {
            using var cpu = SimdTestSupport.Create();
            byte[] input = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
            var expected = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                int source = left ? i - count : i + count;
                if (source >= 0 && source < 16) expected[i] = input[source];
            }
            cpu.WriteXmm(0, input);
            SimdTestSupport.Execute(cpu, new byte[] { 0x66, 0x0F, 0x73, left ? (byte)0xF8 : (byte)0xD8, count }, expected,
                detail: $"byte shift left={left} count={count}");
        }
    }

    private static byte[] Input() => SimdTestSupport.Lanes(64, 0x80017FFEFFFF0001, 0x0123456789ABCDEF);

    private static byte[] Reference(byte[] input, int width, ulong count, string operation)
    {
        var expected = new byte[16];
        for (int lane = 0; lane < 128 / width; lane++)
        {
            ulong value = SimdTestSupport.ReadLane(input, lane, width);
            ulong result;
            if (operation == "arithmetic")
            {
                long signed = SimdTestSupport.Signed(value, width);
                result = unchecked((ulong)(count >= (ulong)width ? (signed < 0 ? -1L : 0) : signed >> (int)count));
            }
            else result = count >= (ulong)width ? 0 : operation == "left" ? value << (int)count : value >> (int)count;
            SimdTestSupport.WriteLane(expected, lane, width, result);
        }
        return expected;
    }
}
