using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Simd;

public sealed class ShaInstructionTests
{
    // Independent reference vectors captured from qemu-i386 8.2.2.
    [Theory]
    [InlineData(0x38, 0xC8, -1, 0xFFC301EEu, 0x94126638u, 0xA4551754u, 0xEA730BC4u)]
    [InlineData(0x38, 0xC9, -1, 0xA5665233u, 0x231010F1u, 0x25060330u, 0x77777777u)]
    [InlineData(0x38, 0xCA, -1, 0x68403DFFu, 0xECD19802u, 0x604E40DFu, 0xB5135B98u)]
    [InlineData(0x38, 0xCB, -1, 0x31EC307Bu, 0x145F24AFu, 0x69C46173u, 0x7EC75B45u)]
    [InlineData(0x38, 0xCC, -1, 0x3E9111B3u, 0x8506D93Au, 0x24B557E8u, 0x01591466u)]
    [InlineData(0x38, 0xCD, -1, 0x2A9D194Cu, 0x1EB1FCECu, 0x53BA7DB2u, 0xC0C8DB89u)]
    [InlineData(0x3A, 0xCC, 0, 0x787E3BCFu, 0xC2160501u, 0xACF86D45u, 0x025FB078u)]
    [InlineData(0x3A, 0xCC, 1, 0x6876BAAFu, 0x838E71BCu, 0xAB319397u, 0xF7D50514u)]
    [InlineData(0x3A, 0xCC, 2, 0x05A88CA0u, 0xB49E10CDu, 0x1B898A61u, 0xE0D56B28u)]
    [InlineData(0x3A, 0xCC, 255, 0xBF58F03Cu, 0xB6B758F4u, 0x4FA73019u, 0xE24BD5CAu)]
    public void ShaInstructionsMatchRegisterAndUnalignedMemoryReference(
        byte map, byte opcode, int control, uint w0, uint w1, uint w2, uint w3)
    {
        foreach (bool memory in new[] { false, true })
        {
            using var cpu = SimdTestSupport.Create();
            const uint address = CpuSandbox.DataAddress + 1;
            var destination = SimdTestSupport.Lanes(32, 0x01334567, 0x89ABCDEF, 0x24354657, 0xFEDCBA98);
            var source = SimdTestSupport.Lanes(32, 0xFFC301EE, 0x94126638, 0xA4551754, 0xAABBDD1E);
            cpu.WriteXmm(0, SimdTestSupport.Lanes(32, 0x42664330, 0x9819C98D, 0x4ABFE5CD, 0x8AF5CB33));
            cpu.WriteXmm(1, source);
            cpu.WriteXmm(2, destination);
            cpu[Reg.EAX] = address;
            cpu.WriteMemory(address, source);
            byte modrm = memory ? (byte)0x10 : (byte)0xD1;
            byte[] code = control < 0 ? [0x0F, map, opcode, modrm] : [0x0F, map, opcode, modrm, (byte)control];
            SimdTestSupport.Execute(cpu, code, SimdTestSupport.Lanes(32, w0, w1, w2, w3), xmmDestination: 2);
        }
    }

    [Fact]
    public void ShaMemoryFaultLeavesDestinationAndFlagsUntouched()
    {
        using var cpu = SimdTestSupport.Create();
        const uint missingPage = CpuSandbox.DataAddress + CpuSandbox.PageSize;
        cpu[Reg.EAX] = missingPage - 8;
        byte[] destination = cpu.ReadXmm(2);
        uint flags = cpu.Eflags;
        cpu.WriteCode([0x0F, 0x38, 0xCB, 0x10]); // SHA256RNDS2 xmm2, [eax]

        Assert.Equal(EmuStatus.Fault, cpu.Step());
        Assert.Equal(14, cpu.FaultVector);
        Assert.Equal(missingPage, cpu.FaultAddress);
        Assert.Equal(CpuSandbox.CodeAddress, cpu.Eip);
        Assert.Equal(destination, cpu.ReadXmm(2));
        Assert.Equal(flags, cpu.Eflags);
    }

    [Theory]
    [InlineData(0x66)]
    [InlineData(0xF2)]
    [InlineData(0xF3)]
    public void ShaIgnoresLegacyOperandAndRepeatPrefixes(byte prefix)
    {
        using var cpu = SimdTestSupport.Create();
        cpu.WriteXmm(0, SimdTestSupport.Lanes(32, 0x42664330, 0x9819C98D, 0x4ABFE5CD, 0x8AF5CB33));
        cpu.WriteXmm(1, SimdTestSupport.Lanes(32, 0xFFC301EE, 0x94126638, 0xA4551754, 0xAABBDD1E));
        cpu.WriteXmm(2, SimdTestSupport.Lanes(32, 0x01334567, 0x89ABCDEF, 0x24354657, 0xFEDCBA98));
        SimdTestSupport.Execute(cpu, [prefix, 0x0F, 0x38, 0xCB, 0xD1],
            SimdTestSupport.Lanes(32, 0x31EC307B, 0x145F24AF, 0x69C46173, 0x7EC75B45), xmmDestination: 2);
    }

    [Fact]
    public void ShaRejectsLockPrefixBeforeAccessingMemory()
    {
        using var cpu = SimdTestSupport.Create();
        cpu[Reg.EAX] = 0x00500000; // Unmapped.
        cpu.WriteCode([0xF0, 0x0F, 0x38, 0xCB, 0x10]);
        Assert.Equal(EmuStatus.Fault, cpu.Step());
        Assert.Equal(6, cpu.FaultVector);
        Assert.Equal(CpuSandbox.CodeAddress, cpu.Eip);
    }

    [Fact]
    public void CpuidAdvertisesShaAndFxState()
    {
        using var cpu = new CpuSandbox();
        cpu.WriteCode([0x0F, 0xA2]); // CPUID
        cpu[Reg.EAX] = 7;
        cpu[Reg.ECX] = 0;
        Assert.Equal(EmuStatus.Running, cpu.Step());
        Assert.NotEqual(0u, cpu[Reg.EBX] & (1u << 29));

        cpu.Eip = CpuSandbox.CodeAddress;
        cpu[Reg.EAX] = 1;
        Assert.Equal(EmuStatus.Running, cpu.Step());
        Assert.NotEqual(0u, cpu[Reg.EDX] & (1u << 24));
    }

    [Fact]
    public void PshufbUsesLowNibblesAndZerosMaskedBytes()
    {
        using var cpu = SimdTestSupport.Create();
        byte[] source = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        cpu.WriteXmm(0, source);
        cpu.WriteXmm(1, [0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
                          0x88, 0x99, 0xAA, 0xBB, 0x0C, 0x7D, 0x7E, 0x7F]);
        SimdTestSupport.Execute(cpu, [0x66, 0x0F, 0x38, 0x00, 0xC1],
            [1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0, 0, 13, 14, 15, 16]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(255)]
    public void PalignrConcatenatesAndZeroExtends(byte count)
    {
        byte[] bytes = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        byte[] expected = new byte[16];
        bytes.Skip(count).Take(16).ToArray().CopyTo(expected, 0);
        foreach (bool memory in new[] { false, true })
        {
            using var cpu = SimdTestSupport.Create();
            cpu.WriteXmm(0, bytes[16..]);
            cpu.WriteXmm(1, bytes[..16]);
            cpu[Reg.EAX] = CpuSandbox.DataAddress;
            cpu.WriteMemory(CpuSandbox.DataAddress, bytes[..16]);
            byte modrm = memory ? (byte)0x00 : (byte)0xC1;
            SimdTestSupport.Execute(cpu, [0x66, 0x0F, 0x3A, 0x0F, modrm, count], expected);
        }
    }
}
