using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Loader;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.Tests.Core;
using Fiberish.VFS;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Loader;

public class ElfLoaderRegressionTests
{
    private const string GuestElfPath = "/tail-bss.elf";
    private const uint SegmentVirtualAddress = 0x08048000;
    private const uint SegmentFileOffset = 0x1000;
    private const uint SegmentFileSize = 0x120;
    private const uint SegmentMemorySize = 0x200;

    [Fact]
    public void PtLoadTailZeroFill_RemainsZero_AfterNativeTearDown_AndRefault()
    {
        using var runtime = KernelRuntime.BootstrapBare(false);
        var tmpfsType = new FileSystemType
        {
            Name = "tmpfs",
            Factory = static _ => new Tmpfs(),
            FactoryWithContext = static (_, memoryContext) => new Tmpfs(memoryContext: memoryContext)
        };
        var rootSb = tmpfsType.CreateAnonymousFileSystem(runtime.MemoryContext).ReadSuper(tmpfsType, 0,
            "elf-tail-bss-root", null);
        runtime.Syscalls.MountRoot(rootSb, new SyscallManager.RootMountOptions
        {
            Source = "tmpfs",
            FsType = "tmpfs",
            Options = "rw"
        });
        runtime.Syscalls.WriteFileInDetachedMount(runtime.Syscalls.RootMount!, "tail-bss.elf", BuildTailBssElfImage(),
            0x1ED);

        var scheduler = new KernelScheduler();
        var (loc, resolvedGuestPath) = runtime.Syscalls.ResolvePath(GuestElfPath, true);
        Assert.True(loc.IsValid);
        Assert.NotNull(loc.Dentry);
        Assert.NotNull(loc.Mount);

        var task = ProcessFactory.CreateInitProcess(
            runtime,
            loc.Dentry!,
            resolvedGuestPath,
            [resolvedGuestPath],
            [],
            scheduler,
            null,
            loc.Mount);

        var mm = task.Process.Mem;
        var bssStart = SegmentVirtualAddress + SegmentFileSize;
        var bssBytes = new byte[checked((int)(SegmentMemorySize - SegmentFileSize))];

        Assert.True(runtime.Engine.CopyFromUser(bssStart, bssBytes));
        Assert.All(bssBytes, static value => Assert.Equal((byte)0, value));

        mm.TearDownNativeMappings(
            runtime.Engine,
            SegmentVirtualAddress,
            LinuxConstants.PageSize,
            false,
            false,
            true);

        Assert.True(mm.HandleFault(bssStart, false, runtime.Engine));

        Array.Fill(bssBytes, (byte)0x5A);
        Assert.True(runtime.Engine.CopyFromUser(bssStart, bssBytes));
        Assert.All(bssBytes, static value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void PtLoadPagePadding_AfterSegmentMemsz_IsAlsoZeroed()
    {
        using var runtime = KernelRuntime.BootstrapBare(false);
        var tmpfsType = new FileSystemType
        {
            Name = "tmpfs",
            Factory = static _ => new Tmpfs(),
            FactoryWithContext = static (_, memoryContext) => new Tmpfs(memoryContext: memoryContext)
        };
        var rootSb = tmpfsType.CreateAnonymousFileSystem(runtime.MemoryContext).ReadSuper(tmpfsType, 0,
            "elf-tail-padding-root", null);
        runtime.Syscalls.MountRoot(rootSb, new SyscallManager.RootMountOptions
        {
            Source = "tmpfs",
            FsType = "tmpfs",
            Options = "rw"
        });
        runtime.Syscalls.WriteFileInDetachedMount(runtime.Syscalls.RootMount!, "tail-bss.elf", BuildTailBssElfImage(),
            0x1ED);

        var scheduler = new KernelScheduler();
        var (loc, resolvedGuestPath) = runtime.Syscalls.ResolvePath(GuestElfPath, true);
        Assert.True(loc.IsValid);
        Assert.NotNull(loc.Dentry);
        Assert.NotNull(loc.Mount);

        _ = ProcessFactory.CreateInitProcess(
            runtime,
            loc.Dentry!,
            resolvedGuestPath,
            [resolvedGuestPath],
            [],
            scheduler,
            null,
            loc.Mount);

        var paddingStart = SegmentVirtualAddress + SegmentMemorySize;
        var paddingBytes = new byte[64];

        Assert.True(runtime.Engine.CopyFromUser(paddingStart, paddingBytes));
        Assert.All(paddingBytes, static value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void ProcessEntryStackPointer_Remains16ByteAligned_WithVariableLengthArgvAndEnvp()
    {
        using var harness = ElfTestHarness.LoadLinuxTestAsset(
            "hello_static",
            args: ["/hello_static", "abc", "long-argument"],
            envs: ["A=1", "LONG_ENVIRONMENT_NAME=xyz"]);

        Assert.Equal(0u, harness.Task.CPU.RegRead(Reg.ESP) & 0xFu);
    }

    [Fact]
    public void ExecutableInode_DoesNotKeepAnOpenFileReference_AfterLoad()
    {
        using var harness = ElfTestHarness.LoadLinuxTestAsset("hello_static");
        using var stream = File.OpenRead(Path.Combine(ElfTestHarness.ResolveLinuxGuestRoot(), "hello_static"));

        var elf = Elf32Reader.Read(stream);
        var hasLoadSegment = false;
        foreach (var segment in elf.Segments)
            if (segment.Type == ElfSegmentType.Load)
            {
                hasLoadSegment = true;
                break;
            }

        var (loc, _) = harness.Runtime.Syscalls.ResolvePath("/hello_static", true);
        Assert.True(loc.IsValid);
        Assert.NotNull(loc.Dentry);
        Assert.NotNull(loc.Dentry!.Inode);
        Assert.Equal(hasLoadSegment ? 1 : 0, loc.Dentry.Inode!.FileOpenRefCount);
    }

    private static byte[] BuildTailBssElfImage()
    {
        var image = new byte[checked((int)(SegmentFileOffset + LinuxConstants.PageSize))];
        var span = image.AsSpan();

        span[0] = 0x7F;
        span[1] = (byte)'E';
        span[2] = (byte)'L';
        span[3] = (byte)'F';
        span[4] = 1;
        span[5] = 1;
        span[6] = 1;

        BinaryPrimitives.WriteUInt16LittleEndian(span[16..18], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..20], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..24], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..28], SegmentVirtualAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..32], 0x34);
        BinaryPrimitives.WriteUInt16LittleEndian(span[40..42], 52);
        BinaryPrimitives.WriteUInt16LittleEndian(span[42..44], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(span[44..46], 1);

        var programHeader = span[0x34..0x54];
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader[..4], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader[4..8], SegmentFileOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader[8..12], SegmentVirtualAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader[16..20], SegmentFileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader[20..24], SegmentMemorySize);
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader[24..28], 5);
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader[28..32], LinuxConstants.PageSize);

        span.Slice((int)SegmentFileOffset, (int)SegmentFileSize).Fill(0x90);
        span.Slice((int)(SegmentFileOffset + SegmentFileSize),
            LinuxConstants.PageSize - (int)SegmentFileSize).Fill(0xCC);

        return image;
    }
}