using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Simd;

public sealed class ScalarMoveTests
{
    [Theory]
    [InlineData(4, false, false)]
    [InlineData(4, false, true)]
    [InlineData(4, true, false)]
    [InlineData(8, false, false)]
    [InlineData(8, false, true)]
    [InlineData(8, true, false)]
    public void MovssMovsd_RegisterPreservesUpper_MemoryLoadClearsUpper(int width, bool memory, bool reverseOpcode)
    {
        using var cpu = SimdTestSupport.Create();
        byte[] source = SimdTestSupport.Lanes(32, 0x80000000, 0x7FC12345, 0x01234567, 0x89ABCDEF);
        int destination = reverseOpcode ? 1 : 0;
        cpu.WriteXmm(reverseOpcode ? 0 : 1, source);
        cpu[Reg.EAX] = CpuSandbox.DataAddress + 3; // Scalar accesses need not be aligned.
        cpu.WriteMemory(cpu[Reg.EAX], source);
        byte[] expected = memory ? new byte[16] : cpu.ReadXmm(destination);
        source.AsSpan(0, width).CopyTo(expected);
        byte[] code = SimdTestSupport.Instruction(width == 4 ? (byte)0xF3 : (byte)0xF2,
            reverseOpcode ? (byte)0x11 : (byte)0x10, memory);
        SimdTestSupport.Execute(cpu, code, expected, destination, detail: $"scalar width={width * 8} memory={memory}");
        SimdTestSupport.Bytes(source, cpu.ReadMemory(cpu[Reg.EAX], 16), "scalar load must not modify source memory");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void ScalarStore_WritesOnlyLowLane(int width)
    {
        using var cpu = SimdTestSupport.Create();
        byte[] before = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        byte[] source = cpu.ReadXmm(0);
        cpu.WriteMemory(CpuSandbox.DataAddress, before);
        cpu[Reg.EAX] = CpuSandbox.DataAddress + 7;
        byte[] expected = before.ToArray();
        source.AsSpan(0, width).CopyTo(expected.AsSpan(7));
        SimdTestSupport.Execute(cpu, SimdTestSupport.Instruction(width == 4 ? (byte)0xF3 : (byte)0xF2, 0x11, true),
            detail: $"scalar store width={width * 8}");
        SimdTestSupport.Bytes(expected, cpu.ReadMemory(CpuSandbox.DataAddress, 32), $"store width={width * 8}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Movd_IntoXmmClearsUpper96Bits(bool memory)
    {
        using var cpu = SimdTestSupport.Create();
        cpu[Reg.EAX] = memory ? CpuSandbox.DataAddress + 1 : 0xDEADBEEF;
        cpu.WriteMemory(CpuSandbox.DataAddress + 1, new byte[] { 0xEF, 0xBE, 0xAD, 0xDE });
        byte[] code = { 0x66, 0x0F, 0x6E, memory ? (byte)0x00 : (byte)0xC0 };
        SimdTestSupport.Execute(cpu, code, SimdTestSupport.Lanes(32, 0xDEADBEEF));
    }

    [Fact]
    public void Movd_FromXmmWritesExactlyOneGeneralRegister()
    {
        using var cpu = SimdTestSupport.Create();
        cpu.WriteXmm(0, SimdTestSupport.Lanes(32, 0xDEADBEEF, 1, 2, 3));
        SimdTestSupport.Execute(cpu, new byte[] { 0x66, 0x0F, 0x7E, 0xC3 },
            registerDestination: Reg.EBX, registerValue: 0xDEADBEEF);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Movq_RegisterFormsClearUpper64Bits(bool reverseOpcode)
    {
        using var cpu = SimdTestSupport.Create();
        byte[] source = SimdTestSupport.Lanes(64, 0xFEDCBA9876543210, 0xDEADBEEFF00DBAAD);
        cpu.WriteXmm(reverseOpcode ? 0 : 1, source);
        byte[] code = reverseOpcode ? new byte[] { 0x66, 0x0F, 0xD6, 0xC1 } : new byte[] { 0xF3, 0x0F, 0x7E, 0xC1 };
        SimdTestSupport.Execute(cpu, code, SimdTestSupport.Lanes(64, 0xFEDCBA9876543210), reverseOpcode ? 1 : 0);
    }
}
