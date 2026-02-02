using System.Buffers.Binary;
using System.Text;
using LibObjectFile.Elf;
using Bifrost.Memory;
using Bifrost.Core;

namespace Bifrost.Loader;

public class LoaderResult
{
    public uint Entry { get; set; }
    public uint SP { get; set; }
    public byte[] InitialStack { get; set; } = Array.Empty<byte>();
    public uint BrkAddr { get; set; }
}

public class ElfLoader
{
    public const uint StackTop = 0xC0000000;
    public const uint StackSize = 0x20000;

    // Auxv types
    const uint AT_NULL = 0;
    const uint AT_PHDR = 3;
    const uint AT_PHENT = 4;
    const uint AT_PHNUM = 5;
    const uint AT_PAGESZ = 6;
    const uint AT_BASE = 7;
    const uint AT_FLAGS = 8;
    const uint AT_ENTRY = 9;
    const uint AT_UID = 11;
    const uint AT_EUID = 12;
    const uint AT_GID = 13;
    const uint AT_EGID = 14;
    const uint AT_PLATFORM = 15;
    const uint AT_RANDOM = 25;

    public static LoaderResult Load(string filename, VMAManager mm, string[] args, string[] envs, Engine engine)
    {
        using var stream = File.OpenRead(filename);
        var elf = ElfFile.Read(stream);
        
        uint loadBase = 0;
        if (elf.FileType == ElfFileType.Dynamic)
        {
            loadBase = 0x40000000; // PIE base
        }

        uint phnum = 0;
        uint phdrAddr = 0;

        foreach (var segment in elf.Segments)
        {
            if (segment.Type == ElfSegmentTypeCore.Load)
            {
                Protection perms = Protection.None;
                if ((segment.Flags.Value & (uint)ElfSegmentFlagsCore.Executable) != 0) perms |= Protection.Exec;
                if ((segment.Flags.Value & (uint)ElfSegmentFlagsCore.Writable) != 0) perms |= Protection.Write;
                if ((segment.Flags.Value & (uint)ElfSegmentFlagsCore.Readable) != 0) perms |= Protection.Read;

                uint vaddrRaw = (uint)segment.VirtualAddress + loadBase;
                long offsetRaw = (long)segment.Position;
                uint sizeRaw = (uint)segment.SizeInMemory;

                uint pageStart = vaddrRaw & ~0xFFFu;
                long pageOffset = offsetRaw & ~0xFFFu;
                uint diff = vaddrRaw - pageStart;

                uint totalSize = sizeRaw + diff;
                uint alignedLen = (totalSize + 0xFFF) & ~0xFFFu;

                if (sizeRaw > 0)
                {
                    long fileSzLimit = diff + (long)segment.Size;
                    var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
                    mm.Mmap(pageStart, alignedLen, perms, MapFlags.Private | MapFlags.Fixed, fs, pageOffset, fileSzLimit, "ELF_LOAD", engine);
                }
            }

            if (segment.Type == ElfSegmentTypeCore.ProgramHeader)
            {
                phdrAddr = (uint)segment.VirtualAddress + loadBase;
            }
            phnum++;
        }

        if (phdrAddr == 0) phdrAddr = loadBase + (uint)elf.Layout.SizeOfElfHeader;

        // Setup Stack
        uint stackStart = StackTop - StackSize;
        mm.Mmap(stackStart, StackSize, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, 0, "STACK", engine);

        uint sp = StackTop;
        byte[] stackData = new byte[StackSize];

        uint PushBytes(ReadOnlySpan<byte> b)
        {
            sp -= (uint)b.Length;
            b.CopyTo(stackData.AsSpan((int)(sp - stackStart)));
            return sp;
        }

        uint PushString(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s + "\0");
            return PushBytes(b);
        }

        void PushUint32(uint v)
        {
            sp -= 4;
            BinaryPrimitives.WriteUInt32LittleEndian(stackData.AsSpan((int)(sp - stackStart)), v);
        }

        uint[] argPtrs = new uint[args.Length];
        for (int i = args.Length - 1; i >= 0; i--) argPtrs[i] = PushString(args[i]);

        uint[] envPtrs = new uint[envs.Length];
        for (int i = envs.Length - 1; i >= 0; i--) envPtrs[i] = PushString(envs[i]);

        uint platPtr = PushString("i686");
        uint randPtr = PushBytes(new byte[16]);

        sp &= ~0xFu;

        void PushAux(uint k, uint v)
        {
            PushUint32(v);
            PushUint32(k);
        }

        PushAux(AT_NULL, 0);
        PushAux(AT_PLATFORM, platPtr);
        PushAux(AT_RANDOM, randPtr);
        PushAux(AT_UID, 1000);
        PushAux(AT_EUID, 1000);
        PushAux(AT_GID, 1000);
        PushAux(AT_EGID, 1000);
        PushAux(AT_PHNUM, (uint)elf.Segments.Count);
        PushAux(AT_PHENT, (uint)elf.Layout.SizeOfProgramHeaderEntry);
        PushAux(AT_PHDR, phdrAddr);
        PushAux(AT_PAGESZ, 4096);
        PushAux(AT_ENTRY, (uint)elf.EntryPointAddress + loadBase);
        PushAux(AT_BASE, loadBase);
        PushAux(AT_FLAGS, 0);

        PushUint32(0);
        for (int i = envPtrs.Length - 1; i >= 0; i--) PushUint32(envPtrs[i]);

        PushUint32(0);
        for (int i = argPtrs.Length - 1; i >= 0; i--) PushUint32(argPtrs[i]);

        PushUint32((uint)args.Length);

        uint brkAddr = 0;
        foreach (var segment in elf.Segments)
        {
            if (segment.Type == ElfSegmentTypeCore.Load)
            {
                uint end = (uint)segment.VirtualAddress + (uint)segment.SizeInMemory + loadBase;
                end = (end + 0xFFF) & ~0xFFFu;
                if (end > brkAddr) brkAddr = end;
            }
        }

        return new LoaderResult
        {
            Entry = (uint)elf.EntryPointAddress + loadBase,
            SP = sp,
            InitialStack = stackData.AsSpan((int)(sp - stackStart)).ToArray(),
            BrkAddr = brkAddr
        };
    }
}