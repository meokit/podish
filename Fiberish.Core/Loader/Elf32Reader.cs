using System.Buffers;
using System.Buffers.Binary;

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
    uint VirtualAddress,
    uint Position,
    uint Size,
    uint SizeInMemory);

internal sealed class ElfFile
{
    public required ElfFileType FileType { get; init; }
    public required uint EntryPointAddress { get; init; }
    public required ushort ElfHeaderSize { get; init; }
    public required ushort ProgramHeaderEntrySize { get; init; }
    public required ElfSegment[] Segments { get; init; }
}

internal static class Elf32Reader
{
    private const int ElfHeaderSize = 52;
    private const int ProgramHeaderSize = 32;

    public static ElfFile Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
            throw new NotSupportedException("ELF loading requires a seekable stream.");

        stream.Seek(0, SeekOrigin.Begin);

        Span<byte> header = stackalloc byte[ElfHeaderSize];
        stream.ReadExactly(header);

        var (fileType, entryPoint, programHeaderOffset, elfHeaderSize, programHeaderEntrySize, programHeaderCount) =
            ParseHeader(header);
        var fileLength = stream.Length;

        var segments = new ElfSegment[programHeaderCount];
        if (programHeaderCount > 0)
        {
            var programHeaderTableSize = checked(programHeaderEntrySize * programHeaderCount);
            if (programHeaderOffset > fileLength - programHeaderTableSize)
                throw new InvalidDataException("ELF program header extends past the end of the file.");

            stream.Seek(programHeaderOffset, SeekOrigin.Begin);
            byte[]? rented = null;
            var programHeaderTable = programHeaderTableSize <= 512
                ? stackalloc byte[programHeaderTableSize]
                : (rented = ArrayPool<byte>.Shared.Rent(programHeaderTableSize)).AsSpan(0, programHeaderTableSize);
            try
            {
                stream.ReadExactly(programHeaderTable);
                for (var i = 0; i < programHeaderCount; i++)
                {
                    var entryOffset = i * programHeaderEntrySize;
                    segments[i] = ParseProgramHeader(programHeaderTable.Slice(entryOffset, ProgramHeaderSize));
                }
            }
            finally
            {
                if (rented != null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }

        return new ElfFile
        {
            FileType = fileType,
            EntryPointAddress = entryPoint,
            ElfHeaderSize = elfHeaderSize,
            ProgramHeaderEntrySize = programHeaderEntrySize,
            Segments = segments
        };
    }

    public static ElfFile Read(byte[] data)
    {
        return Read((ReadOnlySpan<byte>)data);
    }

    public static ElfFile Read(ReadOnlySpan<byte> data)
    {
        var (fileType, entryPoint, programHeaderOffset, elfHeaderSize, programHeaderEntrySize, programHeaderCount) =
            ParseHeader(data);

        var segments = new ElfSegment[programHeaderCount];
        if (programHeaderCount > 0)
        {
            var programHeaderTableSize = checked(programHeaderEntrySize * programHeaderCount);
            var programHeaderTableOffset = checked((int)programHeaderOffset);
            if (programHeaderTableOffset > data.Length - programHeaderTableSize)
                throw new InvalidDataException("ELF program header extends past the end of the file.");

            var programHeaderTable = data.Slice(programHeaderTableOffset, programHeaderTableSize);
            for (var i = 0; i < programHeaderCount; i++)
            {
                var entryOffset = i * programHeaderEntrySize;
                segments[i] = ParseProgramHeader(programHeaderTable.Slice(entryOffset, ProgramHeaderSize));
            }
        }

        return new ElfFile
        {
            FileType = fileType,
            EntryPointAddress = entryPoint,
            ElfHeaderSize = elfHeaderSize,
            ProgramHeaderEntrySize = programHeaderEntrySize,
            Segments = segments
        };
    }

    private static (ElfFileType FileType, uint EntryPoint, uint ProgramHeaderOffset, ushort ElfHeaderSize,
        ushort ProgramHeaderEntrySize, ushort ProgramHeaderCount) ParseHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < ElfHeaderSize)
            throw new InvalidDataException("ELF data too short for header.");

        if (data[0] != 0x7F || data[1] != (byte)'E' || data[2] != (byte)'L' || data[3] != (byte)'F')
            throw new InvalidDataException("Not an ELF file.");
        if (data[4] != 1)
            throw new NotSupportedException("Only ELF32 is supported.");
        if (data[5] != 1)
            throw new NotSupportedException("Only little-endian ELF is supported.");
        if (data[6] != 1)
            throw new InvalidDataException("Unsupported ELF version.");

        var fileType = (ElfFileType)BinaryPrimitives.ReadUInt16LittleEndian(data[16..18]);
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(data[18..20]);
        if (machine != 3)
            throw new NotSupportedException($"Unsupported ELF machine: {machine}.");

        var entryPoint = BinaryPrimitives.ReadUInt32LittleEndian(data[24..28]);
        var programHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[28..32]);
        var elfHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data[40..42]);
        var programHeaderEntrySize = BinaryPrimitives.ReadUInt16LittleEndian(data[42..44]);
        var programHeaderCount = BinaryPrimitives.ReadUInt16LittleEndian(data[44..46]);

        if (elfHeaderSize < ElfHeaderSize)
            throw new InvalidDataException($"Invalid ELF header size: {elfHeaderSize}.");
        if (programHeaderCount > 0 && programHeaderEntrySize < ProgramHeaderSize)
            throw new InvalidDataException($"Invalid program header size: {programHeaderEntrySize}.");

        return (fileType, entryPoint, programHeaderOffset, elfHeaderSize, programHeaderEntrySize,
            programHeaderCount);
    }

    private static ElfSegment ParseProgramHeader(ReadOnlySpan<byte> ph)
    {
        return new ElfSegment(
            (ElfSegmentType)BinaryPrimitives.ReadUInt32LittleEndian(ph[..4]),
            BinaryPrimitives.ReadUInt32LittleEndian(ph[24..28]),
            BinaryPrimitives.ReadUInt32LittleEndian(ph[8..12]),
            BinaryPrimitives.ReadUInt32LittleEndian(ph[4..8]),
            BinaryPrimitives.ReadUInt32LittleEndian(ph[16..20]),
            BinaryPrimitives.ReadUInt32LittleEndian(ph[20..24]));
    }
}