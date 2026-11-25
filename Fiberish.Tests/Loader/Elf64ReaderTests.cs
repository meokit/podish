using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Loader;
using Xunit;

namespace Fiberish.Tests.Loader;

public class Elf64ReaderTests
{
    [Fact]
    public void Read_UsesElf64FieldWidthsAndProgramHeaderOffset()
    {
        const ulong virtualAddress = 0x1_0040_0000;
        var image = Elf64Image.Create(virtualAddress);
        var entry = image.AsSpan(Elf64Image.ProgramHeaderOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], 0x2_0000_0000);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[32..], 0x3_0000_0000);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[40..], 0x4_0000_0000);

        using var stream = new MemoryStream(image, writable: false);
        var elf = ElfReader.Read(stream);

        Assert.Equal(GuestArchitecture.AArch64, elf.Architecture);
        Assert.Equal(virtualAddress + Elf64Image.EntryOffset, elf.EntryPointAddress);
        Assert.Equal((ulong)Elf64Image.ProgramHeaderOffset, elf.ProgramHeaderOffset);
        Assert.Equal((ushort)Elf64Image.ProgramHeaderSize, elf.ProgramHeaderEntrySize);
        var segment = Assert.Single(elf.Segments);
        Assert.Equal(ElfSegmentType.Load, segment.Type);
        Assert.Equal(5u, segment.Flags);
        Assert.Equal(virtualAddress, segment.VirtualAddress);
        Assert.Equal(0x2_0000_0000ul, segment.Position);
        Assert.Equal(0x3_0000_0000ul, segment.Size);
        Assert.Equal(0x4_0000_0000ul, segment.SizeInMemory);
        Assert.Equal(0x10000ul, segment.Alignment);
    }

    [Fact]
    public void Read_RejectsAnOutOfRangeProgramHeaderOffsetBeforeSeeking()
    {
        var image = Elf64Image.Create();
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(32), ulong.MaxValue);

        using var stream = new MemoryStream(image, writable: false);
        Assert.Throws<InvalidDataException>(() => ElfReader.Read(stream));
        Assert.Throws<InvalidDataException>(() => ElfReader.Read(image));
    }

    [Theory]
    [InlineData(3)] // EM_386 is valid only with ELFCLASS32.
    [InlineData(62)] // EM_X86_64.
    public void Read_RejectsOtherElf64Machines(ushort machine)
    {
        var image = Elf64Image.Create();
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(18), machine);
        Assert.Throws<NotSupportedException>(() => ElfReader.Read(image));
    }
}

internal static class Elf64Image
{
    public const int ProgramHeaderOffset = 0x80;
    public const int ProgramHeaderSize = 56;
    public const uint EntryOffset = 0x200;
    public const uint VirtualAddress = 0x00400000;
    public const int FileSize = 0x220;
    public const int MemorySize = 0x1100;

    public static byte[] Create(ulong virtualAddress = VirtualAddress, bool pie = false, string? interpreter = null)
    {
        var image = new byte[0x1000];
        ""u8.CopyTo(image);
        "ELF"u8.CopyTo(image.AsSpan(1));
        image[4] = 2; // ELFCLASS64
        image[5] = 1; // ELFDATA2LSB
        image[6] = 1; // EV_CURRENT
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(16), pie ? (ushort)3 : (ushort)2);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(18), 183); // EM_AARCH64
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(24), virtualAddress + EntryOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(32), ProgramHeaderOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(52), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(54), ProgramHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(56), interpreter == null ? (ushort)1 : (ushort)2);

        var load = image.AsSpan(ProgramHeaderOffset, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(load, 1); // PT_LOAD
        BinaryPrimitives.WriteUInt32LittleEndian(load[4..], 5); // PF_R | PF_X
        BinaryPrimitives.WriteUInt64LittleEndian(load[16..], virtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(load[32..], FileSize);
        BinaryPrimitives.WriteUInt64LittleEndian(load[40..], MemorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(load[48..], 0x10000);

        if (interpreter != null)
        {
            const int pathOffset = 0x180;
            var interp = image.AsSpan(ProgramHeaderOffset + ProgramHeaderSize, ProgramHeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(interp, 3); // PT_INTERP
            BinaryPrimitives.WriteUInt64LittleEndian(interp[8..], pathOffset);
            var pathLength = System.Text.Encoding.UTF8.GetBytes(interpreter, image.AsSpan(pathOffset));
            BinaryPrimitives.WriteUInt64LittleEndian(interp[32..], (ulong)pathLength + 1);
        }

        var code = image.AsSpan((int)EntryOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(code, 0xd2800540); // mov x0, #42
        BinaryPrimitives.WriteUInt32LittleEndian(code[4..], 0xd2800ba8); // mov x8, #93
        BinaryPrimitives.WriteUInt32LittleEndian(code[8..], 0xd4000001); // svc #0
        image.AsSpan(FileSize).Fill(0xcc);
        return image;
    }
}
