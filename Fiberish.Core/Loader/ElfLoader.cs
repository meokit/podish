using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Fiberish.Core;
using Fiberish.Diagnostics;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
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
    public const uint StackTop = LinuxConstants.TaskSize32;
    public const uint StackSize = 0x800000;
    private static readonly ILogger Logger = Logging.CreateLogger<ElfLoader>();

    public static LoaderResult Load(Dentry dentry, string guestPath, SyscallManager sys, string[] args,
        string[] envs, Mount mount)
    {
        var mm = sys.Mem;
        var engine = sys.CurrentSyscallEngine;

        // Open the main ELF via VFS
        var mainFile = new LinuxFile(dentry, FileFlags.O_RDONLY, mount);
        using var stream = new VfsStream(mainFile);
        var elf = Elf32Reader.Read(stream);

        var loadBase = elf.FileType == ElfFileType.Executable ? 0u : 0x40000000;
        Logger.LogDebug("ElfLoader: {Filename} FileType={Type}, selected loadBase=0x{LoadBase:x}", guestPath,
            elf.FileType, loadBase);

        // Load main binary's segments
        var mainInfo = LoadSegments(elf, loadBase, mm, engine, dentry, guestPath, stream, mount);

        // Check for PT_INTERP (dynamic linker)
        uint interpBase = 0;
        var interpEntry = mainInfo.EntryPoint;

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
            var interpElf = Elf32Reader.Read(interpStream);

            if (interpElf.FileType == ElfFileType.Dynamic)
                interpBase = 0x56555000; // Load ld.so at a distinct base
            else
                throw new NotSupportedException($"Unsupported interpreter type: {interpElf.FileType}");

            Logger.LogDebug("ElfLoader: Loading interpreter at base=0x{InterpBase:x}", interpBase);
            var interpInfo = LoadSegments(interpElf, interpBase, mm, engine, interpDentry, mainInfo.InterpPath,
                interpStream, interpMount);
            interpEntry = interpInfo.EntryPoint;
        }

        // Setup Stack
        var stackStart = StackTop - StackSize;
        ProcessAddressSpaceSync.Mmap(mm, engine, stackStart, StackSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "STACK");

        var salt = "fiberish-salt-2024"u8;
        var randBytes = new byte[16];
        var guestPathByteCount = Encoding.ASCII.GetByteCount(guestPath);
        var hashInputLen = salt.Length + guestPathByteCount;
        byte[]? rentedHashInput = null;
        var hashInput = hashInputLen <= 256
            ? stackalloc byte[hashInputLen]
            : (rentedHashInput = ArrayPool<byte>.Shared.Rent(hashInputLen)).AsSpan(0, hashInputLen);
        try
        {
            salt.CopyTo(hashInput);
            Encoding.ASCII.GetBytes(guestPath, hashInput[salt.Length..]);
            var fullHash = SHA256.HashData(hashInput);
            fullHash.AsSpan(0, randBytes.Length).CopyTo(randBytes);
        }
        finally
        {
            if (rentedHashInput != null)
                ArrayPool<byte>.Shared.Return(rentedHashInput);
        }

        static uint GetAsciiStringSize(string value)
        {
            return checked((uint)Encoding.ASCII.GetByteCount(value) + 1u);
        }

        uint variableDataSize = 16; // AT_RANDOM
        foreach (var arg in args) variableDataSize += GetAsciiStringSize(arg);
        foreach (var env in envs) variableDataSize += GetAsciiStringSize(env);
        variableDataSize += GetAsciiStringSize("i686");
        variableDataSize += GetAsciiStringSize(guestPath);

        const int AuxEntryCount = 19;
        var totalWords = 1 + args.Length + 1 + envs.Length + 1 + AuxEntryCount * 2;
        var totalSize = totalWords * 4;

        var initialTargetSp = StackTop - variableDataSize - (uint)totalSize;
        var finalSp = initialTargetSp & ~0xFu;
        var stackData = new byte[StackTop - finalSp];
        var sp = StackTop;

        uint PushBytes(ReadOnlySpan<byte> bytes)
        {
            sp -= (uint)bytes.Length;
            bytes.CopyTo(stackData.AsSpan((int)(sp - finalSp)));
            return sp;
        }

        uint PushAsciiString(string value)
        {
            var byteCount = Encoding.ASCII.GetByteCount(value);
            sp -= (uint)(byteCount + 1);
            var dest = stackData.AsSpan((int)(sp - finalSp), byteCount + 1);
            Encoding.ASCII.GetBytes(value, dest);
            dest[byteCount] = 0;
            return sp;
        }

        void PushUint32(uint value)
        {
            sp -= 4;
            BinaryPrimitives.WriteUInt32LittleEndian(stackData.AsSpan((int)(sp - finalSp), 4), value);
        }

        var argPtrs = new uint[args.Length];
        for (var i = args.Length - 1; i >= 0; i--) argPtrs[i] = PushAsciiString(args[i]);

        var envPtrs = new uint[envs.Length];
        for (var i = envs.Length - 1; i >= 0; i--) envPtrs[i] = PushAsciiString(envs[i]);

        var platPtr = PushAsciiString("i686");
        var execFnPtr = PushAsciiString(guestPath);

        var randPtr = PushBytes(randBytes);
        Logger.LogInformation("Pushing AT_RANDOM to guest stack at 0x{Ptr:X}: {Bytes}", randPtr,
            BitConverter.ToString(randBytes));

        Span<uint> auxv = stackalloc uint[]
        {
            LinuxConstants.AT_PLATFORM, platPtr,
            LinuxConstants.AT_EXECFN, execFnPtr,
            LinuxConstants.AT_RANDOM, randPtr,
            LinuxConstants.AT_UID, 0,
            LinuxConstants.AT_EUID, 0,
            LinuxConstants.AT_GID, 0,
            LinuxConstants.AT_EGID, 0,
            LinuxConstants.AT_PHNUM, mainInfo.PhNum,
            LinuxConstants.AT_PHENT, mainInfo.PhEnt,
            LinuxConstants.AT_PHDR, mainInfo.PhdrAddr,
            LinuxConstants.AT_PAGESZ, LinuxConstants.PageSize,
            LinuxConstants.AT_ENTRY, mainInfo.EntryPoint,
            LinuxConstants.AT_BASE, interpBase,
            LinuxConstants.AT_FLAGS, 0,
            LinuxConstants.AT_SECURE, 0,
            LinuxConstants.AT_HWCAP, 0x0fcbfbfd,
            LinuxConstants.AT_HWCAP2, 0,
            LinuxConstants.AT_CLKTCK, 100,
            LinuxConstants.AT_NULL, 0
        };

        // ABI: Stack pointer should be 16-byte aligned at process entry (where argc is).
        // We push argc + argv[] + null + envp[] + null + auxv key/value pairs.
        if (auxv.Length != AuxEntryCount * 2)
            throw new InvalidOperationException($"Unexpected auxv size: {auxv.Length}.");

        sp = finalSp + (uint)totalSize;

        for (var i = auxv.Length - 1; i >= 0; i--) PushUint32(auxv[i]);

        PushUint32(0);
        for (var i = envPtrs.Length - 1; i >= 0; i--) PushUint32(envPtrs[i]);

        PushUint32(0);
        for (var i = argPtrs.Length - 1; i >= 0; i--) PushUint32(argPtrs[i]);

        PushUint32((uint)args.Length);

        sys.CurrentSyscallEngine.ResetAllCodeCache();

        // If there's an interpreter, start execution at interpreter's entry point; 
        // otherwise at the main binary's entry point.
        var entryPoint = mainInfo.InterpPath != null ? interpEntry : mainInfo.EntryPoint;

        var res = new LoaderResult
        {
            Entry = entryPoint,
            SP = sp,
            InitialStack = stackData.AsSpan((int)(sp - finalSp)).ToArray(),
            BrkAddr = mainInfo.BrkAddr
        };
        Logger.LogInformation(
            "ElfLoader Entry=0x{Entry:x} SP=0x{SP:x} Brk=0x{Brk:x} InitialStackLen={StackLen} InterpBase=0x{InterpBase:x}",
            res.Entry, res.SP, res.BrkAddr, res.InitialStack.Length, interpBase);
        return res;
    }

    /// <summary>
    ///     Load all PT_LOAD segments from an ELF file into guest memory.
    ///     Also detects PT_INTERP and PT_PHDR.
    /// </summary>
    private static ElfLoadInfo LoadSegments(ElfFile elf, uint loadBase, VMAManager mm, Engine engine,
        Dentry? dentry, string fileDesc, Stream elfStream, Mount? mount = null)
    {
        static string ReadAsciiNullTerminated(Stream stream, long position, int size)
        {
            stream.Seek(position, SeekOrigin.Begin);
            byte[]? rented = null;
            var buffer = size <= 256
                ? stackalloc byte[size]
                : (rented = ArrayPool<byte>.Shared.Rent(size)).AsSpan(0, size);
            try
            {
                stream.ReadExactly(buffer);
                var textLen = buffer.IndexOf((byte)0);
                if (textLen < 0) textLen = buffer.Length;
                return Encoding.ASCII.GetString(buffer[..textLen]);
            }
            finally
            {
                if (rented != null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }

        var info = new ElfLoadInfo
        {
            PhEnt = elf.ProgramHeaderEntrySize,
            EntryPoint = elf.EntryPointAddress + loadBase
        };

        uint firstLoadVaddr = 0;
        var foundFirstLoad = false;

        foreach (var segment in elf.Segments)
        {
            if (segment.Type == ElfSegmentType.Load)
            {
                if (!foundFirstLoad)
                {
                    firstLoadVaddr = segment.VirtualAddress;
                    foundFirstLoad = true;
                }

                var perms = Protection.None;
                if ((segment.Flags & ElfSegmentFlags.Executable) != 0) perms |= Protection.Exec;
                if ((segment.Flags & ElfSegmentFlags.Writable) != 0) perms |= Protection.Write;
                if ((segment.Flags & ElfSegmentFlags.Readable) != 0) perms |= Protection.Read;

                var vaddrRaw = segment.VirtualAddress + loadBase;
                var offsetRaw = segment.Position;
                var sizeRaw = segment.SizeInMemory;

                var pageStart = vaddrRaw & LinuxConstants.PageMask;
                var pageOffset = offsetRaw & LinuxConstants.PageMask;
                if (sizeRaw > 0)
                {
                    if (dentry == null)
                        throw new FileNotFoundException($"Could not find file in VFS or Host for mapping: {fileDesc}");

                    var bssStart = vaddrRaw + segment.Size;
                    var bssEnd = vaddrRaw + sizeRaw;

                    // Map the FILE portion (which includes the overlapping BSS on the last page)
                    var fileMapEnd = (bssStart + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
                    var fileMapLen = fileMapEnd - pageStart;

                    if (fileMapLen > 0)
                    {
                        var vfsFile = new LinuxFile(dentry, FileFlags.O_RDONLY, mount!);
                        ProcessAddressSpaceSync.Mmap(mm, engine, pageStart, fileMapLen, perms,
                            MapFlags.Private | MapFlags.Fixed, vfsFile, pageOffset, "ELF_LOAD");

                        if (bssStart < fileMapEnd)
                            mm.FindVmArea(pageStart)?.SetSyntheticZeroRange(bssStart, fileMapEnd);
                    }

                    // Zero-fill the BSS portion on the last file page
                    // CopyToUser already triggers HandleFault via PageFaultResolver when needed
                    if (bssStart < fileMapEnd)
                    {
                        var zeroLen = (int)(fileMapEnd - bssStart);
                        var zeroes = new byte[zeroLen];
                        if (!engine.CopyToUser(bssStart, zeroes))
                            Logger.LogWarning("ElfLoader: Failed to zero-fill BSS tail at 0x{Addr:x} (len {Len})",
                                bssStart, zeroLen);
                    }

                    // Map the remaining full BSS pages as Anonymous
                    if (bssEnd > fileMapEnd)
                    {
                        var anonLen = (bssEnd - fileMapEnd + LinuxConstants.PageOffsetMask) &
                                      LinuxConstants.PageMask;
                        ProcessAddressSpaceSync.Mmap(mm, engine, fileMapEnd, anonLen, perms,
                            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "ELF_BSS");
                    }
                }

                // Track brk
                var end = segment.VirtualAddress + segment.SizeInMemory + loadBase;
                end = (end + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
                if (end > info.BrkAddr) info.BrkAddr = end;
            }

            if (segment.Type == ElfSegmentType.ProgramHeader)
                info.PhdrAddr = segment.VirtualAddress + loadBase;

            // Detect PT_INTERP
            if (segment.Type == ElfSegmentType.Interpreter)
                info.InterpPath = ReadAsciiNullTerminated(elfStream, segment.Position, checked((int)segment.Size));

            info.PhNum++;
        }

        if (info.PhdrAddr == 0 && foundFirstLoad)
            info.PhdrAddr = firstLoadVaddr + loadBase + elf.ElfHeaderSize;

        info.FirstLoadVaddr = firstLoadVaddr + loadBase;

        Logger.LogDebug(
            "ElfLoader: [{FileDesc}] loadBase=0x{LoadBase:x} phdrAddr=0x{PhdrAddr:x} phnum={PhNum} entry=0x{Entry:x} brk=0x{Brk:x}",
            fileDesc, loadBase, info.PhdrAddr, info.PhNum, info.EntryPoint, info.BrkAddr);

        return info;
    }

    // Auxv types moved to LinuxConstants

    /// <summary>
    ///     Result of loading an ELF's LOAD segments into guest memory.
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
}
