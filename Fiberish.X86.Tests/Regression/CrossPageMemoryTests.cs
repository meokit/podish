using System.Runtime.InteropServices;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

public sealed class CrossPageMemoryTests
{
    private const uint SecondPage = CpuSandbox.DataAddress + CpuSandbox.PageSize;
    private static ReadOnlySpan<byte> Code => [0x83, 0x07, 0x01, 0x8B, 0x07]; // add dword [edi],1; mov eax,[edi]

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void WarmCrossPageRmw_DoesNotPartiallyWriteWhenSecondPageFaults(int firstSize, bool readOnly)
    {
        using var cpu = new CpuSandbox();
        uint address = SecondPage - (uint)firstSize;
        WarmCrossPageRmw(cpu, address);
        byte[] before = cpu.ReadMemory(address, 4);
        if (readOnly)
            cpu.ProtectMemory(SecondPage, CpuSandbox.PageSize, 1);
        else
            cpu.UnmapMemory(SecondPage, CpuSandbox.PageSize);
        cpu.Eip = CpuSandbox.CodeAddress;

        cpu.Run(CpuSandbox.CodeAddress + (uint)Code.Length);

        Assert.Equal(EmuStatus.Fault, cpu.Status);
        Assert.Equal(CpuSandbox.CodeAddress, cpu.Eip);
        int readable = readOnly ? before.Length : firstSize;
        Assert.Equal(before[..readable], cpu.ReadMemory(address, readable));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public unsafe void WarmCrossPageRmw_EnablingHookObservesUnsplitTransfers(int firstSize)
    {
        using var cpu = new CpuSandbox();
        uint address = SecondPage - (uint)firstSize;
        WarmCrossPageRmw(cpu, address);
        HookState observed = new() { Address = address };
        X86Native.SetMemHook(cpu.State, &ObserveMemory, (IntPtr)(&observed));
        cpu.Eip = CpuSandbox.CodeAddress;

        cpu.Run(CpuSandbox.CodeAddress + (uint)Code.Length);
        X86Native.SetMemHook(cpu.State, null, IntPtr.Zero);

        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(2, observed.Reads);
        Assert.Equal(1, observed.Writes);
        Assert.Equal(0x44332212ul, observed.FirstRead);
        Assert.Equal(0x44332213ul, observed.ValueWritten);
        Assert.Equal(0x44332213ul, observed.LastRead);
        Assert.Equal(0x44332213u, cpu[Reg.EAX]);
    }

    private static void WarmCrossPageRmw(CpuSandbox cpu, uint address)
    {
        cpu.MapMemory(SecondPage, CpuSandbox.PageSize);
        cpu.WriteMemory(address, [0x11, 0x22, 0x33, 0x44]);
        cpu[Reg.EDI] = address;
        cpu.WriteCode(Code);
        cpu.Run(CpuSandbox.CodeAddress + (uint)Code.Length);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(0x44332212u, cpu[Reg.EAX]);
    }

    private struct HookState
    {
        public uint Address;
        public int Reads;
        public int Writes;
        public ulong FirstRead;
        public ulong LastRead;
        public ulong ValueWritten;
    }

    [UnmanagedCallersOnly]
    private static unsafe void ObserveMemory(IntPtr state, uint address, uint size, int isWrite,
                                             ulong value, IntPtr userData)
    {
        var observed = (HookState*)userData;
        if (address != observed->Address || size != 4) return;
        if (isWrite != 0)
        {
            observed->Writes++;
            observed->ValueWritten = value;
        }
        else
        {
            if (observed->Reads++ == 0) observed->FirstRead = value;
            observed->LastRead = value;
        }
    }
}
