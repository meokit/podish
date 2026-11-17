using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;
namespace Fiberish.X86.Tests.Regression;
public sealed class CacheSignatureProbes
{
    [Fact]
    public void CachedStackBaseAndPartialWritesStayCanonical()
    {
        using var cpu = new CpuSandbox();
        cpu[Reg.ESP] = CpuSandbox.StackAddress + 0x900;
        cpu.WriteMemory(cpu[Reg.ESP] + 4, BitConverter.GetBytes(0x12345678u));
        cpu.WriteMemory(CpuSandbox.StackAddress + 0x804, BitConverter.GetBytes(0xFEDCBA98u));
        byte[] code = [0x8B,0x44,0x24,4, 0x66,0xBC,0,8, 0x8B,0x44,0x24,4,
            0x66,0xB8,0x34,0x12, 0x50,0x5A];
        cpu.WriteCode(code); cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(0xFEDC1234u, cpu[Reg.EAX]); Assert.Equal(0xFEDC1234u, cpu[Reg.EDX]);
        Assert.Equal(CpuSandbox.StackAddress + 0x800, cpu[Reg.ESP]);
        AssertStackLoadClone(cpu);
    }
    [Fact]
    public void FaultDoesNotPublishDestinationAndApiStackMutationIsSeenOnResume()
    {
        using var cpu = new CpuSandbox(); const uint unmapped = 0x50000000;
        cpu[Reg.ESP] = unmapped; cpu[Reg.EAX] = 0xABCDEF01;
        cpu.WriteCode([0x8B,0x44,0x24,4]); cpu.Run(CpuSandbox.CodeAddress + 4);
        Assert.Equal(EmuStatus.Fault, cpu.Status); Assert.Equal(14, cpu.FaultVector);
        Assert.Equal(CpuSandbox.CodeAddress, cpu.Eip); Assert.Equal(0xABCDEF01u, cpu[Reg.EAX]);
        Assert.Equal(unmapped, cpu[Reg.ESP]); AssertStackLoadClone(cpu);
        cpu[Reg.ESP] = CpuSandbox.StackAddress + 0x800;
        cpu.WriteMemory(cpu[Reg.ESP] + 4, BitConverter.GetBytes(0x76543210u));
        cpu.Run(CpuSandbox.CodeAddress + 4);
        Assert.Equal(EmuStatus.Stopped, cpu.Status); Assert.Equal(0x76543210u, cpu[Reg.EAX]);
        Assert.Equal(CpuSandbox.StackAddress + 0x800, cpu[Reg.ESP]);
    }
    [Fact]
    public void ApiMutationBetweenRunsRefreshesBothCachedGuests()
    {
        using var cpu = new CpuSandbox(); byte[] code = [0x8B,0x44,0x24,4];
        for (uint i=0;i<8;i++)
        {
            cpu[Reg.EAX] = 0xAAAAAAAA; cpu[Reg.ESP] = CpuSandbox.StackAddress + 0x800 + i*16;
            cpu.WriteMemory(cpu[Reg.ESP] + 4, BitConverter.GetBytes(0x10000000u+i));
            cpu.WriteCode(code); cpu.Run(CpuSandbox.CodeAddress + 4);
            Assert.Equal(0x10000000u+i, cpu[Reg.EAX]);
            Assert.Equal(CpuSandbox.StackAddress + 0x800 + i*16, cpu[Reg.ESP]);
            AssertStackLoadClone(cpu);
        }
    }
    private static unsafe void AssertStackLoadClone(CpuSandbox cpu)
    {
        int count = X86Native.GetBlockCount(cpu.State); var blocks = new IntPtr[count];
        fixed (IntPtr* p = blocks) X86Native.GetBlockList(cpu.State,p,count);
        foreach (var p in blocks)
        {
            var block=(X86Native.BasicBlock*)p;
            if (block->start_eip!=CpuSandbox.CodeAddress) continue;
            var symbol=X86Native.GetHandlerSymbolById(X86Native.GetHandlerId(block->entry));
            Assert.Contains("RegCacheClone_014_OpMov_Load_Eax_EspBaseNoIndexNoSegment",symbol); return;
        }
        Assert.Fail("Root block missing");
    }
}
