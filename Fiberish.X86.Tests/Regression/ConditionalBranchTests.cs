using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

public sealed class ConditionalBranchTests
{
    [Fact]
    public unsafe void JccFallthroughPreservesSubOperandDirection()
    {
        using var cpu = new CpuSandbox();
        const uint code = CpuSandbox.CodeAddress;
        byte[] instructions =
        [
            0x89, 0xD9,                   // mov ecx, ebx
            0x29, 0xC1,                   // sub ecx, eax
            0x85, 0xC0,                   // test eax, eax
            0x74, 0x05,                   // jz end
            0xBA, 0x01, 0x00, 0x00, 0x00 // mov edx, 1
        ];

        cpu.WriteMemory(code, instructions);
        cpu[Reg.EAX] = 0x78;
        cpu[Reg.EBX] = 0x10A6C;
        cpu.Eip = code;
        uint end = code + (uint)instructions.Length;
        cpu.Run(end, 32);

        X86Native.BlockStats stats = default;
        X86Native.GetBlockStats(cpu.State, &stats);
        Assert.Equal(1UL, stats.BlockCount);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(end, cpu.Eip);
        Assert.Equal(0x78u, cpu[Reg.EAX]);
        Assert.Equal(0x109F4u, cpu[Reg.ECX]);
        Assert.Equal(1u, cpu[Reg.EDX]);
    }
}
