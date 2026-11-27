using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

public sealed class RevalidatedBlockCoherenceTests
{
    [Fact]
    public void RevalidatedSource_RemainsRegisteredForLaterInvalidation()
    {
        using var cpu = new CpuSandbox(mapDefaults: false);
        const uint caller = CpuSandbox.CodeAddress;
        const uint source = caller + CpuSandbox.PageSize;
        const uint successor = source + 0x100;
        const uint end = successor + 64;
        cpu.MapMemory(caller, CpuSandbox.PageSize, 7);
        cpu.MapMemory(source, CpuSandbox.PageSize, 7);
        cpu.WriteMemory(caller, Jump(caller, source));
        cpu.WriteMemory(source, [0xB8, 7, 0, 0, 0, .. Jump(source + 5, successor)]);
        // Warm the cached source and successor.
        cpu.WriteMemory(successor, Enumerable.Repeat((byte)0x41, 64).ToArray());
        RunCaller();
        Assert.Equal(7u, cpu[Reg.EAX]);
        Assert.Equal(64u, cpu[Reg.ECX]);

        // Invalidate the page, leaving the source bytes unchanged so its
        // decoded block can be revalidated.
        cpu.WriteMemory(successor, Jump(successor, end));
        RunCaller();
        Assert.Equal(7u, cpu[Reg.EAX]);

        // A second page invalidation must reach the revalidated source
        // still referenced by the caller's cached target.
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
        cpu.MapMemory(caller, CpuSandbox.PageSize, 7);
        cpu.MapMemory(source, CpuSandbox.PageSize, 7);

        // Warm the caller's link to the source on the second page.
        cpu.WriteMemory(caller, Jump(caller, source));
        cpu.WriteMemory(source, [0xB8, 7, 0, 0, 0, .. Jump(source + 5, successor)]);
        cpu.WriteMemory(successor, Enumerable.Repeat((byte)0x41, 64).ToArray());
        RunCaller();
        Assert.Equal(7u, cpu[Reg.EAX]);

        // The successor write invalidates their shared page. The source
        // is revalidated when the caller reaches it again.
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
