using System.Buffers;
using System.Buffers.Binary;
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
    public GuestAddressSpaceLayout Layout { get; set; } = null!;
}

public class ElfLoader
{
    private static readonly ILogger Logger = Logging.CreateLogger<ElfLoader>();

    public static LoaderResult Load(Dentry dentry, string guestPath, SyscallManager sys, string[] args,
        string[] envs, Mount mount, GuestAddressSpaceLayout layout)
    {
        return Load(dentry, FsEncoding.EncodeUtf8(guestPath), sys,
            args.Select(FsEncoding.EncodeUtf8).ToArray(),
            envs.Select(FsEncoding.EncodeUtf8).ToArray(), mount, layout);
    }

    public static LoaderResult Load(Dentry dentry, byte[] guestPathRaw, SyscallManager sys, byte[][] argsRaw,
        byte[][] envsRaw, Mount mount, GuestAddressSpaceLayout layout)
    {
        var mm = sys.Mem;
        var engine = sys.CurrentSyscallEngine;

        ElfLoadInfo mainInfo;
        {
            var mainFile = new LinuxFile(dentry, FileFlags.O_RDONLY, mount);
            using var mainStream = new VfsStream(mainFile);
            var elf = Elf32Reader.Read(mainStream);

            var loadBase = elf.FileType == ElfFileType.Executable ? 0u : layout.PieBase;
            Logger.LogDebug("ElfLoader: {Filename} FileType={Type}, selected loadBase=0x{LoadBase:x}",
                FsEncoding.DecodeUtf8Lossy(guestPathRaw), elf.FileType, loadBase);

            mainInfo = LoadSegments(elf, loadBase, mm, engine, dentry, guestPathRaw, mainStream, mainFile, mount);
        }

        uint interpBase = 0;
        var interpEntry = mainInfo.EntryPoint;

        if (mainInfo.InterpPathRaw != null)
        {
            var (interpLoc, _, _) = sys.ResolvePathBytes(mainInfo.InterpPathRaw);
            if (!interpLoc.IsValid)
                throw new FileNotFoundException(
                    $"Interpreter not found in VFS: {FsEncoding.DecodeUtf8Lossy(mainInfo.InterpPathRaw)}");

            var interpDentry = interpLoc.Dentry!;
            var interpMount = interpLoc.Mount!;

            var interpFile = new LinuxFile(interpDentry, FileFlags.O_RDONLY, interpMount);
            using var interpStream = new VfsStream(interpFile);
            var interpElf = Elf32Reader.Read(interpStream);

            if (interpElf.FileType == ElfFileType.Dynamic)
                interpBase = layout.InterpreterBaseHint;
            else
                throw new NotSupportedException($"Unsupported interpreter type: {interpElf.FileType}");

            Logger.LogDebug("ElfLoader: Loading interpreter at base=0x{InterpBase:x}", interpBase);
            var interpInfo = LoadSegments(interpElf, interpBase, mm, engine, interpDentry, mainInfo.InterpPathRaw,
                interpStream, interpFile, interpMount);
            interpEntry = interpInfo.EntryPoint;
        }

        var randBytes = layout.AuxRandomBytes;

        const int AuxEntryCount = 19;
        var totalWords = 1 + argsRaw.Length + 1 + envsRaw.Length + 1 + AuxEntryCount * 2;
        var totalSize = totalWords * 4;

        uint variableDataSize = 16;
        foreach (var arg in argsRaw) variableDataSize += checked((uint)arg.Length + 1u);
        foreach (var env in envsRaw) variableDataSize += checked((uint)env.Length + 1u);
        variableDataSize += 5;
        variableDataSize += checked((uint)guestPathRaw.Length + 1u);

        var initialTargetSp = layout.InitialStackTop - variableDataSize - (uint)totalSize;
        var finalSp = initialTargetSp & ~0xFu;
        var stackData = new byte[layout.InitialStackTop - finalSp];
        var sp = layout.InitialStackTop;

        uint PushBytes(ReadOnlySpan<byte> bytes)
        {
            sp -= (uint)bytes.Length;
            bytes.CopyTo(stackData.AsSpan((int)(sp - finalSp)));
            return sp;
        }

        uint PushCStringBytes(ReadOnlySpan<byte> value)
        {
            var byteCount = value.Length;
            sp -= (uint)(byteCount + 1);
            var dest = stackData.AsSpan((int)(sp - finalSp), byteCount + 1);
            value.CopyTo(dest);
            dest[byteCount] = 0;
            return sp;
        }

        void PushUint32(uint value)
        {
            sp -= 4;
            BinaryPrimitives.WriteUInt32LittleEndian(stackData.AsSpan((int)(sp - finalSp), 4), value);
        }

        var argPtrs = new uint[argsRaw.Length];
        for (var i = argsRaw.Length - 1; i >= 0; i--) argPtrs[i] = PushCStringBytes(argsRaw[i]);

        var envPtrs = new uint[envsRaw.Length];
        for (var i = envsRaw.Length - 1; i >= 0; i--) envPtrs[i] = PushCStringBytes(envsRaw[i]);

        var platPtr = PushCStringBytes("i686"u8);
        var execFnPtr = PushCStringBytes(guestPathRaw);

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

        if (auxv.Length != AuxEntryCount * 2)
            throw new InvalidOperationException($"Unexpected auxv size: {auxv.Length}.");

        sp = finalSp + (uint)totalSize;

        for (var i = auxv.Length - 1; i >= 0; i--) PushUint32(auxv[i]);

        PushUint32(0);
        for (var i = envPtrs.Length - 1; i >= 0; i--) PushUint32(envPtrs[i]);

        PushUint32(0);
        for (var i = argPtrs.Length - 1; i >= 0; i--) PushUint32(argPtrs[i]);

        PushUint32((uint)argsRaw.Length);

        var stackMapStart = finalSp & LinuxConstants.PageMask;
        if (stackMapStart < layout.StackLowerBound)
            throw new InvalidOperationException(
                $"Initial stack exceeds RLIMIT-derived range: start=0x{stackMapStart:x}, lower=0x{layout.StackLowerBound:x}");

        var stackMapLen = layout.InitialStackTop - stackMapStart;
        ProcessAddressSpaceSync.Mmap(mm, engine, stackMapStart, stackMapLen, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous | MapFlags.GrowDown | MapFlags.Stack,
            null, 0, "STACK");

        var stackVma = mm.FindVmArea(sp);
        if (stackVma == null)
            throw new InvalidOperationException("Initial stack VMA missing after mmap.");

        stackVma.GrowDownLimit = layout.StackLowerBound;
        stackVma.GrowDownGuardGap = layout.StackGuardGap;
        stackVma.VmPgoff = (stackVma.Start - layout.StackLowerBound) / LinuxConstants.PageSize;

        var res = new LoaderResult
        {
            Entry = mainInfo.InterpPathRaw != null ? interpEntry : mainInfo.EntryPoint,
            SP = sp,
            InitialStack = stackData.AsSpan((int)(sp - finalSp)).ToArray(),
            BrkAddr = mainInfo.BrkAddr,
            Layout = layout
        };
        Logger.LogInformation(
            "ElfLoader Entry=0x{Entry:x} SP=0x{SP:x} Brk=0x{Brk:x} InitialStackLen={StackLen} InterpBase=0x{InterpBase:x}",
            res.Entry, res.SP, res.BrkAddr, res.InitialStack.Length, interpBase);
        return res;
    }

    private static ElfLoadInfo LoadSegments(ElfFile elf, uint loadBase, VMAManager mm, Engine engine,
        Dentry? dentry, byte[] fileDescRaw, Stream elfStream, LinuxFile? sharedFile, Mount? mount = null)
    {
        static byte[]? ReadNulTerminatedBytes(Stream stream, long position, int size)
        {
            stream.Seek(position, SeekOrigin.Begin);
            byte[]? rented = null;
            var buffer = size <= 256
                ? stackalloc byte[size]
                : (rented = ArrayPool<byte>.Shared.Rent(size)).AsSpan(0, size);
            try
            {
                stream.ReadExactly(buffer);
                var nulIndex = buffer.IndexOf((byte)0);
                return nulIndex >= 0 ? buffer[..nulIndex].ToArray() : buffer.ToArray();
            }
            finally
            {
                if (rented != null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }

        static void MmapSharedFile(VMAManager mm, Engine engine, uint pageStart, uint fileMapLen, Protection perms,
            LinuxFile sharedFile, long pageOffset)
        {
            sharedFile.Get();
            try
            {
                ProcessAddressSpaceSync.Mmap(mm, engine, pageStart, fileMapLen, perms,
                    MapFlags.Private | MapFlags.Fixed, sharedFile, pageOffset, "ELF_LOAD");
            }
            catch
            {
                sharedFile.Close();
                throw;
            }
        }

        var info = new ElfLoadInfo
        {
            PhEnt = elf.ProgramHeaderEntrySize,
            EntryPoint = elf.EntryPointAddress + loadBase
        };

        uint firstLoadVaddr = 0;
        var foundFirstLoad = false;

        Span<byte> bssZeroPage = stackalloc byte[LinuxConstants.PageSize];
        bssZeroPage.Clear();

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
                        throw new FileNotFoundException(
                            $"Could not find file in VFS or Host for mapping: {FsEncoding.DecodeUtf8Lossy(fileDescRaw)}");

                    var bssStart = vaddrRaw + segment.Size;
                    var bssEnd = vaddrRaw + sizeRaw;

                    var fileMapEnd = (bssStart + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
                    var fileMapLen = fileMapEnd - pageStart;

                    if (fileMapLen > 0)
                    {
                        if (sharedFile == null)
                            throw new InvalidOperationException("ELF segment mapping requires a shared file.");

                        MmapSharedFile(mm, engine, pageStart, fileMapLen, perms, sharedFile, pageOffset);

                        if (bssStart < fileMapEnd)
                            mm.FindVmArea(pageStart)?.SetSyntheticZeroRange(bssStart, fileMapEnd);
                    }

                    if (bssStart < fileMapEnd)
                    {
                        var zeroLen = (int)(fileMapEnd - bssStart);
                        if (!engine.CopyToUser(bssStart, bssZeroPage[..zeroLen]))
                            Logger.LogWarning("ElfLoader: Failed to zero-fill BSS tail at 0x{Addr:x} (len {Len})",
                                bssStart, zeroLen);
                    }

                    if (bssEnd > fileMapEnd)
                    {
                        var anonLen = (bssEnd - fileMapEnd + LinuxConstants.PageOffsetMask) &
                                      LinuxConstants.PageMask;
                        ProcessAddressSpaceSync.Mmap(mm, engine, fileMapEnd, anonLen, perms,
                            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "ELF_BSS");
                    }
                }

                var end = segment.VirtualAddress + segment.SizeInMemory + loadBase;
                end = (end + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
                if (end > info.BrkAddr) info.BrkAddr = end;
            }

            if (segment.Type == ElfSegmentType.ProgramHeader)
                info.PhdrAddr = segment.VirtualAddress + loadBase;

            if (segment.Type == ElfSegmentType.Interpreter)
                info.InterpPathRaw = ReadNulTerminatedBytes(elfStream, segment.Position, checked((int)segment.Size));

            info.PhNum++;
        }

        if (info.PhdrAddr == 0 && foundFirstLoad)
            info.PhdrAddr = firstLoadVaddr + loadBase + elf.ElfHeaderSize;

        info.FirstLoadVaddr = firstLoadVaddr + loadBase;

        Logger.LogDebug(
            "ElfLoader: [{FileDesc}] loadBase=0x{LoadBase:x} phdrAddr=0x{PhdrAddr:x} phnum={PhNum} entry=0x{Entry:x} brk=0x{Brk:x}",
            FsEncoding.DecodeUtf8Lossy(fileDescRaw), loadBase, info.PhdrAddr, info.PhNum, info.EntryPoint,
            info.BrkAddr);

        return info;
    }

    private class ElfLoadInfo
    {
        public uint PhdrAddr { get; set; }
        public uint PhNum { get; set; }
        public uint PhEnt { get; set; }
        public uint EntryPoint { get; set; }
        public uint BrkAddr { get; set; }
        public uint FirstLoadVaddr { get; set; }
        public byte[]? InterpPathRaw { get; set; }
    }
}