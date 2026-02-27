using System.Buffers.Binary;
using System.Text;
using Fiberish.Diagnostics;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Fiberish.Core;
using LibObjectFile.Elf;
using Microsoft.Extensions.Logging;
using File = System.IO.File;
using LinuxFile = Fiberish.VFS.LinuxFile;

namespace Fiberish.Loader;

public class LoaderResult
{
    public uint Entry { get; set; }
    public uint SP { get; set; }
    public byte[] InitialStack { get; set; } = [];
    public uint BrkAddr { get; set; }
}

public class ElfLoader
{
    public const uint StackTop = 0xC0000000;
    public const uint StackSize = 0x800000;
    private static readonly ILogger Logger = Logging.CreateLogger<ElfLoader>();

    // Auxv types moved to LinuxConstants

    /// <summary>
    /// Result of loading an ELF's LOAD segments into guest memory.
    /// </summary>
    private class ElfLoadInfo
    {
        public uint PhdrAddr { get; set; }
        public uint PhNum { get; set; }
        public uint PhEnt { get; set; }
        public uint EntryPoint { get; set; }
        public uint BrkAddr { get; set; }
        public uint FirstLoadVaddr { get; set; }
        public string? InterpPath { get; set; }
    }

    public static LoaderResult Load(Dentry dentry, string guestPath, SyscallManager sys, string[] args,
        string[] envs, Mount mount)
    {
        var mm = sys.Mem;
        var engine = sys.Engine;

        // Open the main ELF via VFS
        var mainFile = new LinuxFile(dentry, FileFlags.O_RDONLY, mount);
        using var stream = new VfsStream(mainFile);
        var elf = ElfFile.Read(stream);

        var loadBase = (uint)(elf.FileType == ElfFileType.Executable ? 0 : 0x40000000);
        Logger.LogDebug("ElfLoader: {Filename} FileType={Type}, selected loadBase=0x{LoadBase:x}", guestPath,
            elf.FileType, loadBase);

        // Load main binary's segments
        var mainInfo = LoadSegments(elf, loadBase, mm, engine, dentry, guestPath, stream, mount);

        // Check for PT_INTERP (dynamic linker)
        uint interpBase = 0;
        uint interpEntry = mainInfo.EntryPoint;

        if (mainInfo.InterpPath != null)
        {
            // Resolve interpreter
            var (interpLoc, _) = sys.ResolvePath(mainInfo.InterpPath);
            if (!interpLoc.IsValid)
                throw new FileNotFoundException($"Interpreter not found in VFS: {mainInfo.InterpPath}");

            var interpDentry = interpLoc.Dentry!;
            var interpMount = interpLoc.Mount!;

            var interpFile = new LinuxFile(interpDentry, FileFlags.O_RDONLY, interpMount);
            using var interpStream = new VfsStream(interpFile);
            var interpElf = ElfFile.Read(interpStream);

            if (interpElf.FileType == ElfFileType.Dynamic)
            {
                interpBase = 0x56555000; // Load ld.so at a distinct base
            }
            else
            {
                throw new NotSupportedException($"Unsupported interpreter type: {interpElf.FileType}");
            }

            Logger.LogDebug("ElfLoader: Loading interpreter at base=0x{InterpBase:x}", interpBase);
            var interpInfo = LoadSegments(interpElf, interpBase, mm, engine, interpDentry, mainInfo.InterpPath,
                interpStream, interpMount);
            interpEntry = interpInfo.EntryPoint;
        }

        // Setup Stack
        var stackStart = StackTop - StackSize;
        mm.Mmap(stackStart, StackSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, 0, "STACK", engine);

        var sp = StackTop;
        var stackData = new byte[StackSize];

        uint PushBytes(ReadOnlySpan<byte> b)
        {
            sp -= (uint)b.Length;
            b.CopyTo(stackData.AsSpan((int)(sp - stackStart)));
            return sp;
        }

        uint PushString(string s)
        {
            var b = Encoding.ASCII.GetBytes(s + "\0");
            return PushBytes(b);
        }

        void PushUint32(uint v)
        {
            sp -= 4;
            BinaryPrimitives.WriteUInt32LittleEndian(stackData.AsSpan((int)(sp - stackStart)), v);
        }

        var argPtrs = new uint[args.Length];
        for (var i = args.Length - 1; i >= 0; i--) argPtrs[i] = PushString(args[i]);

        var envPtrs = new uint[envs.Length];
        for (var i = envs.Length - 1; i >= 0; i--) envPtrs[i] = PushString(envs[i]);

        var platPtr = PushString("i686");
        var execFnPtr = PushString(guestPath);
        
        var salt = "fiberish-salt-2024"u8;
        var guestPathBytes = Encoding.ASCII.GetBytes(guestPath);
        var hashInput = new byte[salt.Length + guestPathBytes.Length];
        salt.CopyTo(hashInput);
        guestPathBytes.CopyTo(hashInput.AsSpan(salt.Length));
        
        var fullHash = System.Security.Cryptography.SHA256.HashData(hashInput);
        var randBytes = new byte[16];
        Array.Copy(fullHash, randBytes, 16);
        
        var randPtr = PushBytes(randBytes);
        Logger.LogInformation("Pushing AT_RANDOM to guest stack at 0x{Ptr:X}: {Bytes}", randPtr, BitConverter.ToString(randBytes));

        // ABI: Stack pointer should be 16-byte aligned at process entry (where argc is).
        // To achieve this, we count the number of words we're about to push:
        // argc (1) + argv (ArgCount + 1) + envp (EnvCount + 1) + auxv ((AuxCount + 1) * 2)
        // Note: each auxv entry is 2 words (key, value).
        int auxCount = 13; // We push 12 entries + AT_NULL
        int totalWords = 1 + (args.Length + 1) + (envs.Length + 1) + (auxCount * 2);
        int totalSize = totalWords * 4;
        
        // We want (sp - totalSize) % 16 == 0.
        // So sp % 16 should be totalSize % 16.
        uint targetSp = sp - (uint)totalSize;
        uint alignedTargetSp = targetSp & ~0xFu;
        sp = alignedTargetSp + (uint)totalSize;

        void PushAux(uint k, uint v)
        {
            PushUint32(v);
            PushUint32(k);
        }

        PushAux(LinuxConstants.AT_NULL, 0);
        PushAux(LinuxConstants.AT_PLATFORM, platPtr);
        PushAux(LinuxConstants.AT_EXECFN, execFnPtr);
        PushAux(LinuxConstants.AT_RANDOM, randPtr);
        PushAux(LinuxConstants.AT_UID, 0);
        PushAux(LinuxConstants.AT_EUID, 0);
        PushAux(LinuxConstants.AT_GID, 0);
        PushAux(LinuxConstants.AT_EGID, 0);
        // Always describe the MAIN binary's program headers to the dynamic linker
        PushAux(LinuxConstants.AT_PHNUM, mainInfo.PhNum);
        PushAux(LinuxConstants.AT_PHENT, mainInfo.PhEnt);
        PushAux(LinuxConstants.AT_PHDR, mainInfo.PhdrAddr);
        PushAux(LinuxConstants.AT_PAGESZ, LinuxConstants.PageSize);
        // AT_ENTRY is always the main binary's entry point
        PushAux(LinuxConstants.AT_ENTRY, mainInfo.EntryPoint);
        // AT_BASE is the interpreter's load base (0 if no interpreter)
        PushAux(LinuxConstants.AT_BASE, interpBase);
        PushAux(LinuxConstants.AT_FLAGS, 0);
        // i386 baseline from native Alpine container (podman) for better libc/OpenSSL feature probing.
        PushAux(LinuxConstants.AT_HWCAP, 0x0fcbfbfd);
        PushAux(LinuxConstants.AT_HWCAP2, 0);
        PushAux(LinuxConstants.AT_CLKTCK, 100);

        PushUint32(0);
        for (var i = envPtrs.Length - 1; i >= 0; i--) PushUint32(envPtrs[i]);

        PushUint32(0);
        for (var i = argPtrs.Length - 1; i >= 0; i--) PushUint32(argPtrs[i]);

        PushUint32((uint)args.Length);

        sys.Engine.FlushCache();

        // If there's an interpreter, start execution at interpreter's entry point; 
        // otherwise at the main binary's entry point.
        var entryPoint = mainInfo.InterpPath != null ? interpEntry : mainInfo.EntryPoint;

        var res = new LoaderResult
        {
            Entry = entryPoint,
            SP = sp,
            InitialStack = stackData.AsSpan((int)(sp - stackStart)).ToArray(),
            BrkAddr = mainInfo.BrkAddr
        };
        Logger.LogInformation(
            "ElfLoader Entry=0x{Entry:x} SP=0x{SP:x} Brk=0x{Brk:x} InitialStackLen={StackLen} InterpBase=0x{InterpBase:x}",
            res.Entry, res.SP, res.BrkAddr, res.InitialStack.Length, interpBase);
        return res;
    }

    /// <summary>
    /// Load all PT_LOAD segments from an ELF file into guest memory.
    /// Also detects PT_INTERP and PT_PHDR.
    /// </summary>
    private static ElfLoadInfo LoadSegments(ElfFile elf, uint loadBase, VMAManager mm, Engine engine,
        Dentry? dentry, string fileDesc, Stream elfStream, Mount? mount = null)
    {
        var info = new ElfLoadInfo
        {
            PhEnt = elf.Layout.SizeOfProgramHeaderEntry,
            EntryPoint = (uint)elf.EntryPointAddress + loadBase
        };

        uint firstLoadVaddr = 0;
        bool foundFirstLoad = false;

        foreach (var segment in elf.Segments)
        {
            if (segment.Type == ElfSegmentTypeCore.Load)
            {
                if (!foundFirstLoad)
                {
                    firstLoadVaddr = (uint)segment.VirtualAddress;
                    foundFirstLoad = true;
                }

                var perms = Protection.None;
                if ((segment.Flags.Value & (uint)ElfSegmentFlagsCore.Executable) != 0) perms |= Protection.Exec;
                if ((segment.Flags.Value & (uint)ElfSegmentFlagsCore.Writable) != 0) perms |= Protection.Write;
                if ((segment.Flags.Value & (uint)ElfSegmentFlagsCore.Readable) != 0) perms |= Protection.Read;

                var vaddrRaw = (uint)segment.VirtualAddress + loadBase;
                var offsetRaw = (long)segment.Position;
                var sizeRaw = (uint)segment.SizeInMemory;

                var pageStart = vaddrRaw & LinuxConstants.PageMask;
                var pageOffset = offsetRaw & LinuxConstants.PageMask;
                var diff = vaddrRaw - pageStart;

                var totalSize = sizeRaw + diff;
                var alignedLen = (totalSize + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;

                if (sizeRaw > 0)
                {
                    if (dentry == null)
                        throw new FileNotFoundException($"Could not find file in VFS or Host for mapping: {fileDesc}");

                    var fileSzLimit = diff + (long)segment.Size;
                    var vfsFile = new LinuxFile(dentry, FileFlags.O_RDONLY, mount!);
                    mm.Mmap(pageStart, alignedLen, perms, MapFlags.Private | MapFlags.Fixed, vfsFile, pageOffset,
                        fileSzLimit, "ELF_LOAD", engine);
                }

                // Track brk
                var end = (uint)segment.VirtualAddress + (uint)segment.SizeInMemory + loadBase;
                end = (end + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
                if (end > info.BrkAddr) info.BrkAddr = end;
            }

            if (segment.Type == ElfSegmentTypeCore.ProgramHeader)
            {
                info.PhdrAddr = (uint)segment.VirtualAddress + loadBase;
            }

            // Detect PT_INTERP
            if (segment.Type == ElfSegmentTypeCore.Interpreter)
            {
                // Read the interpreter path directly from the file stream
                var interpData = new byte[(int)segment.Size];
                elfStream.Seek((long)segment.Position, SeekOrigin.Begin);
                elfStream.ReadExactly(interpData, 0, interpData.Length);
                // Trim null terminator
                var interpLen = Array.IndexOf(interpData, (byte)0);
                if (interpLen < 0) interpLen = interpData.Length;
                info.InterpPath = Encoding.ASCII.GetString(interpData, 0, interpLen);
            }

            info.PhNum++;
        }

        if (info.PhdrAddr == 0 && foundFirstLoad)
            info.PhdrAddr = firstLoadVaddr + loadBase + elf.Layout.SizeOfElfHeader;

        info.FirstLoadVaddr = firstLoadVaddr + loadBase;

        Logger.LogDebug(
            "ElfLoader: [{FileDesc}] loadBase=0x{LoadBase:x} phdrAddr=0x{PhdrAddr:x} phnum={PhNum} entry=0x{Entry:x} brk=0x{Brk:x}",
            fileDesc, loadBase, info.PhdrAddr, info.PhNum, info.EntryPoint, info.BrkAddr);

        return info;
    }
}
