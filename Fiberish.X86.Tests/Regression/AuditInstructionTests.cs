using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

public sealed class AuditInstructionTests
{
    [Theory]
    [InlineData(0xD1, 0xD2, 1)] // rcl edx,1
    [InlineData(0xD1, 0xDA, 0x80000000u)] // rcr edx,1
    public void Group2_DfeKeepsInputCarry(byte opcode, byte modrm, uint expected)
    {
        using var cpu = new CpuSandbox();
        cpu.WriteCode([0x39, 0xC8, opcode, modrm]); // cmp eax,ecx -> CF=1
        cpu[Reg.EAX] = 0;
        cpu[Reg.ECX] = 1;
        cpu[Reg.EDX] = 0;
        cpu.Eflags = 0x202;
        cpu.Run(CpuSandbox.CodeAddress + 4);
        Assert.Equal(expected, cpu[Reg.EDX]);
    }

    [Theory]
    [InlineData(0xD1, 0xC8, -1)] // ror eax,1 preserves ZF/PF
    [InlineData(0xD3, 0xE0, 0)]  // shl eax,cl, CL=0 preserves all flags
    [InlineData(0xC0, 0xD0, 9)]  // rcl al,9: effective count zero
    [InlineData(0xC1, 0xE0, 32)] // shl eax,32: masked count zero
    public void Group2_DfeKeepsPreservedFlags(byte opcode, byte modrm, int count)
    {
        using var cpu = new CpuSandbox();
        byte[] shift = opcode is 0xC0 or 0xC1 ? [opcode, modrm, (byte)count] : [opcode, modrm];
        byte[] code = [0x39, 0xD2, .. shift, 0x0F, 0x94, 0xC3, 0x0F, 0x9A, 0xC7]; // cmp edx,edx; ...; setz bl; setp bh
        cpu.WriteCode(code);
        cpu[Reg.EAX] = 7;
        cpu[Reg.ECX] = count < 0 ? 0u : (uint)count;
        cpu.Eflags = 0x202;
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(0x0101u, cpu[Reg.EBX] & 0xFFFF);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CmovFalse_StillFaultsForUnreadableMemory(bool word)
    {
        using var cpu = new CpuSandbox();
        byte[] code = word ? [0x66, 0x0F, 0x44, 0x07] : [0x0F, 0x44, 0x07]; // cmovz ax/eax,[edi]
        cpu.WriteCode(code);
        cpu[Reg.EDI] = 0x400000;
        cpu[Reg.EAX] = 0x12345678;
        cpu.Eflags = 0x202; // ZF clear
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Fault, cpu.Status);
        Assert.Equal(14, cpu.FaultVector);
        Assert.Equal(CpuSandbox.CodeAddress, cpu.Eip);
        Assert.Equal(0x12345678u, cpu[Reg.EAX]);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void FailedCmpxchg_StillRequiresWritableDestination(int width)
    {
        using var cpu = new CpuSandbox();
        cpu.WriteMemory(CpuSandbox.DataAddress, [0x02, 0, 0, 0]);
        cpu.ProtectMemory(CpuSandbox.DataAddress, CpuSandbox.PageSize, 1);
        byte[] code = width == 16 ? [0x66, 0x0F, 0xB1, 0x0F] : [0x0F, width == 8 ? (byte)0xB0 : (byte)0xB1, 0x0F];
        cpu.WriteCode(code);
        cpu[Reg.EDI] = CpuSandbox.DataAddress;
        cpu[Reg.EAX] = 1; // unequal
        cpu[Reg.ECX] = 3;
        cpu.Eflags = 0xAD7;
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Fault, cpu.Status);
        Assert.Equal(CpuSandbox.CodeAddress, cpu.Eip);
        Assert.Equal(1u, cpu[Reg.EAX]);
        Assert.Equal(0xAD7u, cpu.Eflags & 0xFFF);
        Assert.Equal((byte)2, cpu.ReadMemory(CpuSandbox.DataAddress, 1)[0]);
    }

    public static IEnumerable<object[]> AlignedInstructions()
    {
        yield return [new byte[] { 0x0F, 0x28, 0x07 }]; // movaps xmm0,[edi]
        yield return [new byte[] { 0x66, 0x0F, 0x28, 0x07 }]; // movapd
        yield return [new byte[] { 0x0F, 0x29, 0x07 }]; // movaps [edi],xmm0
        yield return [new byte[] { 0x66, 0x0F, 0x29, 0x07 }]; // movapd store
        yield return [new byte[] { 0x66, 0x0F, 0x6F, 0x07 }]; // movdqa load
        yield return [new byte[] { 0x66, 0x0F, 0x7F, 0x07 }]; // movdqa store
        yield return [new byte[] { 0x0F, 0x58, 0x07 }]; // addps
        yield return [new byte[] { 0x66, 0x0F, 0x58, 0x07 }]; // addpd
        yield return [new byte[] { 0x0F, 0x54, 0x07 }]; // andps
        yield return [new byte[] { 0x66, 0x0F, 0xFC, 0x07 }]; // paddb
        yield return [new byte[] { 0x0F, 0x5B, 0x07 }]; // cvtdq2ps
        yield return [new byte[] { 0x0F, 0x2B, 0x07 }]; // movntps
        yield return [new byte[] { 0x66, 0x0F, 0xE7, 0x07 }]; // movntdq
    }

    [Theory]
    [MemberData(nameof(AlignedInstructions))]
    public void AlignedSse_MisalignmentRaisesGpWithoutSideEffects(byte[] code)
    {
        using var cpu = new CpuSandbox();
        cpu.WriteCode(code);
        cpu[Reg.EDI] = CpuSandbox.DataAddress + 1;
        byte[] xmm = Enumerable.Repeat((byte)0xA5, 16).ToArray();
        cpu.WriteXmm(0, xmm);
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Fault, cpu.Status);
        Assert.Equal(13, cpu.FaultVector);
        Assert.Equal(CpuSandbox.CodeAddress, cpu.Eip);
        Assert.Equal(xmm, cpu.ReadXmm(0));
        Assert.Equal(new byte[16], cpu.ReadMemory(CpuSandbox.DataAddress + 1, 16));
    }

    public static IEnumerable<object[]> UnalignedInstructions()
    {
        yield return [new byte[] { 0x0F, 0x10, 0x07 }]; // movups
        yield return [new byte[] { 0x66, 0x0F, 0x10, 0x07 }]; // movupd
        yield return [new byte[] { 0xF3, 0x0F, 0x6F, 0x07 }]; // movdqu
        yield return [new byte[] { 0xF3, 0x0F, 0x58, 0x07 }]; // addss
        yield return [new byte[] { 0xF2, 0x0F, 0x58, 0x07 }]; // addsd
    }

    [Theory]
    [MemberData(nameof(UnalignedInstructions))]
    public void UnalignedAndScalarSse_StillAcceptUnalignedMemory(byte[] code)
    {
        using var cpu = new CpuSandbox();
        cpu.WriteCode(code);
        cpu[Reg.EDI] = CpuSandbox.DataAddress + 1;
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PushaAndPopa_UseSsBaseAndOperandWidth(bool word)
    {
        using var cpu = new CpuSandbox();
        const uint sp = 0x800;
        X86Native.SegBaseWrite(cpu.State, (int)Seg.SS, CpuSandbox.StackAddress);
        cpu[Reg.ESP] = sp;
        cpu[Reg.EAX] = 0xAABB1234;
        cpu[Reg.EBP] = 0xCCDD5678;
        byte[] code = word ? [0x66, 0x60, 0x66, 0x61] : [0x60, 0x61];
        cpu.WriteCode(code);
        cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(sp, cpu[Reg.ESP]);
        Assert.Equal(0xAABB1234u, cpu[Reg.EAX]);
        Assert.Equal(0xCCDD5678u, cpu[Reg.EBP]);
        uint width = word ? 2u : 4u;
        Assert.Equal((byte)0x34, cpu.ReadMemory(CpuSandbox.StackAddress + sp - width, 1)[0]);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 34)] // nesting is masked to five bits
    public void EnterLeave_RespectWidthSsAndNesting(bool word, byte level)
    {
        using var cpu = new CpuSandbox();
        const uint sp = 0x900;
        const uint bp = 0x800;
        uint width = word ? 2u : 4u;
        X86Native.SegBaseWrite(cpu.State, (int)Seg.SS, CpuSandbox.StackAddress);
        cpu[Reg.ESP] = sp;
        cpu[Reg.EBP] = bp;
        cpu.WriteMemory(CpuSandbox.StackAddress + bp - width, BitConverter.GetBytes(0x7777u).AsSpan(0, (int)width));
        byte[] enter = word ? [0x66, 0xC8, 0x10, 0, level] : [0xC8, 0x10, 0, level];
        cpu.WriteCode(enter);
        cpu.Run(CpuSandbox.CodeAddress + (uint)enter.Length);
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(sp - width, cpu[Reg.EBP]);
        uint slots = (level & 31) == 0 ? 1u : (uint)(level & 31) + 1;
        Assert.Equal(sp - slots * width - 0x10, cpu[Reg.ESP]);
        cpu.WriteCode(word ? [0x66, 0xC9] : [0xC9]);
        cpu.Run(CpuSandbox.CodeAddress + (word ? 2u : 1u));
        Assert.Equal(EmuStatus.Stopped, cpu.Status);
        Assert.Equal(sp, cpu[Reg.ESP]);
        Assert.Equal(bp, cpu[Reg.EBP]);
    }
}
