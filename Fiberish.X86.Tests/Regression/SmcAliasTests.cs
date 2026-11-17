using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

public sealed class SmcAliasTests
{
    private const uint AliasAddress = 0x00900000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DistantWritableAlias_ModifiesNextInstructionInAlreadyDecodedBlock(bool crossPage)
    {
        using var cpu = new CpuSandbox(mapDefaults: false);
        cpu.MapExternalMemory(CpuSandbox.CodeAddress, 7);
        cpu.MapExternalMemory(CpuSandbox.CodeAddress + CpuSandbox.PageSize, 7);
        cpu.MapExternalAlias(AliasAddress, CpuSandbox.CodeAddress);
        cpu.MapExternalAlias(AliasAddress + CpuSandbox.PageSize, CpuSandbox.CodeAddress + CpuSandbox.PageSize);

        // The first instruction overwrites the immediate of the next instruction.
        // A cross-page dword store starts one byte before the boundary. Both
        // instructions have already been decoded when the store executes.
        uint start = crossPage ? CpuSandbox.CodeAddress + CpuSandbox.PageSize - 7 : CpuSandbox.CodeAddress;
        uint patch = start + 6;
        uint aliasPatch = AliasAddress + patch - CpuSandbox.CodeAddress;
        byte[] code = crossPage
            ? [0x89, 0x0F, 0x90, 0x90, 0x90, 0xB8, 0x01, 0x00, 0x00, 0x00]
            : [0xC6, 0x07, 0x02, 0xB8, 0x01, 0x00, 0x00, 0x00];
        if (!crossPage)
        {
            patch = start + 4;
            aliasPatch = AliasAddress + 4;
        }
        cpu.WriteMemory(start, code);
        // Initialization is complete before changing the execution view to RX.
        cpu.ProtectMemory(CpuSandbox.CodeAddress, 2 * CpuSandbox.PageSize, 5);
        cpu.Eip = start;
        cpu[Reg.EDI] = aliasPatch;
        cpu[Reg.ECX] = 2;

        cpu.Run(start + (uint)code.Length);

        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal((byte)2, cpu.ReadMemory(patch, 1)[0]);
        Assert.Equal(2u, cpu[Reg.EAX]); // stale decoded immediate would produce 1
        Assert.Equal(start + (uint)code.Length, cpu.Eip);

        // A later entry must also see the new bytes (cache invalidation alone
        // can pass this check while still failing the first, active-chain check).
        cpu.Eip = crossPage ? start + 5 : start + 3;
        cpu[Reg.EAX] = 0;
        cpu.Run(start + (uint)code.Length);
        Assert.Equal(2u, cpu[Reg.EAX]);
    }

    [Fact]
    public void CrossPageStore_OnlySecondFragmentAliasesActiveCode()
    {
        using var cpu = new CpuSandbox(mapDefaults: false);
        cpu.MapExternalMemory(CpuSandbox.CodeAddress, 7);
        cpu.MapExternalMemory(CpuSandbox.CodeAddress + CpuSandbox.PageSize, 7);
        // Unlike the first test, the first writable page is ordinary data and
        // is never SMC-armed. Only the store's second fragment aliases code.
        cpu.MapExternalMemory(AliasAddress, 3);
        cpu.MapExternalAlias(AliasAddress + CpuSandbox.PageSize, CpuSandbox.CodeAddress + CpuSandbox.PageSize);
        uint start = CpuSandbox.CodeAddress + CpuSandbox.PageSize - 2;
        byte[] code = [0x89, 0x0F, 0xB8, 0x01, 0x00, 0x00, 0x00]; // mov [edi],ecx; mov eax,1
        cpu.WriteMemory(start, code);
        cpu.ProtectMemory(CpuSandbox.CodeAddress, 2 * CpuSandbox.PageSize, 5);
        uint target = AliasAddress + CpuSandbox.PageSize - 1;
        cpu.Eip = start;
        cpu[Reg.EDI] = target;
        cpu[Reg.ECX] = 0x0002B8AA; // data byte AA; next opcode B8; new immediate 2

        cpu.Run(start + (uint)code.Length);

        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(new byte[] { 0xAA, 0xB8, 0x02, 0x00 }, cpu.ReadMemory(target, 4));
        Assert.Equal(2u, cpu[Reg.EAX]);
    }
}
