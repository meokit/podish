using System.Buffers;
using System.Buffers.Binary;
using Fiberish.Auth.Cred;
using ExecIdentity = Fiberish.Auth.Cred.CredentialService.ExecIdentity;
using Fiberish.Core;
using Fiberish.Diagnostics;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using LinuxFile = Fiberish.VFS.LinuxFile;

namespace Fiberish.Loader;

public struct LoaderResult
{
    public uint Entry { get; set; }
    public uint SP { get; set; }
    /// <summary>
    ///     Initial stack bytes, as a slice of the loader-owned backing array. Callers must copy or
    ///     consume it before the result goes out of scope.
    /// </summary>
    public ArraySegment<byte> InitialStack { get; set; }
    public uint BrkAddr { get; set; }
    public GuestAddressSpaceLayout Layout { get; set; }
}

public class ElfLoader
{
    private static readonly ILogger Logger = Logging.CreateLogger<ElfLoader>();

    public static LoaderResult Load(Dentry dentry, string guestPath, SyscallManager sys, string[] args,
        string[] envs, Mount mount, GuestAddressSpaceLayout layout, ExecIdentity identity)
    {
        var guestPathRaw = FsEncoding.EncodeUtf8(guestPath);
        return Load(dentry, guestPathRaw, guestPathRaw, sys,
            args.Select(FsEncoding.EncodeUtf8).ToArray(),
            envs.Select(FsEncoding.EncodeUtf8).ToArray(), mount, layout, identity);
    }

    public static LoaderResult Load(Dentry dentry, byte[] guestPathRaw, byte[] execFnPathRaw, SyscallManager sys,
        byte[][] argsRaw, byte[][] envsRaw, Mount mount, GuestAddressSpaceLayout layout, ExecIdentity identity,
        Process? process = null)
    {
        var mm = sys.Mem;
        var engine = sys.CurrentSyscallEngine;

        ElfLoadInfo mainInfo;
        {
            var mainFile = new LinuxFile(dentry, FileFlags.O_RDONLY, mount);
            using var mainStream = new VfsStream(mainFile);
            var elf = Elf32Reader.Read(mainStream);
            if (elf.FileType is not (ElfFileType.Executable or ElfFileType.Dynamic))
                throw new InvalidDataException("ELF is not an executable image.");

            var loadBase = elf.FileType == ElfFileType.Executable ? 0u : layout.PieBase;
            if (Logger.IsEnabled(LogLevel.Debug))
                Logger.LogDebug("ElfLoader: {Filename} FileType={Type}, selected loadBase=0x{LoadBase:x}",
                    FsEncoding.DecodeUtf8Lossy(guestPathRaw), elf.FileType, loadBase);

            mainInfo = LoadSegments(elf, loadBase, mm, engine, dentry, guestPathRaw, mainStream, mainFile, mount);
        }

        uint interpBase = 0;
        var interpEntry = mainInfo.EntryPoint;

        if (mainInfo.InterpPathRaw != null)
        {
            var lookup = sys.PathWalker.PathWalkWithData(mainInfo.InterpPathRaw,
                mainInfo.InterpPathRaw.Length, null, LookupFlags.FollowSymlink);
            if (lookup.HasError)
                throw new ExecFileException((Errno)(-lookup.ErrorCode), "ELF interpreter path lookup failed.");
            var interpLoc = lookup.Path;
            if (!interpLoc.IsValid)
                throw new ExecFileException(Errno.ENOENT, "ELF interpreter not found.");

            var interpDentry = interpLoc.Dentry!;
            var interpMount = interpLoc.Mount!;
            var accessRc = (process ?? sys.CurrentProcess)?.CheckExecutableAccess(interpDentry, interpMount) ?? 0;
            if (accessRc < 0)
                throw new ExecFileException((Errno)(-accessRc), "ELF interpreter execute access denied.");

            var interpFile = new LinuxFile(interpDentry, FileFlags.O_RDONLY, interpMount);
            using var interpStream = new VfsStream(interpFile);
            ElfFile interpElf;
            try
            {
                interpElf = Elf32Reader.Read(interpStream);
                if (interpElf.FileType is not (ElfFileType.Executable or ElfFileType.Dynamic))
                    throw new InvalidDataException("ELF interpreter is not an executable image.");
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or NotSupportedException)
            {
                throw new ExecFileException(Errno.ELIBBAD, "Invalid ELF interpreter format.", ex);
            }

            if (interpElf.FileType == ElfFileType.Dynamic)
                interpBase = layout.InterpreterBaseHint;

            if (Logger.IsEnabled(LogLevel.Debug))
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
        long argumentBytes = ((long)argsRaw.Length + envsRaw.Length) * 4 + execFnPathRaw.Length + 1;
        foreach (var arg in argsRaw)
        {
            if (arg.Length >= LinuxConstants.MaxArgStrlen)
                throw new OverflowException("Exec argument exceeds MAX_ARG_STRLEN.");
            variableDataSize += checked((uint)arg.Length + 1u);
            argumentBytes += arg.Length + 1;
        }
        foreach (var env in envsRaw)
        {
            if (env.Length >= LinuxConstants.MaxArgStrlen)
                throw new OverflowException("Exec environment exceeds MAX_ARG_STRLEN.");
            variableDataSize += checked((uint)env.Length + 1u);
            argumentBytes += env.Length + 1;
        }
        // Recheck rewritten shebang vectors/public loader inputs in the same pass
        // used to size the stack. The syscall reader already shares its budget.
        if (argumentBytes > sys.GetExecArgMaxBytes(process))
            throw new OverflowException("Combined exec vector budget exceeded.");
        variableDataSize += 5;
        // AT_EXECFN is the path passed to execve: for a script that is the script path,
        // not the resolved interpreter path used for EXECUTABLE_PATH/AT_PHDR.
        variableDataSize += checked((uint)execFnPathRaw.Length + 1u);

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

        // Write vector slots directly into the final stack buffer. No temporary
        // uint[argc]/uint[envc] allocations, even for very large exec vectors.
        for (var i = argsRaw.Length - 1; i >= 0; i--)
        {
            var ptr = PushCStringBytes(argsRaw[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(stackData.AsSpan(4 + i * 4, 4), ptr);
        }
        var envpOffset = (argsRaw.Length + 2) * 4;
        for (var i = envsRaw.Length - 1; i >= 0; i--)
        {
            var ptr = PushCStringBytes(envsRaw[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(stackData.AsSpan(envpOffset + i * 4, 4), ptr);
        }

        var platPtr = PushCStringBytes("i686"u8);
        var execFnPtr = PushCStringBytes(execFnPathRaw);

        var randPtr = PushBytes(randBytes);
        if (Logger.IsEnabled(LogLevel.Information))
            Logger.LogInformation("Pushing AT_RANDOM to guest stack at 0x{Ptr:X}: {Bytes}", randPtr,
                BitConverter.ToString(randBytes));

        Span<uint> auxv = stackalloc uint[]
        {
            LinuxConstants.AT_PLATFORM, platPtr,
            LinuxConstants.AT_EXECFN, execFnPtr,
            LinuxConstants.AT_RANDOM, randPtr,
            LinuxConstants.AT_UID, unchecked((uint)identity.Uid),
            LinuxConstants.AT_EUID, unchecked((uint)identity.Euid),
            LinuxConstants.AT_GID, unchecked((uint)identity.Gid),
            LinuxConstants.AT_EGID, unchecked((uint)identity.Egid),
            LinuxConstants.AT_PHNUM, mainInfo.PhNum,
            LinuxConstants.AT_PHENT, mainInfo.PhEnt,
            LinuxConstants.AT_PHDR, mainInfo.PhdrAddr,
            LinuxConstants.AT_PAGESZ, LinuxConstants.PageSize,
            LinuxConstants.AT_ENTRY, mainInfo.EntryPoint,
            LinuxConstants.AT_BASE, interpBase,
            LinuxConstants.AT_FLAGS, 0,
            LinuxConstants.AT_SECURE, identity.Secure ? 1u : 0u,
            LinuxConstants.AT_HWCAP, 0x0fcbfbfd,
            LinuxConstants.AT_HWCAP2, 0,
            LinuxConstants.AT_CLKTCK, 100,
            LinuxConstants.AT_NULL, 0
        };

        if (auxv.Length != AuxEntryCount * 2)
            throw new InvalidOperationException($"Unexpected auxv size: {auxv.Length}.");

        sp = finalSp + (uint)totalSize;

        for (var i = auxv.Length - 1; i >= 0; i--) PushUint32(auxv[i]);

        // Vector terminators are already zero in the freshly allocated buffer.
        BinaryPrimitives.WriteUInt32LittleEndian(stackData, (uint)argsRaw.Length);
        sp = finalSp;

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
            InitialStack = new ArraySegment<byte>(stackData, (int)(sp - finalSp),
                stackData.Length - (int)(sp - finalSp)),
            BrkAddr = mainInfo.BrkAddr,
            Layout = layout
        };
        if (Logger.IsEnabled(LogLevel.Information))
            Logger.LogInformation(
                "ElfLoader Entry=0x{Entry:x} SP=0x{SP:x} Brk=0x{Brk:x} InitialStackLen={StackLen} InterpBase=0x{InterpBase:x}",
                res.Entry, res.SP, res.BrkAddr, res.InitialStack.Count, interpBase);
        return res;
    }

    private static ElfLoadInfo LoadSegments(ElfFile elf, uint loadBase, VMAManager mm, Engine engine,
        Dentry? dentry, byte[] fileDescRaw, Stream elfStream, LinuxFile? sharedFile, Mount? mount = null)
    {
        static byte[]? ReadNulTerminatedBytes(Stream stream, long position, int size)
        {
            if (size < 2 || size > LinuxConstants.PathMax)
                throw new InvalidDataException("Invalid PT_INTERP path length.");
            stream.Seek(position, SeekOrigin.Begin);
            byte[]? rented = null;
            var buffer = size <= 256
                ? stackalloc byte[size]
                : (rented = ArrayPool<byte>.Shared.Rent(size)).AsSpan(0, size);
            try
            {
                stream.ReadExactly(buffer);
                if (buffer[^1] != 0)
                    throw new InvalidDataException("PT_INTERP path is not NUL terminated.");
                return buffer[..buffer.IndexOf((byte)0)].ToArray();
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
                var taskSize = mm.Layout?.TaskSize ?? LinuxConstants.TaskSize32;
                if (segment.Size > segment.SizeInMemory ||
                    (ulong)segment.VirtualAddress + loadBase + segment.SizeInMemory > taskSize ||
                    ((segment.VirtualAddress ^ segment.Position) & LinuxConstants.PageOffsetMask) != 0)
                    throw new FatalExecException("Invalid ELF PT_LOAD after Linux's point of no return.");
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

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug(
                "ElfLoader: [{FileDesc}] loadBase=0x{LoadBase:x} phdrAddr=0x{PhdrAddr:x} phnum={PhNum} entry=0x{Entry:x} brk=0x{Brk:x}",
                FsEncoding.DecodeUtf8Lossy(fileDescRaw), loadBase, info.PhdrAddr, info.PhNum, info.EntryPoint,
            info.BrkAddr);

        return info;
    }

    private struct ElfLoadInfo
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
