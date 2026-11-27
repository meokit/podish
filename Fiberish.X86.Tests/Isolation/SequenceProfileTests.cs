using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Isolation;

public sealed class SequenceProfileTests
{
    [Fact]
    public void BuildIdentitySeparatesTrainingFromOptimization()
    {
        var features = X86Native.GetBuildFeatures();
        Assert.Equal(0u, features & ~0x7f3fu);
        Assert.InRange((features >> 4) & 3u, 0u, 3u);
        uint pinMode = ((features >> 8) & 3u) | ((features >> 9) & 4u);
        Assert.InRange(pinMode, 0u, 4u);
        Assert.Equal(pinMode == 4, X86Native.GetOptimizationConfig().Contains("pin=WRITE_BACK;"));
        Assert.Contains("pin=", X86Native.GetOptimizationConfig());
        Assert.Contains("operand=", X86Native.GetOptimizationConfig());
        Assert.NotEqual(3u, features & 3u);
        var hash = X86Native.GetSuperOpcodeSetHash();
        if ((features & 1) == 0) Assert.Equal("none", hash);
        else Assert.Matches("^[a-f0-9]{64}$", hash);
    }

    [Fact]
    public void ContinueEdgesAreInstanceOwnedCumulativeAndIndependentOfCacheLifetime()
    {
        using var cpu = new CpuSandbox();
        using var peer = new CpuSandbox();
        cpu.WriteCode([0x40, 0x43, 0x42]); // INC EAX; INC EBX; INC EDX
        cpu.Run(CpuSandbox.CodeAddress + 3);
        Assert.Equal(Expected(2), Total(cpu));
        Assert.Equal(0UL, Total(peer));
        X86Native.ResetAllCodeCache(cpu.State);
        cpu.Eip = CpuSandbox.CodeAddress;
        cpu.Run(CpuSandbox.CodeAddress + 3);
        Assert.Equal(Expected(4), Total(cpu));
        X86Native.ResetSequenceProfile(cpu.State);
        Assert.Equal(0UL, Total(cpu));
    }

    [Fact]
    public void ParallelInstancesNeverShareSequenceCounts()
    {
        Parallel.For(0, 8, index =>
        {
            using var cpu = new CpuSandbox();
            cpu.WriteCode([0x40, 0x43, 0x42]);
            for (var iteration = 0; iteration <= index; iteration++)
            {
                cpu.Eip = CpuSandbox.CodeAddress;
                cpu.Run(CpuSandbox.CodeAddress + 3);
            }
            Assert.Equal(Expected((ulong)(2 * (index + 1))), Total(cpu));
            Assert.Equal(Expected((ulong)(index + 1)), NGramTotal(cpu, 3));
            Assert.Equal(0UL, NGramTotal(cpu, 4));
        });
    }

    [Fact]
    public void FaultingSecondConstituentCountsItsEntryButNeverTheUnexecutedTail()
    {
        using var cpu = new CpuSandbox();
        cpu.WriteCode([0x40, 0x43, 0x8B, 0x0D, 0, 0, 0x50, 0, 0x42]);
        cpu.Run(CpuSandbox.CodeAddress + 9);
        Assert.Equal(EmuStatus.Fault, cpu.Status);
        Assert.Equal(14, cpu.FaultVector);
        Assert.Equal(CpuSandbox.CodeAddress + 2, cpu.Eip);
        Assert.Equal(0u, cpu[Reg.EDX]);
        Assert.Equal(Expected(2), Total(cpu));
    }

    [Fact]
    public void BranchTargetsAndStepInvocationsDoNotInventAdjacentEdges()
    {
        using var cpu = new CpuSandbox();
        cpu.WriteCode([0x40, 0xEB, 0, 0x43]); // The jump target starts a distinct block.
        cpu.Run(CpuSandbox.CodeAddress + 4);
        Assert.Equal(Expected(1), Total(cpu));
        X86Native.ResetSequenceProfile(cpu.State);
        cpu.WriteCode([0x40, 0x43]);
        cpu.Step(); cpu.Step();
        Assert.Equal(0UL, Total(cpu));
    }

    [Fact]
    public void ExactNGramsAccumulateButNeverJoinSeparateRunsOrCacheLifetimes()
    {
        using var cpu = new CpuSandbox();
        cpu.WriteCode([0x40, 0x43, 0x42, 0x47]);
        cpu.Run(CpuSandbox.CodeAddress + 4);
        Assert.Equal(Expected(2), NGramTotal(cpu, 3));
        Assert.Equal(Expected(1), NGramTotal(cpu, 4));
        X86Native.ResetAllCodeCache(cpu.State);
        cpu.Eip = CpuSandbox.CodeAddress;
        cpu.Run(CpuSandbox.CodeAddress + 4);
        Assert.Equal(Expected(4), NGramTotal(cpu, 3));
        Assert.Equal(Expected(2), NGramTotal(cpu, 4));
        X86Native.ResetSequenceProfile(cpu.State);
        Assert.Equal(0UL, NGramTotal(cpu, 3));
        Assert.Equal(0UL, NGramTotal(cpu, 4));
    }

    [Fact]
    public void NGramsCountFaultEntryButNoUnexecutedTailOrBranchTransition()
    {
        using var cpu = new CpuSandbox();
        cpu.WriteCode([0x40, 0x43, 0x42, 0x8B, 0x0D, 0, 0, 0x50, 0, 0x47]);
        cpu.Run(CpuSandbox.CodeAddress + 10);
        Assert.Equal(EmuStatus.Fault, cpu.Status);
        Assert.Equal(CpuSandbox.CodeAddress + 3, cpu.Eip);
        Assert.Equal(0u, cpu[Reg.EDI]);
        Assert.Equal(Expected(2), NGramTotal(cpu, 3));
        Assert.Equal(Expected(1), NGramTotal(cpu, 4));
        X86Native.ResetSequenceProfile(cpu.State);
        // The taken Jcc ends the block before the next two instructions.
        cpu.WriteCode([0x40, 0x75, 1, 0x47, 0x43, 0x42]);
        cpu.Run(CpuSandbox.CodeAddress + 6);
        Assert.Equal(0u, cpu[Reg.EDI]);
        Assert.Equal(0UL, NGramTotal(cpu, 3));
        Assert.Equal(0UL, NGramTotal(cpu, 4));
    }

    private static ulong Expected(ulong value) => (X86Native.GetBuildFeatures() & 2) != 0 ? value : 0;

    private static unsafe ulong Total(CpuSandbox cpu)
    {
        var count = checked((int)X86Native.GetSequenceProfile(cpu.State, null, 0));
        var entries = new X86Native.SequenceProfileEntry[count];
        fixed (X86Native.SequenceProfileEntry* pointer = entries)
            Assert.Equal((nuint)count, X86Native.GetSequenceProfile(cpu.State, pointer, (nuint)count));
        ulong total = 0;
        foreach (var entry in entries)
        {
            Assert.NotEqual(IntPtr.Zero, entry.First);
            Assert.NotEqual(IntPtr.Zero, entry.Second);
            total = checked(total + entry.Count);
        }
        return total;
    }

    private static unsafe ulong NGramTotal(CpuSandbox cpu, int width)
    {
        var count = checked((int)X86Native.GetNGramProfile(cpu.State, null, 0));
        var entries = new X86Native.NGramProfileEntry[count];
        fixed (X86Native.NGramProfileEntry* pointer = entries)
            Assert.Equal((nuint)count, X86Native.GetNGramProfile(cpu.State, pointer, (nuint)count));
        ulong total = 0;
        foreach (var entry in entries)
        {
            Assert.NotEqual(IntPtr.Zero, entry.First); Assert.NotEqual(IntPtr.Zero, entry.Second);
            Assert.NotEqual(IntPtr.Zero, entry.Third); Assert.True(entry.Count > 0);
            if ((entry.Fourth == IntPtr.Zero ? 3 : 4) == width) total = checked(total + entry.Count);
        }
        return total;
    }
}
