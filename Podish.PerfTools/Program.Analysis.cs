using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LibObjectFile.Elf;

namespace Podish.PerfTools;

internal static partial class Program
{
    private static Dictionary<string, object?> AnalyzeBlocks(string inputPath, string libPath, int nGram, int topNgrams)
    {
        var (dumpFile, summaryFile, defaultOutput) = ResolveInputPaths(inputPath);
        var summary = LoadSummary(summaryFile);
        Console.Error.WriteLine($"[analyze-blocks] loading symbols from {libPath}");
        var symbols = LoadSymbols(libPath);
        Console.Error.WriteLine($"[analyze-blocks] loaded {symbols.Count} symbols");
        using var opIdResolver = HandlerOpIdResolver.TryCreate(libPath);
        Console.Error.WriteLine($"[analyze-blocks] parsing block dump {dumpFile}");
        var (baseAddr, count, blocks, warnings) = ParseBlocks(dumpFile, symbols, opIdResolver);
        Console.Error.WriteLine($"[analyze-blocks] parsed {blocks.Count}/{count} blocks");
        var validation = BuildValidation(summary, count, blocks.Count, warnings);

        var output = new Dictionary<string, object?>
        {
            ["metadata"] = new Dictionary<string, object?>
            {
                ["input_path"] = Path.GetFullPath(inputPath),
                ["lib_path"] = Path.GetFullPath(libPath),
                ["symbol_count"] = symbols.Count,
                ["n_gram"] = nGram,
                ["top_ngrams_limit"] = topNgrams,
                ["base_addr"] = baseAddr,
                ["declared_block_count"] = count,
                ["parsed_block_count"] = blocks.Count
            },
            ["validation"] = validation,
            ["blocks"] = blocks
        };

        if (nGram > 0)
            output["ngrams"] = AnalyzeNgrams(blocks, nGram, topNgrams);

        Console.Error.WriteLine("[analyze-blocks] analysis object built");

        return output;
    }

    private static (string dumpFile, string? summaryFile, string defaultOutput) ResolveInputPaths(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var dumpFile = Path.Combine(inputPath, "blocks.bin");
            var summaryFile = Path.Combine(inputPath, "summary.json");
            if (!File.Exists(dumpFile))
                throw new FileNotFoundException($"Block dump file not found: {dumpFile}");
            return (dumpFile, File.Exists(summaryFile) ? summaryFile : null,
                Path.Combine(inputPath, "blocks_analysis.json"));
        }

        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Block dump file not found: {inputPath}");

        return (inputPath, null, "blocks_analysis.json");
    }

    private static JsonElement? LoadSummary(string? summaryFile)
    {
        if (string.IsNullOrWhiteSpace(summaryFile) || !File.Exists(summaryFile))
            return null;
        return JsonDocument.Parse(File.ReadAllText(summaryFile, Encoding.UTF8)).RootElement.Clone();
    }

    private static Dictionary<ulong, string> LoadSymbols(string libPath)
    {
        return BuildSymbolMap(TryLoadRawSymbolsWithObjectFile(libPath));
    }

    private static List<(ulong addr, string name)> TryLoadRawSymbolsWithObjectFile(string libPath)
    {
        try
        {
            using var inStream = File.OpenRead(libPath);
            return DetectBinaryFormat(inStream) switch
            {
                BinaryFormat.Elf => LoadElfRawSymbols(inStream),
                BinaryFormat.Pe => LoadPeRawSymbols(inStream),
                BinaryFormat.MachO => LoadMachORawSymbols(inStream),
                _ => new List<(ulong addr, string name)>()
            };
        }
        catch
        {
            return new List<(ulong addr, string name)>();
        }
    }

    private static List<(ulong addr, string name)> LoadElfRawSymbols(Stream inStream)
    {
        var rawSymbols = new List<(ulong addr, string name)>();

        inStream.Position = 0;
        var elf = ElfFile.Read(inStream);
        foreach (var section in elf.Sections)
        {
            if (section is not ElfSymbolTable symtab)
                continue;

            foreach (var symbol in symtab.Entries)
            {
                var symbolName = symbol.Name.ToString();
                if (string.IsNullOrWhiteSpace(symbolName))
                    continue;

                ulong value;
                try
                {
                    value = Convert.ToUInt64(symbol.Value, CultureInfo.InvariantCulture);
                }
                catch
                {
                    continue;
                }

                rawSymbols.Add((value, symbolName));
            }
        }

        return rawSymbols;
    }

    private static Dictionary<ulong, string> BuildSymbolMap(List<(ulong addr, string name)> rawSymbols)
    {
        if (rawSymbols.Count == 0)
            return new Dictionary<ulong, string>();

        var demangled = DemangleSymbols(rawSymbols.Select(x => x.name).Distinct(StringComparer.Ordinal).ToArray());
        var symbols = new Dictionary<ulong, string>();
        foreach (var (addr, name) in rawSymbols) symbols[addr] = demangled.TryGetValue(name, out var d) ? d : name;

        return symbols;
    }

    private static List<(ulong addr, string name)> LoadPeRawSymbols(Stream inStream)
    {
        inStream.Position = 0;
        using var reader = new BinaryReader(inStream, Encoding.UTF8, true);
        var rawSymbols = new List<(ulong addr, string name)>();

        if (reader.ReadUInt16() != 0x5A4D)
            return rawSymbols;

        inStream.Position = 0x3C;
        var peHeaderOffset = reader.ReadUInt32();
        if (peHeaderOffset >= inStream.Length || peHeaderOffset + 4 > inStream.Length)
            return rawSymbols;

        inStream.Position = peHeaderOffset;
        if (reader.ReadUInt32() != 0x00004550)
            return rawSymbols;

        _ = reader.ReadUInt16(); // Machine
        var numberOfSections = reader.ReadUInt16();
        _ = reader.ReadUInt32(); // TimeDateStamp
        var pointerToSymbolTable = reader.ReadUInt32();
        var numberOfSymbols = reader.ReadUInt32();
        var sizeOfOptionalHeader = reader.ReadUInt16();
        _ = reader.ReadUInt16(); // Characteristics

        var optionalHeaderStart = inStream.Position;
        var optionalMagic = sizeOfOptionalHeader >= 2 ? reader.ReadUInt16() : (ushort)0;
        uint exportTableRva = 0;
        uint exportTableSize = 0;

        if (sizeOfOptionalHeader > 0 && optionalHeaderStart + sizeOfOptionalHeader <= inStream.Length)
        {
            if (optionalMagic == 0x10B && sizeOfOptionalHeader >= 104)
            {
                inStream.Position = optionalHeaderStart + 96;
                exportTableRva = reader.ReadUInt32();
                exportTableSize = reader.ReadUInt32();
            }
            else if (optionalMagic == 0x20B && sizeOfOptionalHeader >= 120)
            {
                inStream.Position = optionalHeaderStart + 112;
                exportTableRva = reader.ReadUInt32();
                exportTableSize = reader.ReadUInt32();
            }
        }

        inStream.Position = optionalHeaderStart + sizeOfOptionalHeader;
        var sections = new List<PeSectionInfo>(numberOfSections);
        for (var i = 0; i < numberOfSections; i++)
        {
            if (inStream.Position + 40 > inStream.Length)
                break;

            _ = reader.ReadBytes(8); // Name
            var virtualSize = reader.ReadUInt32();
            var virtualAddress = reader.ReadUInt32();
            var sizeOfRawData = reader.ReadUInt32();
            var pointerToRawData = reader.ReadUInt32();
            _ = reader.ReadUInt32(); // PointerToRelocations
            _ = reader.ReadUInt32(); // PointerToLineNumbers
            _ = reader.ReadUInt16(); // NumberOfRelocations
            _ = reader.ReadUInt16(); // NumberOfLineNumbers
            _ = reader.ReadUInt32(); // Characteristics
            sections.Add(new PeSectionInfo(virtualAddress, virtualSize, sizeOfRawData, pointerToRawData));
        }

        if (pointerToSymbolTable != 0 &&
            numberOfSymbols != 0 &&
            pointerToSymbolTable < inStream.Length &&
            pointerToSymbolTable + (ulong)numberOfSymbols * 18 <= (ulong)inStream.Length)
        {
            var stringTableOffset = pointerToSymbolTable + numberOfSymbols * 18;
            uint stringTableSize = 0;
            if (stringTableOffset + 4 <= inStream.Length)
            {
                inStream.Position = stringTableOffset;
                stringTableSize = reader.ReadUInt32();
            }

            inStream.Position = pointerToSymbolTable;
            for (uint i = 0; i < numberOfSymbols; i++)
            {
                if (inStream.Position + 18 > inStream.Length)
                    break;

                var nameBytes = reader.ReadBytes(8);
                var value = reader.ReadUInt32();
                var sectionNumber = reader.ReadInt16();
                _ = reader.ReadUInt16(); // Type
                _ = reader.ReadByte(); // StorageClass
                var numberOfAuxSymbols = reader.ReadByte();

                var name = ReadPeSymbolName(nameBytes, inStream, stringTableOffset, stringTableSize);
                if (!string.IsNullOrWhiteSpace(name) && sectionNumber > 0 && sectionNumber <= sections.Count)
                {
                    var section = sections[sectionNumber - 1];
                    rawSymbols.Add((section.VirtualAddress + value, name));
                }

                if (numberOfAuxSymbols > 0)
                {
                    var skipBytes = (long)numberOfAuxSymbols * 18;
                    if (inStream.Position + skipBytes > inStream.Length)
                        break;
                    inStream.Position += skipBytes;
                    i += numberOfAuxSymbols;
                }
            }
        }

        if (exportTableRva != 0 &&
            exportTableSize >= 40 &&
            TryMapPeRvaToFileOffset(exportTableRva, sections, out var exportDirectoryOffset) &&
            exportDirectoryOffset + 40 <= (ulong)inStream.Length)
        {
            inStream.Position = (long)exportDirectoryOffset;
            _ = reader.ReadUInt32(); // Characteristics
            _ = reader.ReadUInt32(); // TimeDateStamp
            _ = reader.ReadUInt16(); // MajorVersion
            _ = reader.ReadUInt16(); // MinorVersion
            _ = reader.ReadUInt32(); // Name
            _ = reader.ReadUInt32(); // Base
            var numberOfFunctions = reader.ReadUInt32();
            var numberOfNames = reader.ReadUInt32();
            var addressOfFunctionsRva = reader.ReadUInt32();
            var addressOfNamesRva = reader.ReadUInt32();
            var addressOfNameOrdinalsRva = reader.ReadUInt32();

            if (numberOfFunctions > 0 &&
                numberOfNames > 0 &&
                TryMapPeRvaToFileOffset(addressOfFunctionsRva, sections, out var addressOfFunctionsOffset) &&
                TryMapPeRvaToFileOffset(addressOfNamesRva, sections, out var addressOfNamesOffset) &&
                TryMapPeRvaToFileOffset(addressOfNameOrdinalsRva, sections, out var addressOfNameOrdinalsOffset))
                for (uint i = 0; i < numberOfNames; i++)
                {
                    var nameEntryOffset = addressOfNamesOffset + i * 4;
                    var ordinalEntryOffset = addressOfNameOrdinalsOffset + i * 2;
                    if (nameEntryOffset + 4 > (ulong)inStream.Length || ordinalEntryOffset + 2 > (ulong)inStream.Length)
                        break;

                    inStream.Position = (long)nameEntryOffset;
                    var nameRva = reader.ReadUInt32();
                    inStream.Position = (long)ordinalEntryOffset;
                    var ordinal = reader.ReadUInt16();
                    if (ordinal >= numberOfFunctions)
                        continue;

                    var functionEntryOffset = addressOfFunctionsOffset + (ulong)ordinal * 4;
                    if (functionEntryOffset + 4 > (ulong)inStream.Length)
                        continue;

                    inStream.Position = (long)functionEntryOffset;
                    var functionRva = reader.ReadUInt32();
                    if (functionRva >= exportTableRva && functionRva < exportTableRva + exportTableSize)
                        continue;

                    if (!TryMapPeRvaToFileOffset(nameRva, sections, out var nameOffset))
                        continue;

                    var name = ReadNullTerminatedAscii(inStream, nameOffset, int.MaxValue);
                    if (!string.IsNullOrWhiteSpace(name))
                        rawSymbols.Add((functionRva, name));
                }
        }

        return rawSymbols;
    }

    private static List<(ulong addr, string name)> LoadMachORawSymbols(Stream inStream)
    {
        var rawSymbols = new List<(ulong addr, string name)>();
        if (!TryReadMachOSlice(inStream, out var sliceOffset, out var is64Bit, out var isLittleEndian))
            return rawSymbols;

        uint numberOfCommands;
        ulong loadCommandsOffset;
        if (is64Bit)
        {
            if (sliceOffset + 32 > (ulong)inStream.Length)
                return rawSymbols;
            numberOfCommands = ReadUInt32(inStream, sliceOffset + 16, isLittleEndian);
            loadCommandsOffset = sliceOffset + 32;
        }
        else
        {
            if (sliceOffset + 28 > (ulong)inStream.Length)
                return rawSymbols;
            numberOfCommands = ReadUInt32(inStream, sliceOffset + 16, isLittleEndian);
            loadCommandsOffset = sliceOffset + 28;
        }

        ulong? symbolTableOffset = null;
        uint numberOfSymbols = 0;
        ulong? stringTableOffset = null;
        uint stringTableSize = 0;
        ulong? imageBase = null;
        var commandOffset = loadCommandsOffset;

        for (uint i = 0; i < numberOfCommands; i++)
        {
            if (commandOffset + 8 > (ulong)inStream.Length)
                break;

            var command = ReadUInt32(inStream, commandOffset, isLittleEndian);
            var commandSize = ReadUInt32(inStream, commandOffset + 4, isLittleEndian);
            if (commandSize < 8 || commandOffset + commandSize > (ulong)inStream.Length)
                break;

            const uint LcSymtab = 0x2;
            const uint LcSegment = 0x1;
            const uint LcSegment64 = 0x19;

            if (command == LcSymtab && commandSize >= 24)
            {
                symbolTableOffset = ReadUInt32(inStream, commandOffset + 8, isLittleEndian) + sliceOffset;
                numberOfSymbols = ReadUInt32(inStream, commandOffset + 12, isLittleEndian);
                stringTableOffset = ReadUInt32(inStream, commandOffset + 16, isLittleEndian) + sliceOffset;
                stringTableSize = ReadUInt32(inStream, commandOffset + 20, isLittleEndian);
            }
            else if (command == LcSegment64 && commandSize >= 72)
            {
                var vmaddr = ReadUInt64(inStream, commandOffset + 24, isLittleEndian);
                var filesize = ReadUInt64(inStream, commandOffset + 40, isLittleEndian);
                if (filesize != 0 && (!imageBase.HasValue || vmaddr < imageBase.Value))
                    imageBase = vmaddr;
            }
            else if (command == LcSegment && commandSize >= 56)
            {
                var vmaddr = ReadUInt32(inStream, commandOffset + 24, isLittleEndian);
                var filesize = ReadUInt32(inStream, commandOffset + 36, isLittleEndian);
                if (filesize != 0 && (!imageBase.HasValue || vmaddr < imageBase.Value))
                    imageBase = vmaddr;
            }

            commandOffset += commandSize;
        }

        if (!symbolTableOffset.HasValue ||
            !stringTableOffset.HasValue ||
            symbolTableOffset.Value >= (ulong)inStream.Length ||
            stringTableOffset.Value >= (ulong)inStream.Length)
            return rawSymbols;

        var entrySize = is64Bit ? 16u : 12u;
        var baseAddress = imageBase ?? 0;
        for (uint i = 0; i < numberOfSymbols; i++)
        {
            var entryOffset = symbolTableOffset.Value + i * entrySize;
            if (entryOffset + entrySize > (ulong)inStream.Length)
                break;

            var stringIndex = ReadUInt32(inStream, entryOffset, isLittleEndian);
            var type = ReadByte(inStream, entryOffset + 4);
            var value = is64Bit
                ? ReadUInt64(inStream, entryOffset + 8, isLittleEndian)
                : ReadUInt32(inStream, entryOffset + 8, isLittleEndian);

            const byte NStabMask = 0xE0;
            const byte NTypeMask = 0x0E;
            const byte NSect = 0x0E;
            if ((type & NStabMask) != 0 || (type & NTypeMask) != NSect || stringIndex == 0 || value == 0)
                continue;

            var nameOffset = stringTableOffset.Value + stringIndex;
            if (nameOffset >= (ulong)inStream.Length || nameOffset >= stringTableOffset.Value + stringTableSize)
                continue;

            var maxLength = (int)Math.Min(int.MaxValue, stringTableOffset.Value + stringTableSize - nameOffset);
            var name = ReadNullTerminatedAscii(inStream, nameOffset, maxLength);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var normalizedValue = value >= baseAddress ? value - baseAddress : value;
            rawSymbols.Add((normalizedValue, name));
        }

        return rawSymbols;
    }

    private static BinaryFormat DetectBinaryFormat(Stream inStream)
    {
        if (inStream.Length < 4)
            return BinaryFormat.Unknown;

        inStream.Position = 0;
        Span<byte> magic = stackalloc byte[4];
        inStream.ReadExactly(magic);
        inStream.Position = 0;

        if (magic[0] == 0x7F && magic[1] == (byte)'E' && magic[2] == (byte)'L' && magic[3] == (byte)'F')
            return BinaryFormat.Elf;

        if (magic[0] == (byte)'M' && magic[1] == (byte)'Z')
            return BinaryFormat.Pe;

        var value = BinaryPrimitives.ReadUInt32LittleEndian(magic);
        return value switch
        {
            0xFEEDFACE or 0xCEFAEDFE or 0xFEEDFACF or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA or 0xCAFEBABF or 0xBFBAFECA
                => BinaryFormat.MachO,
            _ => BinaryFormat.Unknown
        };
    }

    private static bool TryMapPeRvaToFileOffset(uint rva, List<PeSectionInfo> sections, out ulong fileOffset)
    {
        foreach (var section in sections)
        {
            var span = Math.Max(section.VirtualSize, section.SizeOfRawData);
            if (rva < section.VirtualAddress || rva >= section.VirtualAddress + span)
                continue;

            fileOffset = section.PointerToRawData + (rva - section.VirtualAddress);
            return true;
        }

        fileOffset = 0;
        return false;
    }

    private static string ReadPeSymbolName(byte[] nameBytes, Stream inStream, long stringTableOffset,
        uint stringTableSize)
    {
        if (nameBytes.Length != 8)
            return string.Empty;

        if (BinaryPrimitives.ReadUInt32LittleEndian(nameBytes.AsSpan(0, 4)) == 0)
        {
            var stringOffset = BinaryPrimitives.ReadUInt32LittleEndian(nameBytes.AsSpan(4, 4));
            if (stringOffset < 4 || stringOffset >= stringTableSize)
                return string.Empty;

            return ReadNullTerminatedAscii(inStream, (ulong)(stringTableOffset + stringOffset),
                (int)(stringTableSize - stringOffset));
        }

        var terminator = Array.IndexOf(nameBytes, (byte)0);
        var length = terminator >= 0 ? terminator : nameBytes.Length;
        return Encoding.ASCII.GetString(nameBytes, 0, length);
    }

    private static bool TryReadMachOSlice(Stream inStream, out ulong sliceOffset, out bool is64Bit,
        out bool isLittleEndian)
    {
        sliceOffset = 0;
        is64Bit = false;
        isLittleEndian = true;

        if (inStream.Length < 4)
            return false;

        var magic = ReadUInt32(inStream, 0, true);
        switch (magic)
        {
            case 0xFEEDFACE:
                isLittleEndian = true;
                is64Bit = false;
                return true;
            case 0xCEFAEDFE:
                isLittleEndian = false;
                is64Bit = false;
                return true;
            case 0xFEEDFACF:
                isLittleEndian = true;
                is64Bit = true;
                return true;
            case 0xCFFAEDFE:
                isLittleEndian = false;
                is64Bit = true;
                return true;
            case 0xCAFEBABE:
            case 0xBEBAFECA:
            case 0xCAFEBABF:
            case 0xBFBAFECA:
                return TryReadFatMachOSlice(inStream, magic, out sliceOffset, out is64Bit, out isLittleEndian);
            default:
                return false;
        }
    }

    private static bool TryReadFatMachOSlice(Stream inStream, uint fatMagic, out ulong sliceOffset, out bool is64Bit,
        out bool isLittleEndian)
    {
        sliceOffset = 0;
        is64Bit = false;
        isLittleEndian = true;

        var fatIs64 = fatMagic is 0xCAFEBABF or 0xBFBAFECA;
        var archEntrySize = fatIs64 ? 32 : 20;
        var numberOfArchitectures = ReadUInt32(inStream, 4, false);
        if (numberOfArchitectures == 0)
            return false;

        var preferredCpuType = GetPreferredMachOCpuType();
        ulong? fallbackOffset = null;
        bool? fallbackIs64 = null;
        bool? fallbackLittleEndian = null;

        for (uint i = 0; i < numberOfArchitectures; i++)
        {
            var entryOffset = 8UL + i * (ulong)archEntrySize;
            if (entryOffset + (ulong)archEntrySize > (ulong)inStream.Length)
                break;

            var cpuType = ReadUInt32(inStream, entryOffset, false);
            var offset = fatIs64
                ? ReadUInt64(inStream, entryOffset + 8, false)
                : ReadUInt32(inStream, entryOffset + 8, false);
            if (offset + 4 > (ulong)inStream.Length)
                continue;

            var sliceMagic = ReadUInt32(inStream, offset, true);
            if (!TryDecodeThinMachOMagic(sliceMagic, out var sliceIs64, out var sliceLittleEndian))
                continue;

            fallbackOffset ??= offset;
            fallbackIs64 ??= sliceIs64;
            fallbackLittleEndian ??= sliceLittleEndian;

            if (preferredCpuType.HasValue && cpuType == preferredCpuType.Value)
            {
                sliceOffset = offset;
                is64Bit = sliceIs64;
                isLittleEndian = sliceLittleEndian;
                return true;
            }
        }

        if (fallbackOffset.HasValue && fallbackIs64.HasValue && fallbackLittleEndian.HasValue)
        {
            sliceOffset = fallbackOffset.Value;
            is64Bit = fallbackIs64.Value;
            isLittleEndian = fallbackLittleEndian.Value;
            return true;
        }

        return false;
    }

    private static bool TryDecodeThinMachOMagic(uint magic, out bool is64Bit, out bool isLittleEndian)
    {
        switch (magic)
        {
            case 0xFEEDFACE:
                is64Bit = false;
                isLittleEndian = true;
                return true;
            case 0xCEFAEDFE:
                is64Bit = false;
                isLittleEndian = false;
                return true;
            case 0xFEEDFACF:
                is64Bit = true;
                isLittleEndian = true;
                return true;
            case 0xCFFAEDFE:
                is64Bit = true;
                isLittleEndian = false;
                return true;
            default:
                is64Bit = false;
                isLittleEndian = true;
                return false;
        }
    }

    private static uint? GetPreferredMachOCpuType()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => 0x0100000C,
            Architecture.X64 => 0x01000007,
            Architecture.X86 => 7u,
            Architecture.Arm => 12u,
            _ => null
        };
    }

    private static byte ReadByte(Stream inStream, ulong offset)
    {
        inStream.Position = (long)offset;
        var value = inStream.ReadByte();
        if (value < 0)
            throw new EndOfStreamException();
        return (byte)value;
    }

    private static uint ReadUInt32(Stream inStream, ulong offset, bool isLittleEndian)
    {
        Span<byte> buffer = stackalloc byte[4];
        inStream.Position = (long)offset;
        inStream.ReadExactly(buffer);
        return isLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(buffer)
            : BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    private static ulong ReadUInt64(Stream inStream, ulong offset, bool isLittleEndian)
    {
        Span<byte> buffer = stackalloc byte[8];
        inStream.Position = (long)offset;
        inStream.ReadExactly(buffer);
        return isLittleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(buffer)
            : BinaryPrimitives.ReadUInt64BigEndian(buffer);
    }

    private static string ReadNullTerminatedAscii(Stream inStream, ulong offset, int maxLength)
    {
        if (maxLength <= 0)
            return string.Empty;

        inStream.Position = (long)offset;
        using var buffer = new MemoryStream();
        for (var i = 0; i < maxLength; i++)
        {
            var value = inStream.ReadByte();
            if (value <= 0)
                break;
            buffer.WriteByte((byte)value);
        }

        return Encoding.ASCII.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static Dictionary<string, string> DemangleSymbols(string[] names)
    {
        if (names.Length == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var psi = new ProcessStartInfo("c++filt", "-n")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            foreach (var name in names)
            {
                var toWrite = name.StartsWith("__Z", StringComparison.Ordinal) ? name[1..] : name;
                proc.StandardInput.WriteLine(toWrite);
            }

            proc.StandardInput.Close();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length != names.Length)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < names.Length; i++)
                map[names[i]] = lines[i];
            return map;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static (ulong baseAddr, int count, List<Dictionary<string, object?>> blocks, List<string> warnings)
        ParseBlocks(
            string dumpFile, Dictionary<ulong, string> symbols, HandlerOpIdResolver? opIdResolver)
    {
        var blocks = new List<Dictionary<string, object?>>();
        var warnings = new List<string>();

        using var stream = File.OpenRead(dumpFile);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);

        var baseAddr = reader.ReadUInt64();
        var count = reader.ReadInt32();

        for (var blockIndex = 0; blockIndex < count; blockIndex++)
        {
            var header = reader.ReadBytes(20);
            if (header.Length != 20)
            {
                warnings.Add($"truncated block header at index {blockIndex}: expected 20 bytes, got {header.Length}");
                break;
            }

            var startEip = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
            var endEip = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            var instCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
            var execCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));

            var ops = new List<Dictionary<string, object?>>();
            var truncatedBlock = false;
            for (uint opIndex = 0; opIndex < instCount; opIndex++)
            {
                var opBytes = reader.ReadBytes(32);
                if (opBytes.Length != 32)
                {
                    warnings.Add(
                        $"truncated op payload in block 0x{startEip:x} at op {opIndex}: expected 32 bytes, got {opBytes.Length}");
                    truncatedBlock = true;
                    break;
                }

                var memPacked = BinaryPrimitives.ReadUInt64LittleEndian(opBytes.AsSpan(0, 8));
                var nextEip = BinaryPrimitives.ReadUInt32LittleEndian(opBytes.AsSpan(8, 4));
                var len = opBytes[12];
                var modrm = opBytes[13];
                var prefixes = opBytes[14];
                var meta = opBytes[15];
                var imm = BinaryPrimitives.ReadUInt32LittleEndian(opBytes.AsSpan(16, 4));
                var handlerPtr = BinaryPrimitives.ReadUInt64LittleEndian(opBytes.AsSpan(24, 8));

                var memDisp = (uint)(memPacked & 0xFFFFFFFF);
                var eaDesc = (uint)(memPacked >> 32);
                var handlerOffset = handlerPtr >= baseAddr ? handlerPtr - baseAddr : 0UL;
                var symbolName = FindSymbol(handlerOffset, symbols);
                var logicFunc = NormalizeLogicFuncName(symbolName);
                var opId = opIdResolver?.Resolve(handlerPtr, handlerOffset);
                var defUse = AnalyzeDefUse(opId, modrm, meta, prefixes, (int)eaDesc);
                var defUseNode = defUse?.ToDictionary() ?? new Dictionary<string, object?>
                {
                    ["reads"] = Array.Empty<string>(),
                    ["writes"] = Array.Empty<string>(),
                    ["notes"] = Array.Empty<string>(),
                    ["control_flow"] = false,
                    ["memory_side_effect"] = false
                };

                ops.Add(new Dictionary<string, object?>
                {
                    ["index"] = opIndex,
                    ["next_eip"] = nextEip,
                    ["next_eip_hex"] = $"0x{nextEip:x}",
                    ["handler_ptr"] = handlerPtr,
                    ["handler_ptr_hex"] = $"0x{handlerPtr:x}",
                    ["handler_offset"] = handlerOffset,
                    ["handler_offset_hex"] = $"0x{handlerOffset:x}",
                    ["symbol_raw"] = symbolName,
                    ["logic_func"] = logicFunc,
                    ["symbol"] = logicFunc ?? symbolName,
                    ["op_id"] = opId,
                    ["op_id_hex"] = opId is null ? null : $"0x{opId.Value:x}",
                    ["imm"] = imm,
                    ["imm_hex"] = $"0x{imm:x}",
                    ["len"] = len,
                    ["len_hex"] = $"0x{len:x}",
                    ["prefixes"] = prefixes,
                    ["prefixes_hex"] = $"0x{prefixes:x}",
                    ["modrm"] = modrm,
                    ["modrm_hex"] = $"0x{modrm:x}",
                    ["meta"] = meta,
                    ["meta_hex"] = $"0x{meta:x}",
                    ["mem"] = new Dictionary<string, object?>
                    {
                        ["disp"] = memDisp,
                        ["disp_hex"] = $"0x{memDisp:x}",
                        ["ea_desc"] = eaDesc,
                        ["ea_desc_hex"] = $"0x{eaDesc:x}",
                        ["base_offset"] = eaDesc & 0x3F,
                        ["base_offset_hex"] = $"0x{eaDesc & 0x3F:x}",
                        ["index_offset"] = (eaDesc >> 6) & 0x3F,
                        ["index_offset_hex"] = $"0x{(eaDesc >> 6) & 0x3F:x}",
                        ["scale"] = (eaDesc >> 12) & 0x3,
                        ["scale_hex"] = $"0x{(eaDesc >> 12) & 0x3:x}",
                        ["segment"] = (eaDesc >> 14) & 0x7,
                        ["segment_hex"] = $"0x{(eaDesc >> 14) & 0x7:x}"
                    },
                    ["def_use"] = defUseNode
                });
            }

            if (truncatedBlock)
                break;

            blocks.Add(new Dictionary<string, object?>
            {
                ["start_eip"] = startEip,
                ["start_eip_hex"] = $"0x{startEip:x}",
                ["end_eip"] = endEip,
                ["end_eip_hex"] = $"0x{endEip:x}",
                ["inst_count"] = instCount,
                ["exec_count"] = execCount,
                ["ops"] = ops
            });
        }

        return (baseAddr, count, blocks, warnings);
    }

    private static Dictionary<string, object?> BuildValidation(JsonElement? summary, int declaredBlockCount,
        int parsedBlocks, List<string> parseWarnings)
    {
        var validation = new Dictionary<string, object?>
        {
            ["warnings"] = parseWarnings.ToArray()
        };

        if (summary is { } summaryRoot)
            if (summaryRoot.TryGetProperty("native_stats", out var nativeStatsRaw) &&
                nativeStatsRaw.ValueKind == JsonValueKind.String)
                try
                {
                    var nativeStats = JsonDocument.Parse(nativeStatsRaw.GetString() ?? "{}").RootElement;
                    if (nativeStats.TryGetProperty("all_blocks_count", out var allBlocksCount) &&
                        allBlocksCount.ValueKind == JsonValueKind.Number)
                    {
                        var nativeCount = allBlocksCount.GetInt32();
                        if (nativeCount != declaredBlockCount)
                            validation["warnings"] = ((string[])validation["warnings"]!).Append(
                                    $"summary native_stats.all_blocks_count={nativeCount} but dump declared block count={declaredBlockCount}")
                                .ToArray();
                        if (nativeCount > 0 && parsedBlocks == 0)
                            validation["warnings"] = ((string[])validation["warnings"]!).Append(
                                    "summary reports non-zero native all_blocks_count but parsed blocks are empty; dump/export format likely drifted")
                                .ToArray();
                    }
                }
                catch
                {
                    validation["warnings"] = ((string[])validation["warnings"]!)
                        .Append("summary native_stats is not valid JSON").ToArray();
                }

        return validation;
    }

    private static string FindSymbol(ulong offset, Dictionary<ulong, string> symbols)
    {
        return symbols.TryGetValue(offset, out var name) ? name : $"func_{offset:x}";
    }

    private static string? NormalizeLogicFuncName(string? symbolName)
    {
        if (string.IsNullOrWhiteSpace(symbolName))
            return null;
        if (symbolName.Contains("SuperOpcode_", StringComparison.Ordinal))
            return null;
        if (symbolName.StartsWith("op::Op", StringComparison.Ordinal))
            return symbolName;
        if (symbolName.StartsWith("Op", StringComparison.Ordinal) && OpPrefixRegex.IsMatch(symbolName))
            return "op::" + symbolName;

        var wrapperMatch = DispatchWrapperRegex.Match(symbolName);
        if (wrapperMatch.Success)
            return wrapperMatch.Groups[1].Value;

        var directMatch = DirectLogicRegex.Match(symbolName);
        if (directMatch.Success)
            return directMatch.Groups[1].Value;

        var mangledMatch = MangledLogicRegex.Match(symbolName);
        if (mangledMatch.Success)
            return "op::" + mangledMatch.Groups[1].Value;

        return null;
    }

    private static Dictionary<string, object?> AnalyzeNgrams(List<Dictionary<string, object?>> blocks, int n,
        int topNgrams)
    {
        if (n <= 0)
            return new Dictionary<string, object?>();

        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        var blockCoverage = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            if (!block.TryGetValue("ops", out var opsObj) || opsObj is not List<Dictionary<string, object?>> ops)
                continue;

            var symbols = ops.Select(op => op.TryGetValue("symbol", out var sym) ? sym?.ToString() ?? "" : "").ToList();
            if (symbols.Count < n)
                continue;

            var seenInBlock = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i + n <= symbols.Count; i++)
            {
                var gram = string.Join(" -> ", symbols.Skip(i).Take(n));
                count[gram] = count.TryGetValue(gram, out var v) ? v + 1 : 1;
                seenInBlock.Add(gram);
            }

            foreach (var gram in seenInBlock)
                blockCoverage[gram] = blockCoverage.TryGetValue(gram, out var v) ? v + 1 : 1;
        }

        return new Dictionary<string, object?>
        {
            ["n"] = n,
            ["top"] = topNgrams,
            ["entries"] = count
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(topNgrams)
                .Select(kv => new Dictionary<string, object?>
                {
                    ["gram"] = kv.Key,
                    ["count"] = kv.Value,
                    ["block_coverage"] = blockCoverage.TryGetValue(kv.Key, out var cov) ? cov : 0
                })
                .ToList()
        };
    }

    private sealed class HandlerOpIdResolver : IDisposable
    {
        private readonly Dictionary<ulong, int?> _cache = new();
        private readonly GetOpIdForHandlerDelegate? _getOpIdForHandler;
        private readonly ulong _libBase;

        private readonly IntPtr _libraryHandle;

        private HandlerOpIdResolver(
            IntPtr libraryHandle,
            GetOpIdForHandlerDelegate? getOpIdForHandler,
            ulong libBase)
        {
            _libraryHandle = libraryHandle;
            _getOpIdForHandler = getOpIdForHandler;
            _libBase = libBase;
        }

        public void Dispose()
        {
            if (_libraryHandle != IntPtr.Zero)
                NativeLibrary.Free(_libraryHandle);
        }

        public static HandlerOpIdResolver? TryCreate(string libPath)
        {
            try
            {
                var handle = NativeLibrary.Load(libPath);
                var getOpIdForHandler = Marshal.GetDelegateForFunctionPointer<GetOpIdForHandlerDelegate>(
                    NativeLibrary.GetExport(handle, "X86_GetOpIdForHandler"));
                var getLibAddress = Marshal.GetDelegateForFunctionPointer<GetLibAddressDelegate>(
                    NativeLibrary.GetExport(handle, "X86_GetLibAddress"));
                var libBase = (ulong)getLibAddress().ToInt64();
                return new HandlerOpIdResolver(handle, getOpIdForHandler, libBase);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[analyze-blocks] warning: failed to load X86_GetOpIdForHandler from {libPath}: {ex.Message}");
                return null;
            }
        }

        public int? Resolve(ulong handlerPtr, ulong handlerOffset)
        {
            if (_getOpIdForHandler is null)
                return null;

            ulong lookupPtr;
            if (_libBase != 0 && handlerOffset != 0)
                lookupPtr = _libBase + handlerOffset;
            else if (handlerPtr != 0)
                lookupPtr = handlerPtr;
            else
                return null;

            if (_cache.TryGetValue(lookupPtr, out var cached))
                return cached;

            var value = _getOpIdForHandler((IntPtr)unchecked((long)lookupPtr));
            int? result = value < 0 ? null : value;
            _cache[lookupPtr] = result;
            return result;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetOpIdForHandlerDelegate(IntPtr handler);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetLibAddressDelegate();
    }

    private enum BinaryFormat
    {
        Unknown,
        Elf,
        Pe,
        MachO
    }

    private sealed record PeSectionInfo(
        uint VirtualAddress,
        uint VirtualSize,
        uint SizeOfRawData,
        uint PointerToRawData);
}