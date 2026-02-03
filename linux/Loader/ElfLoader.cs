using System.Buffers.Binary;
using System.Text;
using System.IO;
using LibObjectFile.Elf;
using Bifrost.Memory;
using Bifrost.Core;
using Bifrost.Syscalls;
using Bifrost.VFS;
using Bifrost.Native;

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

    // Auxv types moved to LinuxConstants

    public static LoaderResult Load(string filename, SyscallManager sys, string[] args, string[] envs)
    {
        var mm = sys.Mem;
        var engine = sys.Engine;

        // Try to find the file in VFS to get a Dentry for mmap
        // If filename is absolute on host, try to make it relative to rootfs if possible
        string vfsLookupPath = filename;
        string hostRoot = Path.GetFullPath(((HostSuperBlock)sys.Root.SuperBlock).HostRoot).TrimEnd(Path.DirectorySeparatorChar);
        string absFilename = Path.GetFullPath(filename);

        if (absFilename.StartsWith(hostRoot, StringComparison.OrdinalIgnoreCase))
        {
            vfsLookupPath = absFilename.Substring(hostRoot.Length);
            if (string.IsNullOrEmpty(vfsLookupPath)) vfsLookupPath = "/";
            else if (vfsLookupPath[0] != Path.DirectorySeparatorChar && vfsLookupPath[0] != '/') vfsLookupPath = "/" + vfsLookupPath;
            vfsLookupPath = vfsLookupPath.Replace(Path.DirectorySeparatorChar, '/');
        }

        var dentry = sys.PathWalk(vfsLookupPath);

        // If PathWalk failed (e.g. file is outside rootfs), try to get a Dentry directly from Hostfs if applicable
        if (dentry == null && sys.Root.SuperBlock is HostSuperBlock hsb)
        {
            try
            {
                string absPath = Path.GetFullPath(filename);
                if (System.IO.File.Exists(absPath))
                {
                    dentry = hsb.GetDentry(absPath, Path.GetFileName(absPath), null);
                }
            }
            catch { /* ignore */ }
        }

        // Still use Host IO for ElfFile reader as it needs a Stream
        using var stream = System.IO.File.OpenRead(filename);
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

                uint pageStart = vaddrRaw & LinuxConstants.PageMask;
                long pageOffset = offsetRaw & (long)LinuxConstants.PageMask;
                uint diff = vaddrRaw - pageStart;

                uint totalSize = sizeRaw + diff;
                uint alignedLen = (totalSize + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;

                if (sizeRaw > 0)
                {
                    if (dentry == null)
                    {
                        throw new FileNotFoundException($"Could not find file in VFS or Host for mapping: {filename}");
                    }

                    long fileSzLimit = diff + (long)segment.Size;
                    var vfsFile = new Bifrost.VFS.File(dentry, FileFlags.O_RDONLY);
                    mm.Mmap(pageStart, alignedLen, perms, MapFlags.Private | MapFlags.Fixed, vfsFile, pageOffset, fileSzLimit, "ELF_LOAD", engine);
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
        uint execFnPtr = PushString(filename);
        uint randPtr = PushBytes(new byte[16]);

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
        PushAux(LinuxConstants.AT_PHENT, (uint)elf.Layout.SizeOfProgramHeaderEntry);
        PushAux(LinuxConstants.AT_PHDR, phdrAddr);
        PushAux(LinuxConstants.AT_PAGESZ, (uint)LinuxConstants.PageSize);
        PushAux(LinuxConstants.AT_ENTRY, (uint)elf.EntryPointAddress + loadBase);
        PushAux(LinuxConstants.AT_BASE, 0); // For static-pie/PIE with no interpreter, this should be 0
        PushAux(LinuxConstants.AT_FLAGS, 0);
        PushAux(LinuxConstants.AT_HWCAP, 0);
        PushAux(LinuxConstants.AT_CLKTCK, 100);

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
                end = (end + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
                if (end > brkAddr) brkAddr = end;
            }
        }

        sys.Engine.FlushCache();

        var res = new LoaderResult
        {
            Entry = (uint)elf.EntryPointAddress + loadBase,
            SP = sp,
            InitialStack = stackData.AsSpan((int)(sp - stackStart)).ToArray(),
            BrkAddr = brkAddr
        };
        Console.WriteLine($"[ElfLoader] Entry=0x{res.Entry:x} SP=0x{res.SP:x} Brk=0x{res.BrkAddr:x} InitialStackLen={res.InitialStack.Length}");
        return res;
    }
}