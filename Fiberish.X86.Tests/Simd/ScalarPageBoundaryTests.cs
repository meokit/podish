using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Simd;

public sealed class ScalarPageBoundaryTests
{
    [Theory]
    [InlineData(4, false)]
    [InlineData(8, false)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    public void ScalarMemoryMove_AtPageEndDoesNotAccessFullXmmWidth(int width, bool store)
    {
        using var cpu = SimdTestSupport.Create();
        uint edge = CpuSandbox.DataAddress + 0x1000;
        cpu[Reg.EAX] = edge - (uint)width;
        byte[] source = SimdTestSupport.Lanes(64, 0x7FF8123456789ABC, 0xFEDCBA9876543210);
        cpu.WriteXmm(0, source);
        cpu.WriteMemory(edge - 16, Enumerable.Repeat((byte)0xA5, 16).ToArray());
        if (!store) cpu.WriteMemory(cpu[Reg.EAX], source.AsSpan(0, width));
        var expected = new byte[16];
        source.AsSpan(0, width).CopyTo(expected);
        byte[] code = SimdTestSupport.Instruction(width == 4 ? (byte)0xF3 : (byte)0xF2, store ? (byte)0x11 : (byte)0x10, true);
        SimdTestSupport.Execute(cpu, code, store ? null : expected,
            detail: $"scalar page edge width={width * 8} store={store} address={cpu[Reg.EAX]:X8}");
        byte[] memory = Enumerable.Repeat((byte)0xA5, 16).ToArray();
        source.AsSpan(0, width).CopyTo(memory.AsSpan(16 - width));
        SimdTestSupport.Bytes(memory, cpu.ReadMemory(edge - 16, 16),
            $"bytes={Convert.ToHexString(code)} scalar page edge width={width * 8} store={store}");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void ScalarCvtt_AtPageEndReadsOnlyTheScalarOperand(int width)
    {
        using var cpu = SimdTestSupport.Create();
        uint address = CpuSandbox.DataAddress + 0x1000 - (uint)width;
        cpu[Reg.EAX] = address;
        byte[] source = width == 4 ? SimdTestSupport.Lanes(32, BitConverter.SingleToUInt32Bits(-1.75f))
            : SimdTestSupport.Lanes(64, BitConverter.DoubleToUInt64Bits(-1.75));
        cpu.WriteMemory(address, source.AsSpan(0, width));
        byte[] code = { width == 4 ? (byte)0xF3 : (byte)0xF2, 0x0F, 0x2C, 0x10 };
        SimdTestSupport.Execute(cpu, code, registerDestination: Reg.EDX, registerValue: 0xFFFFFFFF,
            detail: $"cvtt page edge width={width * 8} address={address:X8} input=-1.75");
    }
}
