using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

/// <summary>
/// Regression tests for instructions whose register side effects interact with
/// the memory fault / SMC interception machinery:
///   * XADD must compute the store address before overwriting its source register.
///   * Byte XCHG must update its register after the store completes.
///   * Flags from earlier instructions must survive a memory-operation exit.
/// The SMC cases need External pages: ForceWriteSlow is only armed for pages
/// tracked in the MMU's external-alias table.
/// </summary>
public sealed class XchgXaddSmcTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Xadd_MemoryDestination_SourceRegisterAlsoAddress_UsesOriginalAddress(bool run)
    {
        using var cpu = new CpuSandbox();
        uint p = CpuSandbox.DataAddress + 0x100;
        uint q = 0x10;
        WriteU32(cpu, p, q);

        // xadd [eax], eax
        byte[] code = [0x0F, 0xC1, 0x00];
        cpu.WriteCode(code);
        cpu[Reg.EAX] = p;
        if (run) cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        else cpu.Step();

        Assert.NotEqual(EmuStatus.Fault, cpu.Status);
        Assert.Equal(unchecked(p + q), ReadU32(cpu, p));
        Assert.Equal(q, cpu[Reg.EAX]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Xadd_SibIndexSource_StoresAtOriginalEffectiveAddress(bool run)
    {
        using var cpu = new CpuSandbox();
        uint baseAddress = CpuSandbox.DataAddress + 0x200;
        uint index = 0x8;
        uint q = 0x20;
        uint p = baseAddress + index;
        WriteU32(cpu, p, q);

        // xadd [eax+ecx], ecx
        byte[] code = [0x0F, 0xC1, 0x0C, 0x08];
        cpu.WriteCode(code);
        cpu[Reg.EAX] = baseAddress;
        cpu[Reg.ECX] = index;
        if (run) cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        else cpu.Step();

        Assert.NotEqual(EmuStatus.Fault, cpu.Status);
        Assert.Equal(unchecked(q + index), ReadU32(cpu, p));   // dest = dest + src
        Assert.Equal(q, cpu[Reg.ECX]);                          // src = old dest
        Assert.Equal(0u, ReadU32(cpu, baseAddress + q));        // no store at base+overwritten-index
    }

    [Fact]
    public void Xadd_SetsArithmeticFlags()
    {
        using var cpu = new CpuSandbox();
        uint p = CpuSandbox.DataAddress + 0x120;
        WriteU32(cpu, p, 0xFFFF_FFFF);

        byte[] code = [0x0F, 0xC1, 0x08]; // xadd [eax], ecx
        cpu.WriteCode(code);
        cpu[Reg.EAX] = p;
        cpu[Reg.ECX] = 1;
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);

        Assert.False((cpu.Eflags & 0x1) == 0);  // CF
        Assert.False((cpu.Eflags & 0x40) == 0); // ZF
        Assert.Equal(0u, ReadU32(cpu, p));
        Assert.Equal(0xFFFF_FFFFu, cpu[Reg.ECX]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Xchg_Byte_ThroughSmcInterception_UpdatesRegister(bool run)
    {
        using var cpu = CreateExternalSandbox();
        uint target = CpuSandbox.CodeAddress + 0x100;
        cpu.WriteMemory(target, [0x00]);

        // xchg [0x00100100], al
        byte[] code = [0x86, 0x05, 0x00, 0x01, 0x10, 0x00];
        cpu.WriteCode(code);
        cpu[Reg.EAX] = 0x5A;
        if (run) cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        else cpu.Step();

        Assert.NotEqual(EmuStatus.Fault, cpu.Status);
        Assert.Equal((byte)0x5A, cpu.ReadMemory(target, 1)[0]);
        Assert.Equal((byte)0x00, cpu[Reg.EAX] & 0xFF);
    }

    [Fact]
    public void Xchg_Byte_OnWritablePage_UpdatesRegister()
    {
        using var cpu = new CpuSandbox();
        uint target = CpuSandbox.DataAddress + 0x300;
        cpu.WriteMemory(target, [0x00]);

        byte[] code = [0x86, 0x05, 0x00, 0x03, 0x20, 0x00]; // xchg [0x00200300], al
        cpu.WriteCode(code);
        cpu[Reg.EAX] = 0x5A;
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);

        Assert.Equal((byte)0x5A, cpu.ReadMemory(target, 1)[0]);
        Assert.Equal((byte)0x00, cpu[Reg.EAX] & 0xFF);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Flags_FromComparison_SurviveSmcStore(bool run)
    {
        using var cpu = CreateExternalSandbox();
        cpu.WriteMemory(CpuSandbox.DataAddress, [0xCC]);

        byte[] code =
        [
            0x39, 0xC0,                                     // cmp eax,eax  -> ZF=1
            0xC6, 0x05, 0x00, 0x01, 0x10, 0x00, 0x00,       // mov byte [0x00100100],0 (SMC)
            0x0F, 0x94, 0x05, 0x00, 0x00, 0x20, 0x00        // setz byte [0x00200000]
        ];
        cpu.WriteCode(code);
        cpu[Reg.EAX] = 0x1234;
        if (run) cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        else
        {
            for (var i = 0; i < 3; i++) cpu.Step();
        }

        Assert.NotEqual(EmuStatus.Fault, cpu.Status);
        Assert.Equal((byte)0x01, cpu.ReadMemory(CpuSandbox.DataAddress, 1)[0]);
    }

    private static CpuSandbox CreateExternalSandbox()
    {
        var cpu = new CpuSandbox(mapDefaults: false);
        cpu.MapExternalMemory(CpuSandbox.CodeAddress, 7);
        cpu.MapExternalMemory(CpuSandbox.DataAddress, 3);
        cpu.MapExternalMemory(CpuSandbox.StackAddress, 3);
        cpu[Reg.ESP] = CpuSandbox.StackAddress + CpuSandbox.PageSize - 16;
        return cpu;
    }

    private static void WriteU32(CpuSandbox cpu, uint address, uint value) =>
        cpu.WriteMemory(address, BitConverter.GetBytes(value));

    private static uint ReadU32(CpuSandbox cpu, uint address) =>
        BitConverter.ToUInt32(cpu.ReadMemory(address, 4));
}
