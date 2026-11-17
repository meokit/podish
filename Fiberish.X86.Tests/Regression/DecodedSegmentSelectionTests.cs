using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

public sealed class DecodedSegmentSelectionTests
{
    public static IEnumerable<object[]> MemoryForms()
    {
        yield return [new byte[] { 0x8B, 0x7D, 0 }, Seg.SS, 0x80u]; // [ebp]
        yield return [new byte[] { 0x8B, 0x3C, 0x24 }, Seg.SS, 0x80u]; // [esp]
        yield return [new byte[] { 0x8B, 0x38 }, Seg.DS, 0x80u]; // [eax]
        yield return [new byte[] { 0x8B, 0x3C, 0x2D, 0, 0, 0, 0 }, Seg.DS, 0x80u]; // EBP index, no base
        yield return [new byte[] { 0x8B, 0x3C, 0x28 }, Seg.DS, 0x100u]; // [eax+ebp]
        yield return [new byte[] { 0x67, 0x8B, 0x3A }, Seg.SS, 0x90u]; // [bp+si]
        yield return [new byte[] { 0x67, 0x8B, 0x38 }, Seg.DS, 0x90u]; // [bx+si]
        yield return [new byte[] { 0x67, 0x8B, 0x3E, 0x80, 0 }, Seg.DS, 0x80u]; // disp16
        yield return [new byte[] { 0x64, 0x8B, 0x7D, 0 }, Seg.FS, 0x80u];
        yield return [new byte[] { 0x65, 0x8B, 0x7D, 0 }, Seg.GS, 0x80u];
        yield return [new byte[] { 0x3E, 0x8B, 0x7D, 0 }, Seg.DS, 0x80u]; // DS override of BP's SS
        yield return [new byte[] { 0x36, 0x8B, 0x38 }, Seg.SS, 0x80u]; // SS override of EAX's DS
    }

    [Theory]
    [MemberData(nameof(MemoryForms))]
    public void CachedOperand_UsesDecodedSegmentChoiceButLiveSegmentBase(byte[] code, Seg segment, uint offset)
    {
        using var cpu = new CpuSandbox();
        const uint firstBase = CpuSandbox.DataAddress;
        const uint secondBase = firstBase + CpuSandbox.PageSize;
        cpu.MapMemory(secondBase, CpuSandbox.PageSize);
        cpu.WriteMemory(firstBase + offset, BitConverter.GetBytes(0x11223344u));
        cpu.WriteMemory(secondBase + offset, BitConverter.GetBytes(0x55667788u));
        cpu[Reg.EAX] = cpu[Reg.EBX] = cpu[Reg.EBP] = cpu[Reg.ESP] = 0x80;
        cpu[Reg.ESI] = 0x10;
        X86Native.SegBaseWrite(cpu.State, (int)segment, firstBase);
        cpu.WriteCode(code);
        Run();
        Assert.Equal(0x11223344u, cpu[Reg.EDI]);

        // Do not rewrite code or invalidate its cached translation. Resolving
        // the segment at decode time must not freeze its runtime base value.
        X86Native.SegBaseWrite(cpu.State, (int)segment, secondBase);
        cpu.Eip = CpuSandbox.CodeAddress;
        Run();
        Assert.Equal(0x55667788u, cpu[Reg.EDI]);

        void Run()
        {
            cpu.Run(CpuSandbox.CodeAddress + (uint)code.Length);
            Assert.Equal(EmuStatus.Stopped, cpu.Status);
        }
    }
}
