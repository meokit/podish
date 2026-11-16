using System.Buffers.Binary;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Xunit;

namespace Fiberish.X86.Tests.Isolation;

public sealed class FxStateTests
{
    [Fact]
    public void Fxsave_UsesLegacyLayoutLogicalStOrderAndPhysicalAbridgedTags()
    {
        using var cpu = new CpuSandbox();
        Initialize(cpu);
        const int top = 3;
        byte[] source = Image(top);
        cpu.Fcw = U16(source, 0);
        cpu.Fsw = U16(source, 2);
        cpu.Ftw = FullTags(top);
        cpu.Mxcsr = U32(source, 24);
        for (int logical = 0; logical < 8; logical++)
            cpu.WriteFpu(logical, source.AsSpan(32 + logical * 16, 10));
        const uint address = CpuSandbox.DataAddress + 0x100;
        byte[] untouched = Enumerable.Repeat((byte)0xA5, 544).ToArray();
        cpu.WriteMemory(address - 16, untouched);
        cpu[Reg.EAX] = address;
        cpu.WriteCode([0x0F, 0xAE, 0x00]); // FXSAVE [EAX]
        CpuSnapshot before = cpu.Snapshot();

        Assert.Equal(EmuStatus.Running, cpu.Step());
        Assert.Equal(CpuSandbox.CodeAddress + 3, cpu.Eip);
        AssertUnchanged(before, cpu);
        byte[] saved = cpu.ReadMemory(address, 512);
        Assert.Equal(cpu.Fcw, U16(saved, 0));
        Assert.Equal(cpu.Fsw, U16(saved, 2));
        Assert.Equal(source[4], saved[4]);
        Assert.Equal(cpu.Mxcsr, U32(saved, 24));
        Assert.Equal(0xFFFFu, U32(saved, 28));
        for (int logical = 0; logical < 8; logical++)
            Assert.Equal(cpu.ReadFpu(logical), saved.AsSpan(32 + logical * 16, 10).ToArray());
        for (int index = 0; index < 8; index++)
            Assert.Equal(cpu.ReadXmm(index), saved.AsSpan(160 + index * 16, 16).ToArray());
        // Undefined reserved outputs are deliberately not asserted. The final
        // 48 bytes are software-owned, however, and must not be overwritten.
        Assert.All(saved.AsSpan(464, 48).ToArray(), value => Assert.Equal((byte)0xA5, value));
        Assert.All(cpu.ReadMemory(address - 16, 16), value => Assert.Equal((byte)0xA5, value));
        Assert.All(cpu.ReadMemory(address + 512, 16), value => Assert.Equal((byte)0xA5, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Fxrstor_RestoresAll80BitsTagsMetadataAndXmmWithoutChangingGprs(int top)
    {
        using var cpu = new CpuSandbox();
        Initialize(cpu);
        byte[] image = Image(top);
        const uint input = CpuSandbox.DataAddress;
        const uint output = CpuSandbox.DataAddress + 0x400;
        cpu.WriteMemory(input, image);
        cpu[Reg.EAX] = input;
        cpu[Reg.EBX] = output;
        cpu.WriteCode([0x0F, 0xAE, 0x08]); // FXRSTOR [EAX]
        uint[] registers = cpu.Snapshot().Registers;
        uint flags = cpu.Eflags;

        Assert.Equal(EmuStatus.Running, cpu.Step());
        Assert.Equal(registers, cpu.Snapshot().Registers);
        Assert.Equal(flags, cpu.Eflags);
        Assert.Equal(U16(image, 0), cpu.Fcw);
        Assert.Equal(U16(image, 2), cpu.Fsw);
        Assert.Equal(FullTags(top), cpu.Ftw);
        Assert.Equal(U32(image, 24), cpu.Mxcsr);
        for (int logical = 0; logical < 8; logical++)
            Assert.Equal(image.AsSpan(32 + logical * 16, 10).ToArray(), cpu.ReadFpu(logical));
        for (int index = 0; index < 8; index++)
            Assert.Equal(image.AsSpan(160 + index * 16, 16).ToArray(), cpu.ReadXmm(index));

        // Changing TOP must expose the same physical ring, not a reordered array.
        cpu.Fsw = (ushort)((cpu.Fsw & ~0x3800) | (((top + 1) & 7) << 11));
        for (int logical = 0; logical < 8; logical++)
            Assert.Equal(image.AsSpan(32 + ((logical + 1) & 7) * 16, 10).ToArray(), cpu.ReadFpu(logical));
        Assert.Equal(FullTags(top), cpu.Ftw);
        cpu.Fsw = U16(image, 2);

        cpu.WriteCode([0x0F, 0xAE, 0x03]); // FXSAVE [EBX]
        Assert.Equal(EmuStatus.Running, cpu.Step());
        byte[] saved = cpu.ReadMemory(output, 512);
        Assert.Equal(U16(image, 6), U16(saved, 6));
        Assert.Equal(U32(image, 8), U32(saved, 8)); // FIP restored even when ES=0
        Assert.Equal(U32(image, 16), U32(saved, 16));
        Assert.Equal(image[4], saved[4]);
        Assert.Equal(registers, cpu.Snapshot().Registers);
        Assert.Equal(flags, cpu.Eflags);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 8)]
    [InlineData(true, 1)]
    [InlineData(true, 8)]
    public void FxState_MisalignmentRaisesGpBeforeChangingStateOrMemory(bool restore, int misalignment)
    {
        using var cpu = new CpuSandbox();
        Initialize(cpu);
        uint address = CpuSandbox.DataAddress + (uint)misalignment;
        byte[] memory = Enumerable.Repeat((byte)0xA5, 512).ToArray();
        cpu.WriteMemory(address, memory);
        cpu[Reg.EAX] = address;
        cpu.WriteCode([0x0F, 0xAE, restore ? (byte)0x08 : (byte)0x00]);
        CpuSnapshot before = cpu.Snapshot();

        Assert.Equal(EmuStatus.Fault, cpu.Step());
        Assert.Equal(13, cpu.FaultVector);
        Assert.Equal(before.Eip, cpu.Eip);
        AssertUnchanged(before, cpu);
        Assert.Equal(memory, cpu.ReadMemory(address, 512));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FxState_PageFaultIsPreciseAndRestoreDoesNotPartiallyCommit(bool restore)
    {
        using var cpu = new CpuSandbox();
        Initialize(cpu);
        const uint address = CpuSandbox.DataAddress + CpuSandbox.PageSize - 256;
        cpu.WriteMemory(address, Image(5).AsSpan(0, 256));
        cpu[Reg.EAX] = address;
        cpu.WriteCode([0x0F, 0xAE, restore ? (byte)0x08 : (byte)0x00]);
        CpuSnapshot before = cpu.Snapshot();

        Assert.Equal(EmuStatus.Fault, cpu.Step());
        Assert.Equal(14, cpu.FaultVector);
        Assert.Equal(CpuSandbox.DataAddress + CpuSandbox.PageSize, cpu.FaultAddress);
        Assert.Equal(before.Eip, cpu.Eip);
        AssertUnchanged(before, cpu);
    }

    [Fact]
    public void Fxsave_WriteProtectedDestinationRaisesPageFault()
    {
        using var cpu = new CpuSandbox();
        Initialize(cpu);
        byte[] memory = Image(3);
        cpu.WriteMemory(CpuSandbox.DataAddress, memory);
        cpu.ProtectMemory(CpuSandbox.DataAddress, CpuSandbox.PageSize, 1);
        cpu[Reg.EAX] = CpuSandbox.DataAddress;
        cpu.WriteCode([0x0F, 0xAE, 0x00]);
        CpuSnapshot before = cpu.Snapshot();

        Assert.Equal(EmuStatus.Fault, cpu.Step());
        Assert.Equal(14, cpu.FaultVector);
        Assert.Equal(CpuSandbox.DataAddress, cpu.FaultAddress);
        Assert.Equal(before.Eip, cpu.Eip);
        AssertUnchanged(before, cpu);
        Assert.Equal(memory, cpu.ReadMemory(CpuSandbox.DataAddress, 512));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReservedMxcsrBitsRaiseGpWithoutCommittingAnyState(bool fxrstor)
    {
        using var cpu = new CpuSandbox();
        Initialize(cpu);
        byte[] image = Image(5);
        Put32(image, fxrstor ? 24 : 0, 0x00011F80);
        cpu.WriteMemory(CpuSandbox.DataAddress, image);
        cpu[Reg.EAX] = CpuSandbox.DataAddress;
        cpu.WriteCode([0x0F, 0xAE, fxrstor ? (byte)0x08 : (byte)0x10]);
        CpuSnapshot before = cpu.Snapshot();

        Assert.Equal(EmuStatus.Fault, cpu.Step());
        Assert.Equal(13, cpu.FaultVector);
        Assert.Equal(before.Eip, cpu.Eip);
        AssertUnchanged(before, cpu);
        Assert.Equal(image, cpu.ReadMemory(CpuSandbox.DataAddress, 512));
    }

    [Theory]
    [InlineData(0x00001F80u)]
    [InlineData(0x00003F80u)]
    [InlineData(0x0000FFFFu)]
    public void Ldmxcsr_AcceptsSupportedControlAndStickyFlagBits(uint value)
    {
        using var cpu = new CpuSandbox();
        Initialize(cpu);
        byte[] memory = new byte[4];
        Put32(memory, 0, value);
        cpu.WriteMemory(CpuSandbox.DataAddress, memory);
        cpu[Reg.EAX] = CpuSandbox.DataAddress;
        cpu.WriteCode([0x0F, 0xAE, 0x10]); // LDMXCSR [EAX]
        CpuSnapshot before = cpu.Snapshot();

        Assert.Equal(EmuStatus.Running, cpu.Step());
        AssertUnchanged(before with { Mxcsr = value }, cpu);
        Assert.Equal(CpuSandbox.CodeAddress + 3, cpu.Eip);
    }

    [Fact]
    public void Fxrstor_RoundingAppliesToFollowingX87InsideRunAndHostEnvironmentIsRestored()
    {
        using var cpu = new CpuSandbox();
        cpu.Fcw = 0x0B7F; // Previous guest RC=up must not survive FXRSTOR.
        byte[] image = Image(0); // Restores RC=down and ST0=1.5.
        cpu.WriteMemory(CpuSandbox.DataAddress, image);
        cpu[Reg.EAX] = CpuSandbox.DataAddress;
        cpu.WriteCode([0x0F, 0xAE, 0x08, 0xD9, 0xFC]); // FXRSTOR [EAX]; FRNDINT
        ulong environment = X86Native.DebugGetFloatEnvironment();

        try
        {
            cpu.Run(CpuSandbox.CodeAddress + 5);

            Assert.Equal(environment, X86Native.DebugGetFloatEnvironment());
            Assert.Equal(EmuStatus.Stopped, cpu.Status);
            Assert.Equal(0x077F, cpu.Fcw);
            Assert.Equal(Ext80(0x8000000000000000, 0x3FFF), cpu.ReadFpu(0)); // 1.0, not 2.0
        }
        finally
        {
            X86Native.DebugSetFloatEnvironment(environment);
        }
    }

    private static void Initialize(CpuSandbox cpu)
    {
        for (int index = 0; index < 8; index++)
        {
            cpu[(Reg)index] = 0x12340000u + (uint)index * 0x010101u;
            cpu.WriteXmm(index, Xmm(index));
            cpu.WriteFpu(index, Ext80(0x8000000000000000UL + (uint)index, (ushort)(0x4000 + index)));
        }
        cpu.Fcw = 0x037F;
        cpu.Fsw = 0x1800;
        cpu.Ftw = 0;
        cpu.Mxcsr = 0x1F80;
        cpu.Eflags = 0xAD7;
    }

    private static byte[] Image(int top)
    {
        byte[] image = Enumerable.Repeat((byte)0xA5, 512).ToArray();
        Put16(image, 0, 0x077F);
        Put16(image, 2, (ushort)(0x4500 | (top << 11))); // ES=0, nonzero condition codes
        image[4] = (byte)~(1 << ((top + 7) & 7)); // Logical ST7 is empty.
        Put16(image, 6, 0x0321);
        Put32(image, 8, 0x61626364);
        Put32(image, 16, 0x71727374);
        Put32(image, 24, 0x3F80);
        Put32(image, 28, 0); // FXRSTOR ignores the saved MXCSR_MASK field.
        byte[][] registers =
        [
            Ext80(0xC000000000000000, 0x3FFF), // Valid 1.5
            Ext80(0, 0),                    // Zero
            Ext80(1, 0),                    // Denormal
            Ext80(0x8000000000000000, 0x7FFF), // Infinity
            Ext80(0xC000000000000123, 0xFFFF), // Negative quiet NaN, payload retained
            Ext80(0x123456789ABCDEF0, 0x3FFF), // Unsupported unnormal
            Ext80(0, 0x8000),               // Negative zero
            Ext80(0xFEDCBA9876543210, 0xC002) // Empty slot still retains all payload bits
        ];
        for (int logical = 0; logical < 8; logical++)
        {
            registers[logical].CopyTo(image, 32 + logical * 16);
            Xmm(logical).CopyTo(image, 160 + logical * 16);
        }
        return image;
    }

    private static ushort FullTags(int top)
    {
        int[] logicalTags = [0, 1, 2, 2, 2, 2, 1, 3];
        ushort tags = 0;
        for (int logical = 0; logical < 8; logical++)
            tags |= (ushort)(logicalTags[logical] << (((top + logical) & 7) * 2));
        return tags;
    }

    private static byte[] Ext80(ulong significand, ushort signExponent)
    {
        byte[] bytes = new byte[10];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, significand);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), signExponent);
        return bytes;
    }

    private static byte[] Xmm(int index) => Enumerable.Range(0, 16)
        .Select(offset => unchecked((byte)(1 + index * 0x21 + offset * 13))).ToArray();

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private static void AssertUnchanged(CpuSnapshot before, CpuSandbox cpu)
    {
        Assert.Equal(before.Registers, cpu.Snapshot().Registers);
        Assert.Equal(before.Eflags, cpu.Eflags);
        Assert.Equal(before.Fcw, cpu.Fcw);
        Assert.Equal(before.Fsw, cpu.Fsw);
        Assert.Equal(before.Ftw, cpu.Ftw);
        Assert.Equal(before.Mxcsr, cpu.Mxcsr);
        for (int index = 0; index < 8; index++)
        {
            Assert.Equal(before.Fpu[index], cpu.ReadFpu(index));
            Assert.Equal(before.Xmm[index], cpu.ReadXmm(index));
        }
    }
}
