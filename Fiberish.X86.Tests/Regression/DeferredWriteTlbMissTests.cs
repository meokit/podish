using System.Runtime.InteropServices;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

public sealed class DeferredWriteTlbMissTests
{
    [Theory]
    [InlineData("mov")]
    [InlineData("inc")]
    [InlineData("push")]
    [InlineData("cross-page")]
    [InlineData("rejected")]
    public unsafe void WriteMiss_PreservesRemapRestartAndFaultSemantics(string form)
    {
        using var cpu = new CpuSandbox(mapDefaults: false);
        cpu.MapMemory(CpuSandbox.CodeAddress, CpuSandbox.PageSize, 7);
        cpu.MapMemory(CpuSandbox.DataAddress, CpuSandbox.PageSize);
        bool crossPage = form == "cross-page";
        if (crossPage) cpu.MapMemory(CpuSandbox.DataAddress + CpuSandbox.PageSize, CpuSandbox.PageSize);
        uint address = CpuSandbox.DataAddress + (crossPage ? CpuSandbox.PageSize - 2 : 0);
        const uint oldValue = 0x11223344;
        const uint value = 0x89ABCDEF;
        const uint marker = 0x55667788;
        cpu.WriteMemory(address, BitConverter.GetBytes(oldValue));
        uint protectedPage = CpuSandbox.DataAddress + (crossPage ? CpuSandbox.PageSize : 0);
        byte[] replacement = cpu.ReadMemory(protectedPage, (int)CpuSandbox.PageSize);
        if (!crossPage) BitConverter.GetBytes(marker).CopyTo(replacement, 4);
        var oldBacking = X86Native.ResolvePtrForRead(cpu.State, protectedPage);
        Assert.True(oldBacking != null);
        byte[] originalBacking = new ReadOnlySpan<byte>(oldBacking, (int)CpuSandbox.PageSize).ToArray();
        cpu.ProtectMemory(protectedPage, CpuSandbox.PageSize, 1);

        byte[] code = form switch
        {
            "inc" => [0x8B, 0x1F, 0xFF, 0x07, 0x8B, 0x4F, 4, 0x8B, 0x2F],
            "push" => [0x8B, 0x1F, 0x50, 0x8B, 0x4F, 4, 0x8B, 0x2F],
            "cross-page" or "rejected" => [0x8B, 0x1F, 0x89, 0x07, 0x8B, 0x0F],
            _ => [0x8B, 0x1F, 0x89, 0x07, 0x8B, 0x4F, 4, 0x8B, 0x2F]
        };
        cpu.WriteCode(code);
        cpu[Reg.EAX] = value;
        cpu[Reg.EDI] = address;
        cpu[Reg.ESP] = CpuSandbox.DataAddress + 4;
        cpu.Eflags = 0xAD7;
        var context = new FaultContext(cpu, protectedPage, replacement, form == "rejected");
        var root = GCHandle.Alloc(context);
        try
        {
            X86Native.SetFaultCallback(cpu.State, &OnWriteFault, GCHandle.ToIntPtr(root));
            cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
            Assert.Null(context.Error);
            Assert.Equal(1, context.Invocations);
            Assert.Equal(oldValue, cpu[Reg.EBX]); // The read before the write warmed the old mapping.
            Assert.Equal(originalBacking, new ReadOnlySpan<byte>(oldBacking, (int)CpuSandbox.PageSize).ToArray());
            if (form == "rejected")
            {
                Assert.Equal(EmuStatus.Fault, cpu.Status);
                Assert.Equal(CpuSandbox.CodeAddress + 2, cpu.Eip);
            }
            else
            {
                Assert.Equal(EmuStatus.Stopped, cpu.Status);
                Assert.Equal(CpuSandbox.CodeAddress + (uint)code.Length, cpu.Eip);
                uint expected = form == "inc" ? oldValue + 1 : value;
                Assert.Equal(expected, BitConverter.ToUInt32(cpu.ReadMemory(address, 4)));
                if (crossPage) Assert.Equal(value, cpu[Reg.ECX]);
                else
                {
                    Assert.Equal(marker, cpu[Reg.ECX]);
                    Assert.Equal(expected, cpu[Reg.EBP]);
                    if (form == "push") Assert.Equal(CpuSandbox.DataAddress, cpu[Reg.ESP]);
                }
            }
            if (form != "inc") Assert.Equal(0xAD7u & 0x8D5u, cpu.Eflags & 0x8D5u);
        }
        finally
        {
            X86Native.SetFaultCallback(cpu.State, null, IntPtr.Zero);
            root.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static int OnWriteFault(IntPtr state, uint address, int isWrite, IntPtr userdata)
    {
        var context = (FaultContext)GCHandle.FromIntPtr(userdata).Target!;
        try
        {
            if (context.Invocations++ != 0 || state != context.Cpu.State || isWrite != 1 ||
                (address & ~(CpuSandbox.PageSize - 1)) != context.Page)
                throw new InvalidOperationException($"Unexpected write fault at {address:X8}.");
            if (context.Reject) return 0;
            context.Cpu.ReplaceBackingPage(context.Page, context.Replacement, 3);
            return 1;
        }
        catch (Exception error)
        {
            context.Error = error;
            X86Native.EmuFault(state);
            return 0;
        }
    }

    private sealed class FaultContext(CpuSandbox cpu, uint page, byte[] replacement, bool reject)
    {
        public CpuSandbox Cpu { get; } = cpu;
        public uint Page { get; } = page;
        public byte[] Replacement { get; } = replacement;
        public bool Reject { get; } = reject;
        public int Invocations { get; set; }
        public Exception? Error { get; set; }
    }
}
