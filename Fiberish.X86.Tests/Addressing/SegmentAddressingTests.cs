using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Addressing;

public sealed class SegmentAddressingTests
{
    public static IEnumerable<object[]> SegmentCases()
    {
        yield return new object[] { "EBX defaults to DS", new byte[] { 0x8B, 0x13 }, Seg.DS, 0x180u };
        yield return new object[] { "EBP defaults to SS", new byte[] { 0x8B, 0x55, 0 }, Seg.SS, 0x180u };
        yield return new object[] { "ESP defaults to SS", new byte[] { 0x8B, 0x14, 0x24 }, Seg.SS, 0x180u };
        yield return new object[] { "SIB EBP base defaults to SS", new byte[] { 0x8B, 0x54, 0x0D, 0 }, Seg.SS, 0x180u };
        yield return new object[] { "SIB EBP index does not select SS", new byte[] { 0x8B, 0x14, 0x2B }, Seg.DS, 0x300u };
        yield return new object[] { "SIB no base with EBP index defaults to DS", new byte[] { 0x8B, 0x14, 0x2D, 0, 0, 0, 0 }, Seg.DS, 0x180u };
        yield return new object[] { "disp32 defaults to DS", new byte[] { 0x8B, 0x15, 0x80, 1, 0, 0 }, Seg.DS, 0x180u };
        yield return new object[] { "SS overrides EBX default", new byte[] { 0x36, 0x8B, 0x13 }, Seg.SS, 0x180u };
        yield return new object[] { "DS overrides EBP default", new byte[] { 0x3E, 0x8B, 0x55, 0 }, Seg.DS, 0x180u };
        yield return new object[] { "FS overrides DS", new byte[] { 0x64, 0x8B, 0x13 }, Seg.FS, 0x180u };
        yield return new object[] { "GS overrides SS", new byte[] { 0x65, 0x8B, 0x55, 0 }, Seg.GS, 0x180u };
        yield return new object[] { "FS overrides ESP default", new byte[] { 0x64, 0x8B, 0x14, 0x24 }, Seg.FS, 0x180u };
        yield return new object[] { "GS absolute disp32", new byte[] { 0x65, 0x8B, 0x15, 0x80, 1, 0, 0 }, Seg.GS, 0x180u };
    }

    [Theory]
    [MemberData(nameof(SegmentCases))]
    public void SegmentBase_IsAddedAfterEffectiveAddress(string name, byte[] code, Seg expectedSegment, uint offset)
    {
        using var cpu = AddressingTestSupport.Create();
        cpu.MapMemory(0x400000, 0x1000);
        cpu.MapMemory(0x500000, 0x1000);
        cpu[Reg.EBX] = 0x180;
        cpu[Reg.EBP] = 0x180;
        cpu[Reg.ESP] = 0x180;
        cpu[Reg.ECX] = 0;
        foreach (Seg segment in new[] { Seg.DS, Seg.SS, Seg.FS, Seg.GS })
        {
            uint baseAddress = Base(segment);
            X86Native.SegBaseWrite(cpu.State, (int)segment, baseAddress);
            cpu.WriteMemory(baseAddress + offset, AddressingTestSupport.Word(0xA0B00000u | (uint)segment));
        }
        AddressingTestSupport.Execute(cpu, code,
            $"{name}; segment={expectedSegment} offset={offset:X8} width=32",
            (Reg.EDX, 0xA0B00000u | (uint)expectedSegment));
    }

    [Theory]
    [InlineData(0x64, Seg.FS)]
    [InlineData(0x65, Seg.GS)]
    public void SegmentBaseAddition_WrapsAt32Bits(int prefix, Seg segment)
    {
        using var cpu = AddressingTestSupport.Create();
        cpu[Reg.EBX] = CpuSandbox.DataAddress + 0x30;
        X86Native.SegBaseWrite(cpu.State, (int)segment, 0xFFFFFFF0);
        cpu.WriteMemory(CpuSandbox.DataAddress + 0x20, AddressingTestSupport.Word(0xF00DBAAD));
        AddressingTestSupport.Execute(cpu, new byte[] { (byte)prefix, 0x8B, 0x13 },
            $"{segment} base=FFFFFFF0 effective={cpu[Reg.EBX]:X8} width=32", (Reg.EDX, 0xF00DBAAD));
    }

    [Theory]
    [InlineData(0x64, Seg.FS)]
    [InlineData(0x65, Seg.GS)]
    public void Lea_DoesNotAddSegmentBaseOrReadMemory(int prefix, Seg segment)
    {
        using var cpu = AddressingTestSupport.Create();
        cpu[Reg.EBX] = 0xDEADBEEF; // Deliberately unmapped.
        X86Native.SegBaseWrite(cpu.State, (int)segment, CpuSandbox.DataAddress);
        AddressingTestSupport.Execute(cpu, new byte[] { (byte)prefix, 0x8D, 0x13 },
            $"LEA ignores {segment} and memory permissions width=32", (Reg.EDX, 0xDEADBEEF));
    }

    private static uint Base(Seg segment) => segment switch
    {
        Seg.DS => CpuSandbox.DataAddress,
        Seg.SS => CpuSandbox.StackAddress,
        Seg.FS => 0x400000,
        Seg.GS => 0x500000,
        _ => throw new ArgumentOutOfRangeException(nameof(segment))
    };
}
