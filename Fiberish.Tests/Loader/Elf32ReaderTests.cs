using System.Buffers.Binary;
using Fiberish.Loader;
using Xunit;

namespace Fiberish.Tests.Loader;

public class Elf32ReaderTests
{
    [Fact]
    public void Read_ParsesElf32ProgramHeadersWithoutSectionLoading()
    {
        var image = BuildTwoProgramHeaderElfImage();

        using var stream = new MemoryStream(image, false);
        var elf = Elf32Reader.Read(stream);

        Assert.Equal(ElfFileType.Executable, elf.FileType);
        Assert.Equal(0x8048123u, elf.EntryPointAddress);
        Assert.Equal((ushort)52, elf.ElfHeaderSize);
        Assert.Equal((ushort)32, elf.ProgramHeaderEntrySize);
        Assert.Equal(2, elf.Segments.Length);

        var load = elf.Segments[0];
        Assert.Equal(ElfSegmentType.Load, load.Type);
        Assert.Equal(0x1000u, load.Position);
        Assert.Equal(0x8048000u, load.VirtualAddress);
        Assert.Equal(0x2000u, load.Size);
        Assert.Equal(0x3000u, load.SizeInMemory);
        Assert.Equal(5u, load.Flags);

        var interp = elf.Segments[1];
        Assert.Equal(ElfSegmentType.Interpreter, interp.Type);
        Assert.Equal(0x140u, interp.Position);
        Assert.Equal(12u, interp.Size);
    }

    [Fact]
    public void Read_StreamOnlyConsumesTheHeaderAndProgramHeaders()
    {
        var image = BuildTwoProgramHeaderElfImage();
        var minimumReadablePrefix = 0x74;

        using var stream = new PartialElfStream(image.AsSpan(0, minimumReadablePrefix).ToArray(), 0x4000);
        var elf = Elf32Reader.Read(stream);

        Assert.Equal(2, elf.Segments.Length);
        Assert.True(stream.TotalBytesRead <= minimumReadablePrefix);
        Assert.Equal(minimumReadablePrefix, stream.MaxObservedPosition);
    }

    private static byte[] BuildTwoProgramHeaderElfImage()
    {
        var image = new byte[0x180];
        var span = image.AsSpan();

        span[0] = 0x7F;
        span[1] = (byte)'E';
        span[2] = (byte)'L';
        span[3] = (byte)'F';
        span[4] = 1; // ELFCLASS32
        span[5] = 1; // little-endian
        span[6] = 1; // version

        BinaryPrimitives.WriteUInt16LittleEndian(span[16..18], 2); // ET_EXEC
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..20], 3); // EM_386
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..24], 1); // EV_CURRENT
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..28], 0x8048123); // entry
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..32], 0x34); // phoff
        BinaryPrimitives.WriteUInt16LittleEndian(span[40..42], 52); // ehsize
        BinaryPrimitives.WriteUInt16LittleEndian(span[42..44], 32); // phentsize
        BinaryPrimitives.WriteUInt16LittleEndian(span[44..46], 2); // phnum

        var ph0 = span[0x34..0x54];
        BinaryPrimitives.WriteUInt32LittleEndian(ph0[..4], 1); // PT_LOAD
        BinaryPrimitives.WriteUInt32LittleEndian(ph0[4..8], 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(ph0[8..12], 0x8048000);
        BinaryPrimitives.WriteUInt32LittleEndian(ph0[16..20], 0x2000);
        BinaryPrimitives.WriteUInt32LittleEndian(ph0[20..24], 0x3000);
        BinaryPrimitives.WriteUInt32LittleEndian(ph0[24..28], 5); // R|X

        var ph1 = span[0x54..0x74];
        BinaryPrimitives.WriteUInt32LittleEndian(ph1[..4], 3); // PT_INTERP
        BinaryPrimitives.WriteUInt32LittleEndian(ph1[4..8], 0x140);
        BinaryPrimitives.WriteUInt32LittleEndian(ph1[8..12], 0x8049140);
        BinaryPrimitives.WriteUInt32LittleEndian(ph1[16..20], 12);
        BinaryPrimitives.WriteUInt32LittleEndian(ph1[20..24], 12);
        BinaryPrimitives.WriteUInt32LittleEndian(ph1[24..28], 4); // R

        return image;
    }

    private sealed class PartialElfStream(byte[] content, long length) : Stream
    {
        public int MaxObservedPosition { get; private set; }
        public int TotalBytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= content.Length)
                return 0;

            var available = Math.Min(count, (int)(content.Length - Position));
            Array.Copy(content, Position, buffer, offset, available);
            Position += available;
            TotalBytesRead += available;
            MaxObservedPosition = Math.Max(MaxObservedPosition, (int)Position);
            return available;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null)
            };
            return Position;
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}