using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

public sealed class RepStringOperationTests
{
    [Theory]
    [InlineData(0xAA, 1)]
    [InlineData(0xAB, 4)]
    public void StosStopsAtTheBudgetAndResumesWithTheSameEip(byte opcode, int width)
    {
        using var cpu = new CpuSandbox();
        const uint address = CpuSandbox.DataAddress + 3;
        const uint count = 100;
        const uint budget = 19;
        cpu.WriteCode([0xF3, opcode]);
        cpu[Reg.EDI] = address;
        cpu[Reg.ECX] = count;
        cpu[Reg.EAX] = 0x07061112;

        cpu.Run(CpuSandbox.CodeAddress + 2, budget);

        Assert.Equal(CpuSandbox.CodeAddress, cpu.Eip);
        Assert.Equal(count - budget, cpu[Reg.ECX]);
        Assert.Equal(address + budget * width, cpu[Reg.EDI]);
        Assert.Equal(0, cpu.ReadMemory(cpu[Reg.EDI], 1)[0]);

        cpu.Run(CpuSandbox.CodeAddress + 2);
        Assert.Equal(0u, cpu[Reg.ECX]);
        var element = BitConverter.GetBytes(0x07061112).AsSpan(0, width).ToArray();
        Assert.Equal(Enumerable.Repeat(element, (int)count).SelectMany(bytes => bytes),
            cpu.ReadMemory(address, (int)count * width));
    }

    [Theory]
    [InlineData(false, 64, 65)]
    [InlineData(false, 65, 64)]
    [InlineData(true, 64, 65)]
    [InlineData(true, 65, 64)]
    public void MovsbPreservesDirectionalOverlap(bool backwards, int source, int destination)
    {
        using var cpu = new CpuSandbox();
        var initial = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var expected = initial.ToArray();
        var step = backwards ? -1 : 1;
        for (var i = 0; i < 32; i++)
            expected[destination + i * step] = expected[source + i * step];

        cpu.WriteCode([0xF3, 0xA4]);
        cpu.WriteMemory(CpuSandbox.DataAddress, initial);
        cpu[Reg.ESI] = CpuSandbox.DataAddress + (uint)source;
        cpu[Reg.EDI] = CpuSandbox.DataAddress + (uint)destination;
        cpu[Reg.ECX] = 32;
        if (backwards) cpu.Eflags |= 0x400;

        cpu.Run(CpuSandbox.CodeAddress + 2);

        Assert.Equal(0u, cpu[Reg.ECX]);
        Assert.Equal(expected, cpu.ReadMemory(CpuSandbox.DataAddress, initial.Length));
    }

    [Fact]
    public void RepneScasStopsOnMatchAfterResuming()
    {
        using var cpu = new CpuSandbox();
        cpu.WriteCode([0xF2, 0xAE]);
        cpu.WriteMemory(CpuSandbox.DataAddress + 14, [0x55]);
        cpu[Reg.ECX] = 32;
        cpu[Reg.EDI] = CpuSandbox.DataAddress;
        cpu[Reg.EAX] = 0x55;

        cpu.Run(CpuSandbox.CodeAddress + 2, 8);
        Assert.Equal(CpuSandbox.CodeAddress, cpu.Eip);
        Assert.Equal(24u, cpu[Reg.ECX]);

        cpu.Run(CpuSandbox.CodeAddress + 2, 32);
        Assert.Equal(CpuSandbox.CodeAddress + 2, cpu.Eip);
        Assert.Equal(17u, cpu[Reg.ECX]);
        Assert.Equal(CpuSandbox.DataAddress + 15, cpu[Reg.EDI]);
        Assert.NotEqual(0u, cpu.Eflags & 0x40);
    }

    [Fact]
    public void LargeRepYieldsBeforeTheOrdinaryInstructionBudgetIsExhausted()
    {
        using var cpu = new CpuSandbox();
        cpu.MapMemory(CpuSandbox.DataAddress, 128 * 1024);
        cpu.WriteCode([0xF3, 0xAA]);
        cpu[Reg.EDI] = CpuSandbox.DataAddress;
        cpu[Reg.ECX] = 100_000;
        cpu[Reg.EAX] = 0xAA;

        cpu.Run(CpuSandbox.CodeAddress + 2, 1_000_000);
        Assert.Equal(EmuStatus.Yield, cpu.Status);
        Assert.Equal(CpuSandbox.CodeAddress, cpu.Eip);
        Assert.InRange(cpu[Reg.ECX], 1u, 99_999u);

        cpu.Run(CpuSandbox.CodeAddress + 2, 1_000_000);
        Assert.Equal(0u, cpu[Reg.ECX]);
        Assert.Equal(0xAA, cpu.ReadMemory(CpuSandbox.DataAddress + 99_999, 1)[0]);
    }
}
