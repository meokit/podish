using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Simd;

public sealed class PackedMoveTests
{
    [Theory]
    [InlineData(0x00, 0x10, 3)] // MOVUPS
    [InlineData(0x66, 0x10, 3)] // MOVUPD
    [InlineData(0xF3, 0x6F, 3)] // MOVDQU
    [InlineData(0x00, 0x28, 0)] // MOVAPS
    [InlineData(0x66, 0x28, 0)] // MOVAPD
    [InlineData(0x66, 0x6F, 0)] // MOVDQA
    public void FullWidthMoves_CopyAllBitsAndPreserveNeighbors(int prefix, int opcode, int offset)
    {
        foreach (bool store in new[] { false, true })
        {
            using var cpu = SimdTestSupport.Create();
            byte[] source = SimdTestSupport.Lanes(32, 0x80000000, 0x7FC12345, 0xFFFFFFFF, 0x12345678);
            byte[] before = Enumerable.Repeat((byte)0xA5, 48).ToArray();
            cpu.WriteMemory(CpuSandbox.DataAddress, before);
            cpu[Reg.EAX] = CpuSandbox.DataAddress + 16 + (uint)offset;
            if (store) cpu.WriteXmm(0, source);
            else cpu.WriteMemory(cpu[Reg.EAX], source);
            int storeOpcode = opcode == 0x6F ? 0x7F : opcode + 1;
            byte[] code = SimdTestSupport.Instruction((byte)prefix, (byte)(store ? storeOpcode : opcode), true);
            SimdTestSupport.Execute(cpu, code, store ? null : source, detail: $"packed move offset={offset} store={store}");
            source.CopyTo(before, 16 + offset);
            SimdTestSupport.Bytes(before, cpu.ReadMemory(CpuSandbox.DataAddress, 48),
                $"bytes={Convert.ToHexString(code)} offset={offset} store={store}");
        }
    }
}
