using System.Buffers;
using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.VFS;

namespace Fiberish.Loader;

internal enum ElfFileType : ushort
{
    None = 0,
    Relocatable = 1,
    Executable = 2,
    Dynamic = 3,
    Core = 4
}

internal enum ElfSegmentType : uint
{
    Null = 0,
    Load = 1,
    Dynamic = 2,
    Interpreter = 3,
    Note = 4,
    ProgramHeader = 6
}

internal static class ElfSegmentFlags
{
    public const uint Executable = 0x1;
    public const uint Writable = 0x2;
    public const uint Readable = 0x4;
}

internal readonly record struct ElfSegment(
    ElfSegmentType Type,
    uint Flags,
    ulong VirtualAddress,
    ulong Position,
    ulong Size,
    ulong SizeInMemory,
    ulong Alignment);

internal sealed class ElfFile
{
    public required ElfFileType FileType { get; init; }
    public required GuestArchitecture Architecture { get; init; }
    public required ulong EntryPointAddress { get; init; }
    public required ulong ProgramHeaderOffset { get; init; }
    public required ushort ElfHeaderSize { get; init; }
    public required ushort ProgramHeaderEntrySize { get; init; }
    public required ElfSegment[] Segments { get; init; }
}

internal static class ElfReader
{
    private const int IdentSize = 16;
    private const int Elf32HeaderSize = 52;
    private const int Elf64HeaderSize = 64;
    private const int Elf32ProgramHeaderSize = 32;
    private const int Elf64ProgramHeaderSize = 56;
    private const int MaxElf64ProgramHeaderTableSize = 65536;

    private readonly record struct Header(ElfFileType FileType, GuestArchitecture Architecture,
        ulong EntryPoint, ulong ProgramHeaderOffset, ushort ElfHeaderSize, ushort ProgramHeaderEntrySize,
        ushort ProgramHeaderCount)
    {
        public int GetTableSize(long fileLength)
        {
            if (ProgramHeaderCount == 0) return 0;
            var tableSize = (long)ProgramHeaderEntrySize * ProgramHeaderCount;
            if (Architecture == GuestArchitecture.AArch64 && tableSize > MaxElf64ProgramHeaderTableSize)
                throw new InvalidDataException("ELF64 program header table is too large.");
            if (tableSize > int.MaxValue || tableSize > fileLength ||
                ProgramHeaderOffset > (ulong)(fileLength - tableSize))
                throw new InvalidDataException("ELF program header extends past the end of the file.");
            return (int)tableSize;
        }

        public ElfFile ToFile(ReadOnlySpan<byte> table)
        {
            var segments = new ElfSegment[ProgramHeaderCount];
            for (var i = 0; i < segments.Length; i++)
            {
                var entry = table[(i * ProgramHeaderEntrySize)..];
                segments[i] = Architecture == GuestArchitecture.AArch64
                    ? Parse64ProgramHeader(entry)
                    : Parse32ProgramHeader(entry);
            }

            return new ElfFile
            {
                FileType = FileType,
                Architecture = Architecture,
                EntryPointAddress = EntryPoint,
                ProgramHeaderOffset = ProgramHeaderOffset,
                ElfHeaderSize = ElfHeaderSize,
                ProgramHeaderEntrySize = ProgramHeaderEntrySize,
                Segments = segments
            };
        }
    }

    public static ElfFile Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
            throw new NotSupportedException("ELF loading requires a seekable stream.");

        stream.Seek(0, SeekOrigin.Begin);

        Span<byte> data = stackalloc byte[Elf64HeaderSize];
        stream.ReadExactly(data[..IdentSize]);
        var headerSize = ReadHeaderSize(data);
        stream.ReadExactly(data[IdentSize..headerSize]);
        var header = ParseHeader(data[..headerSize], headerSize);
        var tableSize = header.GetTableSize(stream.Length);
        if (tableSize == 0) return header.ToFile([]);

        stream.Seek((long)header.ProgramHeaderOffset, SeekOrigin.Begin);
        byte[]? rented = null;
        var table = tableSize <= 512
            ? stackalloc byte[tableSize]
            : (rented = ArrayPool<byte>.Shared.Rent(tableSize)).AsSpan(0, tableSize);
        try
        {
            stream.ReadExactly(table);
            return header.ToFile(table);
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static async ValueTask<ElfFile> ReadAsync(VfsStream stream, FiberTask task)
    {
        stream.Position = 0;
        var data = new byte[Elf64HeaderSize];
        await stream.ReadExactlyForTaskAsync(task, data.AsMemory(0, IdentSize));
        var headerSize = ReadHeaderSize(data);
        await stream.ReadExactlyForTaskAsync(task, data.AsMemory(IdentSize, headerSize - IdentSize));
        var header = ParseHeader(data, headerSize);
        var tableSize = header.GetTableSize(stream.Length);
        if (tableSize == 0) return header.ToFile([]);

        stream.Position = (long)header.ProgramHeaderOffset;
        var table = new byte[tableSize];
        await stream.ReadExactlyForTaskAsync(task, table);
        return header.ToFile(table);
    }

    public static ElfFile Read(byte[] data)
    {
        return Read((ReadOnlySpan<byte>)data);
    }

    public static ElfFile Read(ReadOnlySpan<byte> data)
    {
        var header = ParseHeader(data, ReadHeaderSize(data));
        var tableSize = header.GetTableSize(data.Length);
        return tableSize == 0
            ? header.ToFile([])
            : header.ToFile(data.Slice((int)header.ProgramHeaderOffset, tableSize));
    }

    private static int ReadHeaderSize(ReadOnlySpan<byte> data)
    {
        if (data.Length < IdentSize)
            throw new InvalidDataException("ELF data too short for header.");

        if (data[0] != 0x7F || data[1] != (byte)'E' || data[2] != (byte)'L' || data[3] != (byte)'F')
            throw new InvalidDataException("Not an ELF file.");
        if (data[5] != 1)
            throw new NotSupportedException("Only little-endian ELF is supported.");
        if (data[6] != 1)
            throw new InvalidDataException("Unsupported ELF version.");

        return data[4] switch
        {
            1 => Elf32HeaderSize,
            2 => Elf64HeaderSize,
            _ => throw new NotSupportedException($"Unsupported ELF class: {data[4]}.")
        };
    }

    private static Header ParseHeader(ReadOnlySpan<byte> data, int headerSize)
    {
        if (data.Length < headerSize)
            throw new InvalidDataException("ELF data too short for header.");

        var fileType = (ElfFileType)BinaryPrimitives.ReadUInt16LittleEndian(data[16..18]);
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(data[18..20]);
        var is64Bit = headerSize == Elf64HeaderSize;
        var architecture = (is64Bit, machine) switch
        {
            (false, 3) => GuestArchitecture.I386,
            (true, 183) => GuestArchitecture.AArch64,
            _ => throw new NotSupportedException($"Unsupported ELF machine: {machine}.")
        };

        if (is64Bit && BinaryPrimitives.ReadUInt32LittleEndian(data[20..24]) != 1)
            throw new InvalidDataException("Unsupported ELF header version.");

        var entryPoint = is64Bit
            ? BinaryPrimitives.ReadUInt64LittleEndian(data[24..32])
            : BinaryPrimitives.ReadUInt32LittleEndian(data[24..28]);
        var programHeaderOffset = is64Bit
            ? BinaryPrimitives.ReadUInt64LittleEndian(data[32..40])
            : BinaryPrimitives.ReadUInt32LittleEndian(data[28..32]);
        var sizeField = is64Bit ? 52 : 40;
        var elfHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data[sizeField..]);
        var programHeaderEntrySize = BinaryPrimitives.ReadUInt16LittleEndian(data[(sizeField + 2)..]);
        var programHeaderCount = BinaryPrimitives.ReadUInt16LittleEndian(data[(sizeField + 4)..]);

        if (elfHeaderSize < headerSize)
            throw new InvalidDataException($"Invalid ELF header size: {elfHeaderSize}.");
        var programHeaderSize = is64Bit ? Elf64ProgramHeaderSize : Elf32ProgramHeaderSize;
        if (programHeaderCount > 0 && (is64Bit
                ? programHeaderEntrySize != programHeaderSize
                : programHeaderEntrySize < programHeaderSize))
            throw new InvalidDataException($"Invalid program header size: {programHeaderEntrySize}.");

        return new Header(fileType, architecture, entryPoint, programHeaderOffset, elfHeaderSize, programHeaderEntrySize,
            programHeaderCount);
    }

    private static ElfSegment Parse32ProgramHeader(ReadOnlySpan<byte> ph)
    {
        return new ElfSegment(
            (ElfSegmentType)BinaryPrimitives.ReadUInt32LittleEndian(ph[..4]),
            BinaryPrimitives.ReadUInt32LittleEndian(ph[24..28]),
            BinaryPrimitives.ReadUInt32LittleEndian(ph[8..12]),
            BinaryPrimitives.ReadUInt32LittleEndian(ph[4..8]),
            BinaryPrimitives.ReadUInt32LittleEndian(ph[16..20]),
            BinaryPrimitives.ReadUInt32LittleEndian(ph[20..24]),
            BinaryPrimitives.ReadUInt32LittleEndian(ph[28..32]));
    }

    private static ElfSegment Parse64ProgramHeader(ReadOnlySpan<byte> ph)
    {
        return new ElfSegment(
            (ElfSegmentType)BinaryPrimitives.ReadUInt32LittleEndian(ph[..4]),
            BinaryPrimitives.ReadUInt32LittleEndian(ph[4..8]),
            BinaryPrimitives.ReadUInt64LittleEndian(ph[16..24]),
            BinaryPrimitives.ReadUInt64LittleEndian(ph[8..16]),
            BinaryPrimitives.ReadUInt64LittleEndian(ph[32..40]),
            BinaryPrimitives.ReadUInt64LittleEndian(ph[40..48]),
            BinaryPrimitives.ReadUInt64LittleEndian(ph[48..56]));
    }
}
