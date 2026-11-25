using System.Buffers.Binary;
using System.Numerics;
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

    internal sealed record PreparedImage(LinuxFile File, ElfFile Elf, byte[]? InterpPathRaw);

    internal static (LinuxFile File, VfsStream Stream) OpenForExec(Dentry dentry, Mount mount, FiberTask task)
    {
        var file = new LinuxFile(dentry, FileFlags.O_RDONLY, mount);
        return (file, new VfsStream(file, task));
    }

    internal static async ValueTask<PreparedImage> ReadImageAsync(LinuxFile file, VfsStream stream, FiberTask task,
        GuestArchitecture? expectedArchitecture = null)
    {
        var elf = await ElfReader.ReadAsync(stream, task);
        ValidateExecutable(elf, expectedArchitecture);

        byte[]? interpPath = null;
        foreach (var segment in elf.Segments)
        {
            if (segment.Type != ElfSegmentType.Interpreter) continue;
            var bytes = new byte[InterpreterPathSize(segment)];
            stream.Position = InterpreterPathOffset(segment, stream.Length, bytes.Length);
            await stream.ReadExactlyForTaskAsync(task, bytes);
            interpPath = ParseInterpreterPath(bytes);
        }

        return new PreparedImage(file, elf, interpPath);
    }

    private static void ValidateExecutable(ElfFile elf, GuestArchitecture? expectedArchitecture = null)
    {
        if (elf.FileType is not (ElfFileType.Executable or ElfFileType.Dynamic))
            throw new InvalidDataException("ELF is not an executable image.");
        var supportedArchitecture = elf.Architecture switch
        {
#if FIBER_GUEST_I386
            GuestArchitecture.I386 => true,
#endif
#if FIBER_GUEST_AARCH64
            GuestArchitecture.AArch64 => true,
#endif
            _ => false
        };
        if (!supportedArchitecture || (expectedArchitecture.HasValue && elf.Architecture != expectedArchitecture.Value))
            throw new NotSupportedException($"Unsupported guest architecture {elf.Architecture} for this ELF image.");
        if (elf.Architecture == GuestArchitecture.AArch64 && elf.Segments.Length == 0)
            throw new InvalidDataException("ELF64 executable has no program headers.");
    }

    internal static bool IsInvalidElfFormat(Exception ex) =>
        ex is InvalidDataException or EndOfStreamException or NotSupportedException;

    internal static PathLocation ResolveInterpreter(byte[] path, SyscallManager sys, Process? process)
    {
        var lookup = sys.PathWalker.PathWalkWithData(path, path.Length, null, LookupFlags.FollowSymlink);
        if (lookup.HasError)
            throw new ExecFileException((Errno)(-lookup.ErrorCode), "ELF interpreter path lookup failed.");
        if (!lookup.Path.IsValid)
            throw new ExecFileException(Errno.ENOENT, "ELF interpreter not found.");

        var accessRc = (process ?? sys.CurrentProcess)?.CheckExecutableAccess(lookup.Path.Dentry!, lookup.Path.Mount!) ?? 0;
        if (accessRc < 0)
            throw new ExecFileException((Errno)(-accessRc), "ELF interpreter execute access denied.");
        return lookup.Path;
    }

    public static LoaderResult Load(Dentry dentry, byte[] guestPathRaw, byte[] execFnPathRaw, SyscallManager sys,
        byte[][] argsRaw, byte[][] envsRaw, Mount mount, GuestAddressSpaceLayout layout, ExecIdentity identity,
        Process? process = null) =>
        Load(dentry, guestPathRaw, execFnPathRaw, sys, argsRaw, envsRaw, mount, layout, identity, process, null, null);

    internal static LoaderResult Load(Dentry dentry, byte[] guestPathRaw, byte[] execFnPathRaw, SyscallManager sys,
        byte[][] argsRaw, byte[][] envsRaw, Mount mount, GuestAddressSpaceLayout layout, ExecIdentity identity,
        Process? process, PreparedImage? preparedMain, PreparedImage? preparedInterpreter)
    {
        var mm = sys.Mem;
        var engine = sys.CurrentSyscallEngine;

        ElfLoadInfo LoadMain(ElfFile elf, LinuxFile file, Stream? stream)
        {
            engine.SelectArchitecture(elf.Architecture);
            sys.SetupVDSO();
            return LoadSegments(elf, GetLoadBase(elf, layout.PieBase), mm, engine, guestPathRaw, file, stream);
        }

        ElfLoadInfo mainInfo;
        GuestArchitecture architecture;
        if (preparedMain != null)
        {
            architecture = preparedMain.Elf.Architecture;
            mainInfo = LoadMain(preparedMain.Elf, preparedMain.File, null);
            mainInfo.InterpPathRaw = preparedMain.InterpPathRaw;
        }
        else
        {
            var file = new LinuxFile(dentry, FileFlags.O_RDONLY, mount);
            using var stream = new VfsStream(file);
            var elf = ElfReader.Read(stream);
            ValidateExecutable(elf);
            architecture = elf.Architecture;
            mainInfo = LoadMain(elf, file, stream);
        }

        uint interpBase = 0;
        var interpEntry = mainInfo.EntryPoint;

        if (mainInfo.InterpPathRaw is { } interpPath)
        {
            uint LoadInterpreter(ElfFile elf, LinuxFile file, Stream? stream)
            {
                interpBase = GetLoadBase(elf, layout.InterpreterBaseHint);
                return LoadSegments(elf, interpBase, mm, engine, interpPath, file, stream).EntryPoint;
            }

            if (preparedMain != null)
            {
                var interpreter = preparedInterpreter ?? throw new InvalidOperationException("ELF interpreter was not prepared.");
                interpEntry = LoadInterpreter(interpreter.Elf, interpreter.File, null);
            }
            else
            {
                var loc = ResolveInterpreter(interpPath, sys, process);
                var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount!);
                using var stream = new VfsStream(file);
                ElfFile elf;
                try
                {
                    elf = ElfReader.Read(stream);
                    ValidateExecutable(elf, architecture);
                }
                catch (Exception ex) when (IsInvalidElfFormat(ex))
                {
                    throw new ExecFileException(Errno.ELIBBAD, "Invalid ELF interpreter format.", ex);
                }

                interpEntry = LoadInterpreter(elf, file, stream);
            }
        }

        var randBytes = layout.AuxRandomBytes;
        var isAArch64 = architecture == GuestArchitecture.AArch64;
        var wordSize = isAArch64 ? sizeof(ulong) : sizeof(uint);
        var platform = isAArch64 ? "aarch64"u8 : "i686"u8;

        const int AuxEntryCount = 19;
        var totalSize = checked((argsRaw.Length + envsRaw.Length + 3 + AuxEntryCount * 2) * wordSize);

        long argumentBytes = ((long)argsRaw.Length + envsRaw.Length) * wordSize + execFnPathRaw.Length + 1;
        var variableDataSize = (long)randBytes.Length + platform.Length + 1 + execFnPathRaw.Length + 1;
        foreach (var arg in argsRaw)
        {
            if (arg.Length >= LinuxConstants.MaxArgStrlen)
                throw new OverflowException("Exec argument exceeds MAX_ARG_STRLEN.");
            variableDataSize += arg.Length + 1;
            argumentBytes += arg.Length + 1;
        }
        foreach (var env in envsRaw)
        {
            if (env.Length >= LinuxConstants.MaxArgStrlen)
                throw new OverflowException("Exec environment exceeds MAX_ARG_STRLEN.");
            variableDataSize += env.Length + 1;
            argumentBytes += env.Length + 1;
        }
        // Recheck rewritten shebang vectors/public loader inputs in the same pass
        // used to size the stack. The syscall reader already shares its budget.
        if (argumentBytes > sys.GetExecArgMaxBytes(process))
            throw new OverflowException("Combined exec vector budget exceeded.");
        // AT_EXECFN is the path passed to execve: for a script that is the script path,
        // not the resolved interpreter path used for EXECUTABLE_PATH/AT_PHDR.
        var initialTargetSp = checked(layout.InitialStackTop - (uint)(variableDataSize + totalSize));
        var finalSp = initialTargetSp & ~0xFu;
        var stackMapStart = finalSp & LinuxConstants.PageMask;
        if (stackMapStart < layout.StackLowerBound)
            throw new InvalidOperationException(
                $"Initial stack exceeds RLIMIT-derived range: start=0x{stackMapStart:x}, lower=0x{layout.StackLowerBound:x}");

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

        void WriteWord(int offset, uint value)
        {
            if (isAArch64)
                BinaryPrimitives.WriteUInt64LittleEndian(stackData.AsSpan(offset, wordSize), value);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(stackData.AsSpan(offset, wordSize), value);
        }

        // Write vector slots directly into the final stack buffer.
        for (var i = argsRaw.Length - 1; i >= 0; i--)
        {
            var ptr = PushCStringBytes(argsRaw[i]);
            WriteWord((i + 1) * wordSize, ptr);
        }
        var envpOffset = (argsRaw.Length + 2) * wordSize;
        for (var i = envsRaw.Length - 1; i >= 0; i--)
        {
            var ptr = PushCStringBytes(envsRaw[i]);
            WriteWord(envpOffset + i * wordSize, ptr);
        }

        var platPtr = PushCStringBytes(platform);
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
            LinuxConstants.AT_HWCAP, isAArch64 ? 0u : 0x0fcbfbfd,
            LinuxConstants.AT_HWCAP2, 0,
            LinuxConstants.AT_CLKTCK, 100,
            LinuxConstants.AT_NULL, 0
        };

        if (auxv.Length != AuxEntryCount * 2)
            throw new InvalidOperationException($"Unexpected auxv size: {auxv.Length}.");

        var auxvOffset = totalSize - auxv.Length * wordSize;
        for (var i = 0; i < auxv.Length; i++)
            WriteWord(auxvOffset + i * wordSize, auxv[i]);

        // Vector terminators are already zero in the freshly allocated buffer.
        WriteWord(0, (uint)argsRaw.Length);

        var stackMapLen = layout.InitialStackTop - stackMapStart;
        ProcessAddressSpaceSync.Mmap(mm, engine, stackMapStart, stackMapLen, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous | MapFlags.GrowDown | MapFlags.Stack,
            null, 0, "STACK");

        var stackVma = mm.FindVmArea(finalSp);
        if (stackVma == null)
            throw new InvalidOperationException("Initial stack VMA missing after mmap.");

        stackVma.GrowDownLimit = layout.StackLowerBound;
        stackVma.GrowDownGuardGap = layout.StackGuardGap;
        stackVma.VmPgoff = (stackVma.Start - layout.StackLowerBound) / LinuxConstants.PageSize;

        var res = new LoaderResult
        {
            Entry = mainInfo.InterpPathRaw != null ? interpEntry : mainInfo.EntryPoint,
            SP = finalSp,
            InitialStack = new ArraySegment<byte>(stackData),
            BrkAddr = mainInfo.BrkAddr,
            Layout = layout
        };
        if (Logger.IsEnabled(LogLevel.Information))
            Logger.LogInformation(
                "ElfLoader Entry=0x{Entry:x} SP=0x{SP:x} Brk=0x{Brk:x} InitialStackLen={StackLen} InterpBase=0x{InterpBase:x}",
                res.Entry, res.SP, res.BrkAddr, res.InitialStack.Count, interpBase);
        return res;
    }

    private static int InterpreterPathSize(ElfSegment segment)
    {
        if (segment.Size < 2 || segment.Size > LinuxConstants.PathMax)
            throw new InvalidDataException("Invalid PT_INTERP path length.");
        return (int)segment.Size;
    }

    private static byte[] ParseInterpreterPath(ReadOnlySpan<byte> buffer)
    {
        if (buffer[^1] != 0)
            throw new InvalidDataException("PT_INTERP path is not NUL terminated.");
        return buffer[..buffer.IndexOf((byte)0)].ToArray();
    }

    private static byte[] ReadInterpreterPath(Stream stream, ElfSegment segment)
    {
        Span<byte> buffer = stackalloc byte[InterpreterPathSize(segment)];
        stream.Position = InterpreterPathOffset(segment, stream.Length, buffer.Length);
        stream.ReadExactly(buffer);
        return ParseInterpreterPath(buffer);
    }

    private static long InterpreterPathOffset(ElfSegment segment, long fileLength, int pathSize)
    {
        if (fileLength < pathSize || segment.Position > (ulong)(fileLength - pathSize))
            throw new InvalidDataException("PT_INTERP extends past the end of the file.");
        return (long)segment.Position;
    }

    private static uint GetLoadBase(ElfFile elf, uint hint)
    {
        if (elf.FileType == ElfFileType.Executable) return 0;
        if (elf.Architecture == GuestArchitecture.I386) return hint;

        ulong alignment = LinuxConstants.PageSize;
        foreach (var segment in elf.Segments)
            if (segment.Type == ElfSegmentType.Load && BitOperations.IsPow2(segment.Alignment))
                alignment = Math.Max(alignment, segment.Alignment);

        var loadBase = hint & ~(alignment - 1);
        if (loadBase == 0)
            throw new FatalExecException("ELF load alignment exceeds the guest address space.");
        return (uint)loadBase;
    }

    private static ElfLoadInfo LoadSegments(ElfFile elf, uint loadBase, VMAManager mm, Engine engine,
        byte[] fileDescRaw, LinuxFile sharedFile, Stream? elfStream)
    {
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

        var taskSize = mm.Layout?.TaskSize ?? LinuxConstants.TaskSize32;
        if (loadBase >= taskSize || elf.EntryPointAddress >= taskSize - loadBase)
            throw new FatalExecException("ELF entry is outside the guest address space.");

        var info = new ElfLoadInfo
        {
            PhEnt = elf.ProgramHeaderEntrySize,
            PhNum = (uint)elf.Segments.Length,
            EntryPoint = (uint)elf.EntryPointAddress + loadBase
        };

        uint? firstLoadVaddr = null;
        uint? phdrInLoad = null;
        var phdrSize = (ulong)info.PhEnt * info.PhNum;

        foreach (var segment in elf.Segments)
        {
            if (segment.Type == ElfSegmentType.Load)
            {
                if (segment.Size > segment.SizeInMemory ||
                    segment.VirtualAddress >= taskSize - loadBase ||
                    segment.SizeInMemory > taskSize - loadBase - segment.VirtualAddress ||
                    segment.Position > long.MaxValue ||
                    segment.Size > long.MaxValue - segment.Position ||
                    ((segment.VirtualAddress ^ segment.Position) & LinuxConstants.PageOffsetMask) != 0)
                    throw new FatalExecException("Invalid ELF PT_LOAD after Linux's point of no return.");

                var vaddrRaw = (uint)segment.VirtualAddress + loadBase;
                firstLoadVaddr ??= vaddrRaw;
                if (segment.Position <= elf.ProgramHeaderOffset && phdrSize <= segment.Size)
                {
                    var offsetInSegment = elf.ProgramHeaderOffset - segment.Position;
                    if (offsetInSegment <= segment.Size - phdrSize)
                        phdrInLoad = vaddrRaw + (uint)offsetInSegment;
                }

                var perms = Protection.None;
                if ((segment.Flags & ElfSegmentFlags.Executable) != 0) perms |= Protection.Exec;
                if ((segment.Flags & ElfSegmentFlags.Writable) != 0) perms |= Protection.Write;
                if ((segment.Flags & ElfSegmentFlags.Readable) != 0) perms |= Protection.Read;

                var sizeRaw = (uint)segment.SizeInMemory;

                var pageStart = vaddrRaw & LinuxConstants.PageMask;
                var pageOffset = (long)(segment.Position & ~(ulong)LinuxConstants.PageOffsetMask);
                if (sizeRaw > 0)
                {
                    var bssStart = vaddrRaw + (uint)segment.Size;
                    var bssEnd = vaddrRaw + sizeRaw;

                    var fileMapEnd = segment.Size == 0
                        ? pageStart
                        : (bssStart + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
                    var fileMapLen = fileMapEnd - pageStart;

                    if (fileMapLen > 0)
                    {
                        MmapSharedFile(mm, engine, pageStart, fileMapLen, perms, sharedFile, pageOffset);

                        if (bssStart < fileMapEnd)
                            mm.FindVmArea(pageStart)?.SetSyntheticZeroRange(bssStart, fileMapEnd);
                    }

                    // The tail bytes of the last file-backed page are provided by
                    // the synthetic zero range above: the host page here is the
                    // shared file-cache page and the segment mapping is often
                    // read-only, so writing zeros through it would either fault
                    // (read-only writes are rejected) or corrupt the file cache.
                    // Guest reads and CoW copies apply the zero fill instead.

                    if (bssEnd > fileMapEnd)
                    {
                        var anonLen = (bssEnd - fileMapEnd + LinuxConstants.PageOffsetMask) &
                                      LinuxConstants.PageMask;
                        ProcessAddressSpaceSync.Mmap(mm, engine, fileMapEnd, anonLen, perms,
                            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "ELF_BSS");
                    }
                }

                var end = vaddrRaw + sizeRaw;
                end = (end + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
                if (end > info.BrkAddr) info.BrkAddr = end;
            }

            if (segment.Type == ElfSegmentType.ProgramHeader)
            {
                if (segment.VirtualAddress >= taskSize - loadBase ||
                    phdrSize > taskSize - loadBase - segment.VirtualAddress)
                    throw new FatalExecException("ELF PT_PHDR is outside the guest address space.");
                info.PhdrAddr = (uint)segment.VirtualAddress + loadBase;
            }

            if (segment.Type == ElfSegmentType.Interpreter && elfStream != null)
                info.InterpPathRaw = ReadInterpreterPath(elfStream, segment);
        }

        if (info.PhdrAddr == 0)
            info.PhdrAddr = phdrInLoad ?? (elf.Architecture == GuestArchitecture.I386 && firstLoadVaddr.HasValue
                ? firstLoadVaddr.Value + elf.ElfHeaderSize
                : 0u);

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
        public byte[]? InterpPathRaw { get; set; }
    }
}
