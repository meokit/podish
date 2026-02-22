using System.Buffers.Binary;
using System.Text;
using Fiberish.Diagnostics;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
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

    public static LoaderResult Load(string filename, SyscallManager sys, string[] args, string[] envs)
    {
        var mm = sys.Mem;
        var engine = sys.Engine;

        // Try to find the file in VFS to get a Dentry for mmap
        // If filename is absolute on host, try to make it relative to rootfs if possible
        var vfsLookupPath = filename;
        string? hostRoot = null;
        HostSuperBlock? hsb = null;

        if (sys.Root.SuperBlock is HostSuperBlock h)
        {
            hsb = h;
            hostRoot = h.HostRoot;
        }
        else if (sys.Root.SuperBlock is OverlaySuperBlock osb && osb.LowerSB is HostSuperBlock lh)
        {
            hsb = lh;
            hostRoot = lh.HostRoot;
        }

        if (hostRoot != null)
        {
            hostRoot = Path.GetFullPath(hostRoot).TrimEnd(Path.DirectorySeparatorChar);
            var absFilename = Path.GetFullPath(filename);

            if (absFilename.StartsWith(hostRoot, StringComparison.OrdinalIgnoreCase))
            {
                vfsLookupPath = absFilename[hostRoot.Length..];
                if (string.IsNullOrEmpty(vfsLookupPath)) vfsLookupPath = "/";
                else if (vfsLookupPath[0] != Path.DirectorySeparatorChar && vfsLookupPath[0] != '/')
                    vfsLookupPath = "/" + vfsLookupPath;
                vfsLookupPath = vfsLookupPath.Replace(Path.DirectorySeparatorChar, '/');
            }
        }

        var dentry = sys.PathWalk(vfsLookupPath);

        // If PathWalk failed (e.g. file is outside rootfs), try to get a Dentry directly from Hostfs if applicable
        if (dentry == null && hsb != null)
            try
            {
                var absPath = Path.GetFullPath(filename);
                if (File.Exists(absPath)) dentry = hsb.GetDentry(absPath, Path.GetFileName(absPath), null);
            }
            catch
            {
                /* ignore */
            }

        // Resolve the host path for reading the ELF file
        var hostPath = filename;

        // If we found a dentry with a HostInode, use its HostPath directly
        if (dentry?.Inode is HostInode hi)
            hostPath = hi.HostPath;
        else if (dentry?.Inode is OverlayInode oi && oi.UpperInode == null && oi.LowerInode is HostInode lhi)
            hostPath = lhi.HostPath;
        else if (hostRoot != null && filename.StartsWith("/") && !Path.IsPathRooted(filename))
            // Only combine if it's a guest absolute path AND not a host absolute path
            // Note: On Linux/Mac, Path.IsPathRooted("/") is true. 
            // We need a better way to distinguish.
            hostPath = Path.Combine(hostRoot, filename.TrimStart('/'));
        else if (hostRoot != null && filename.StartsWith("/") && !filename.StartsWith(hostRoot))
            // If it's a guest path (starts with /) but not already pointing into hostRoot
            hostPath = Path.Combine(hostRoot, filename.TrimStart('/'));

        // Still use Host IO for ElfFile reader as it needs a Stream
        using var stream = File.OpenRead(hostPath);
        var elf = ElfFile.Read(stream);

        uint loadBase = 0;
        if (elf.FileType == ElfFileType.Dynamic) loadBase = 0x40000000; // PIE base
        Logger.LogDebug("ElfLoader: {Filename} FileType={Type}, selected loadBase=0x{LoadBase:x}", filename, elf.FileType, loadBase);

        uint phnum = 0;
        uint phdrAddr = 0;
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
                        throw new FileNotFoundException($"Could not find file in VFS or Host for mapping: {filename}");

                    var fileSzLimit = diff + (long)segment.Size;
                    var vfsFile = new LinuxFile(dentry, FileFlags.O_RDONLY);
                    mm.Mmap(pageStart, alignedLen, perms, MapFlags.Private | MapFlags.Fixed, vfsFile, pageOffset,
                        fileSzLimit, "ELF_LOAD", engine);
                }
            }

            if (segment.Type == ElfSegmentTypeCore.ProgramHeader) phdrAddr = (uint)segment.VirtualAddress + loadBase;
            phnum++;
        }

        if (phdrAddr == 0 && foundFirstLoad) phdrAddr = firstLoadVaddr + loadBase + elf.Layout.SizeOfElfHeader;
        Logger.LogDebug("ElfLoader: loadBase=0x{LoadBase:x} phdrAddr=0x{PhdrAddr:x} phnum={Phnum}", loadBase, phdrAddr,
            phnum);

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
        var execFnPtr = PushString(filename);
        var randPtr = PushBytes(new byte[16]);

        sp &= ~0xFu;

        void PushAux(uint k, uint v)
        {
            PushUint32(v);
            PushUint32(k);
        }

        PushAux(LinuxConstants.AT_NULL, 0);
        PushAux(LinuxConstants.AT_PLATFORM, platPtr);
        PushAux(LinuxConstants.AT_EXECFN, execFnPtr);
        PushAux(LinuxConstants.AT_RANDOM, randPtr);
        PushAux(LinuxConstants.AT_UID, 1000);
        PushAux(LinuxConstants.AT_EUID, 1000);
        PushAux(LinuxConstants.AT_GID, 1000);
        PushAux(LinuxConstants.AT_EGID, 1000);
        PushAux(LinuxConstants.AT_PHNUM, (uint)elf.Segments.Count);
        PushAux(LinuxConstants.AT_PHENT, elf.Layout.SizeOfProgramHeaderEntry);
        PushAux(LinuxConstants.AT_PHDR, phdrAddr);
        PushAux(LinuxConstants.AT_PAGESZ, LinuxConstants.PageSize);
        PushAux(LinuxConstants.AT_ENTRY, (uint)elf.EntryPointAddress + loadBase);
        PushAux(LinuxConstants.AT_BASE, 0); // For static-pie/PIE with no interpreter, this should be 0
        PushAux(LinuxConstants.AT_FLAGS, 0);
        PushAux(LinuxConstants.AT_HWCAP, 0);
        PushAux(LinuxConstants.AT_CLKTCK, 100);

        PushUint32(0);
        for (var i = envPtrs.Length - 1; i >= 0; i--) PushUint32(envPtrs[i]);

        PushUint32(0);
        for (var i = argPtrs.Length - 1; i >= 0; i--) PushUint32(argPtrs[i]);

        PushUint32((uint)args.Length);

        uint brkAddr = 0;
        foreach (var segment in elf.Segments)
            if (segment.Type == ElfSegmentTypeCore.Load)
            {
                var end = (uint)segment.VirtualAddress + (uint)segment.SizeInMemory + loadBase;
                end = (end + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
                if (end > brkAddr) brkAddr = end;
            }

        sys.Engine.FlushCache();

        var res = new LoaderResult
        {
            Entry = (uint)elf.EntryPointAddress + loadBase,
            SP = sp,
            InitialStack = stackData.AsSpan((int)(sp - stackStart)).ToArray(),
            BrkAddr = brkAddr
        };
        Logger.LogInformation("ElfLoader Entry=0x{Entry:x} SP=0x{SP:x} Brk=0x{Brk:x} InitialStackLen={StackLen}",
            res.Entry, res.SP, res.BrkAddr, res.InitialStack.Length);
        return res;
    }
}