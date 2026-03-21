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

        if (header[0] != 0x7F || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F')
            throw new InvalidDataException("Not an ELF file.");
        if (header[4] != 1)
            throw new NotSupportedException("Only ELF32 is supported.");
        if (header[5] != 1)
            throw new NotSupportedException("Only little-endian ELF is supported.");
        if (header[6] != 1)
            throw new InvalidDataException("Unsupported ELF version.");

        var fileType = (ElfFileType)BinaryPrimitives.ReadUInt16LittleEndian(header[16..18]);
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header[18..20]);
        if (machine != 3)
            throw new NotSupportedException($"Unsupported ELF machine: {machine}.");

        var entryPoint = BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]);
        var programHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[28..32]);
        var elfHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(header[40..42]);
        var programHeaderEntrySize = BinaryPrimitives.ReadUInt16LittleEndian(header[42..44]);
        var programHeaderCount = BinaryPrimitives.ReadUInt16LittleEndian(header[44..46]);

        if (elfHeaderSize < ElfHeaderSize)
            throw new InvalidDataException($"Invalid ELF header size: {elfHeaderSize}.");
        if (programHeaderCount > 0 && programHeaderEntrySize < ProgramHeaderSize)
            throw new InvalidDataException($"Invalid program header size: {programHeaderEntrySize}.");

        var segments = new ElfSegment[programHeaderCount];
        Span<byte> programHeader = stackalloc byte[ProgramHeaderSize];
        for (var i = 0; i < programHeaderCount; i++)
        {
            var entryOffset = checked(programHeaderOffset + (long)i * programHeaderEntrySize);
            stream.Seek(entryOffset, SeekOrigin.Begin);
            stream.ReadExactly(programHeader);

            segments[i] = new ElfSegment(
                (ElfSegmentType)BinaryPrimitives.ReadUInt32LittleEndian(programHeader[..4]),
                BinaryPrimitives.ReadUInt32LittleEndian(programHeader[24..28]),
                BinaryPrimitives.ReadUInt32LittleEndian(programHeader[8..12]),
                BinaryPrimitives.ReadUInt32LittleEndian(programHeader[4..8]),
                BinaryPrimitives.ReadUInt32LittleEndian(programHeader[16..20]),
                BinaryPrimitives.ReadUInt32LittleEndian(programHeader[20..24]));
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
}