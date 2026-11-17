using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

public sealed class RevalidatedBlockCoherenceTests
{
    [Fact]
    public void RevalidatedSource_WhenConcatIsBuilt_RemainsRegisteredForLaterInvalidation()
    {
        using var cpu = new CpuSandbox(mapDefaults: false);
        const uint caller = CpuSandbox.CodeAddress;
        const uint source = caller + CpuSandbox.PageSize;
        const uint successor = source + 0x100;
        const uint end = successor + 64;
        cpu.MapExternalMemory(caller, 7);
        cpu.MapExternalMemory(source, 7);
        cpu.WriteMemory(caller, Jump(caller, source));
        cpu.WriteMemory(source, [0xB8, 7, 0, 0, 0, .. Jump(source + 5, successor)]);
        // Source has two instructions; successor has 64. Their direct-jump
        // concat would have 65, so the initial source must be cached separately.
        cpu.WriteMemory(successor, Enumerable.Repeat((byte)0x41, 64).ToArray());
        RunCaller();
        Assert.Equal(7u, cpu[Reg.EAX]);
        Assert.Equal(64u, cpu[Reg.ECX]);

        // Invalidate the page, leaving the source bytes unchanged. Its old
        // decoded block can be revalidated, but a concat now fits as well.
        cpu.WriteMemory(successor, Jump(successor, end));
        RunCaller();
        Assert.Equal(7u, cpu[Reg.EAX]);

        // The caller's cached target still refers to that revalidated source.
        // A second page invalidation must reach it, even if concat publication
        // chose an already-valid block rather than the new concat.
        cpu.WriteMemory(source + 1, BitConverter.GetBytes(9u));
        RunCaller();
        Assert.Equal(9u, cpu[Reg.EAX]);

        void RunCaller()
        {
            cpu.Eip = caller;
            cpu.Run(end);
            Assert.Equal(EmuStatus.Stopped, cpu.Status);
            Assert.Equal(end, cpu.Eip);
        }
    }

    [Fact]
    public void RevalidatedSource_AfterGuestStoreToItsCodePage_ExecutesNewBytes()
    {
        using var cpu = new CpuSandbox(mapDefaults: false);
        const uint caller = CpuSandbox.CodeAddress;
        const uint source = caller + CpuSandbox.PageSize;
        const uint successor = source + 0x100;
        const uint end = successor + 64;
        cpu.MapExternalMemory(caller, 7);
        cpu.MapExternalMemory(source, 7);

        // Warm the source alone: two instructions plus the 64-instruction
        // successor exceed the concat limit.
        cpu.WriteMemory(caller, Jump(caller, source));
        cpu.WriteMemory(source, [0xB8, 7, 0, 0, 0, .. Jump(source + 5, successor)]);
        cpu.WriteMemory(successor, Enumerable.Repeat((byte)0x41, 64).ToArray());
        RunCaller();
        Assert.Equal(7u, cpu[Reg.EAX]);

        // Invalidate the source page. Revalidation now also fits a short concat
        // with the rewritten successor, so the revalidated block is republished.
        cpu.WriteMemory(successor, Jump(successor, end));
        RunCaller();
        Assert.Equal(7u, cpu[Reg.EAX]);

        // A guest store (not the external write API) patches the immediate in
        // place on the executable page. It must invalidate the republished
        // block: both SMC arming and page registration have to be restored.
        cpu.WriteMemory(caller,
            [0xB8, 9, 0, 0, 0, 0x88, 0x02, .. Jump(caller + 7, source)]); // mov eax,9; mov [edx],al; jmp source
        cpu[Reg.EDX] = source + 1;
        RunCaller();
        Assert.Equal(9u, cpu[Reg.EAX]);

        void RunCaller()
        {
            cpu.Eip = caller;
            cpu.Run(end);
            Assert.True(cpu.Status == EmuStatus.Stopped,
                $"status={cpu.Status} vector={cpu.FaultVector} faultAddr=0x{cpu.FaultAddress:X} " +
                $"eip=0x{cpu.Eip:X} eax=0x{cpu[Reg.EAX]:X} edx=0x{cpu[Reg.EDX]:X}");
            Assert.Equal(end, cpu.Eip);
        }
    }

    private static byte[] Jump(uint from, uint to) =>
        [0xE9, .. BitConverter.GetBytes(unchecked((int)(to - from - 5)))];
}
