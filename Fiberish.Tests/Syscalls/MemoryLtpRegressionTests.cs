using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public sealed partial class LtpRegressionTests
{
    private const uint MemoryBase = 0x30000000;
    private const uint MemoryTarget = 0x31000000;
    private const uint Page = LinuxConstants.PageSize;

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(1u, 1u)]
    [InlineData(2u, 1u)]
    [InlineData(3u, 1u)]
    [InlineData(0u, 2u)]
    [InlineData(1u, 2u)]
    [InlineData(2u, 2u)]
    [InlineData(3u, 2u)]
    public async Task Mmap_WriteOnlyDescriptorAlwaysReturnsEacces(uint prot, uint flags)
    {
        using var env = new TestEnv();
        await env.CreateFile();
        var fd = await env.Call("SysOpen", 0x10000, (uint)FileFlags.O_WRONLY);
        Assert.Equal(-(int)Errno.EACCES,
            await env.Call("SysMmap2", 0, Page, prot, flags, (uint)fd));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4096u)]
    public async Task Mmap_InvalidFdPrecedesLengthValidation(uint length)
    {
        using var env = new TestEnv();
        var fd = await env.CreateFile();
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
        Assert.Equal(-(int)Errno.EBADF,
            await env.Call("SysMmap2", 0, length, 1, 1, (uint)fd));
    }

    [Fact]
    public async Task Mmap_SharedValidateRejectsUnknownFlags_ButLegacySharedIgnoresThem()
    {
        using var env = new TestEnv();
        var fd = (uint)await env.CreateFile();
        Assert.Equal(-(int)Errno.EOPNOTSUPP,
            await env.Call("SysMmap2", 0, Page, 1, 3 | 0x400, fd));
        var address = (uint)await env.Call("SysMmap2", 0, Page, 1, 1 | 0x400, fd);
        Assert.True(address < 0xFFFFF000u);
        address = (uint)await env.Call("SysMmap2", 0, Page, 1, 3, fd);
        Assert.True(address < 0xFFFFF000u);
        Assert.NotEqual((MapFlags)0, env.Vma.FindVmArea(address)!.Flags & MapFlags.Shared);
        Assert.Equal((MapFlags)0, env.Vma.FindVmArea(address)!.Flags & MapFlags.Private);
    }

    [Fact]
    public async Task Mmap_NonFixedHintIsRoundedDown()
    {
        using var env = new TestEnv();
        Assert.Equal(MemoryBase, (uint)await env.Call("SysMmap2", MemoryBase + 123, Page, 3, 0x22));
        Assert.Equal(-(int)Errno.EINVAL,
            await env.Call("SysMmap2", MemoryTarget + 123, Page, 3, 0x32));
    }

    [Theory]
    [InlineData(uint.MaxValue)]
    [InlineData(0xFFFFFFFEu)]
    public async Task Munmap_OverflowingLengthFailsWithoutUnmapping(uint length)
    {
        using var env = new TestEnv();
        MapAnonymous(env, MemoryBase, Page);
        Assert.Equal(-(int)Errno.EINVAL, await env.Call("SysMunmap", MemoryBase, length));
        Assert.NotNull(env.Vma.FindVmArea(MemoryBase));
    }

    [Theory]
    [InlineData(1u, 0u, -(int)Errno.EINVAL)]
    [InlineData(1u, 4u, -(int)Errno.EINVAL)]
    [InlineData(0u, 5u, -(int)Errno.EINVAL)]
    [InlineData(0u, 8u, -(int)Errno.EINVAL)]
    [InlineData(0u, uint.MaxValue, -(int)Errno.EINVAL)]
    public async Task Msync_RejectsBadAlignmentAndFlags(uint offset, uint flags, int expected)
    {
        using var env = new TestEnv();
        MapAnonymous(env, MemoryBase, Page);
        Assert.Equal(expected, await env.Call("SysMsync", MemoryBase + offset, Page, flags));
    }

    [Fact]
    public async Task Msync_UnmappedHoleReturnsEnomem_AndZeroLengthSucceeds()
    {
        using var env = new TestEnv();
        MapAnonymous(env, MemoryBase, Page);
        MapAnonymous(env, MemoryBase + 2 * Page, Page);
        Assert.Equal(-(int)Errno.ENOMEM, await env.Call("SysMsync", MemoryBase, 3 * Page, 4));
        Assert.Equal(0, await env.Call("SysMsync", MemoryTarget, 0, 4));
        Assert.Equal(0, await env.Call("SysMsync", 0xFFFFF000u, 0, 4));
    }

    [Theory]
    [InlineData(0u, -(int)Errno.EFAULT)]
    [InlineData(0xDEAD0000u, -(int)Errno.EFAULT)]
    public async Task Memfd_NameMustBeReadable(uint name, int expected)
    {
        using var env = new TestEnv();
        Assert.Equal(expected, await env.Call("SysMemfdCreate", name));
    }

    [Theory]
    [InlineData(249, 0)]
    [InlineData(250, -(int)Errno.EINVAL)]
    [InlineData(2047, -(int)Errno.EINVAL)]
    public async Task Memfd_NameLimitIs249Bytes(int length, int expected)
    {
        using var env = new TestEnv();
        env.WriteString(0x10000, new string('a', length));
        var rc = await env.Call("SysMemfdCreate", 0x10000);
        if (expected == 0) Assert.True(rc >= 0);
        else Assert.Equal(expected, rc);
    }

    [Fact]
    public async Task Memfd_NameIsByteLimited_AndTerminatorAtPageBoundaryDoesNotOverread()
    {
        using var env = new TestEnv();
        env.WriteString(0x10000, new string('é', 125)); // 250 UTF-8 bytes
        Assert.Equal(-(int)Errno.EINVAL, await env.Call("SysMemfdCreate", 0x10000));
        env.WriteString(0x15FFE, "x");
        Assert.True(await env.Call("SysMemfdCreate", 0x15FFE) >= 0);
    }

    [Fact]
    public async Task ProcSelfMounts_UsesActualNamespaceAndIsEnumerated()
    {
        using var env = new TestEnv();
        env.Syscalls.MountStandardProc();
        var dir = env.Syscalls.PathWalk($"/proc/{env.Process.TGID}").Dentry!;
        Assert.Contains(((IContextualDirectoryInode)dir.Inode!).GetEntries(env.Task), entry => entry.Name == "mounts");
        env.WriteString(0x10000, "/proc/self/mounts");
        var fd = (uint)await env.Call("SysOpen", 0x10000);
        var count = await env.Call("SysRead", fd, 0x12000, Page);
        Assert.True(count > 0);
        Assert.Equal(Fiberish.Syscalls.ProcFsManager.GenerateMounts(env.Syscalls),
            System.Text.Encoding.UTF8.GetString(env.Read(0x12000, count)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemoryFault_ProtNoneMustNotBeReportedHandled(bool shared)
    {
        using var env = new TestEnv();
        env.Vma.Mmap(MemoryBase, Page, Protection.None,
            (shared ? MapFlags.Shared : MapFlags.Private) | MapFlags.Anonymous | MapFlags.Fixed,
            null, 0, "[no-access]", env.Engine);
        Assert.Equal(FaultResult.Segv, env.Vma.HandleFaultDetailed(MemoryBase, false, env.Engine));
        Assert.Equal(FaultResult.Segv, env.Vma.HandleFaultDetailed(MemoryBase, true, env.Engine));
    }

    [Theory]
    [InlineData(4096u)]
    [InlineData(8192u)]
    public async Task Mremap_FixedMustMoveEvenWhenNotGrowing(uint newLength)
    {
        using var env = new TestEnv();
        MapAnonymous(env, MemoryBase, 2 * Page);
        MapAnonymous(env, MemoryTarget, 2 * Page);
        Assert.True(env.Engine.CopyToUser(MemoryBase, new byte[] { 0xA5 }));
        Assert.Equal(MemoryTarget,
            (uint)await env.Call("SysMremap", MemoryBase, 2 * Page, newLength, 3, MemoryTarget));
        Assert.Null(env.Vma.FindVmArea(MemoryBase));
        Assert.Equal((byte)0xA5, env.Read(MemoryTarget, 1)[0]);
    }

    [Fact]
    public async Task Mremap_OverlappingFixedRangesFailBeforeAnyUnmapping()
    {
        using var env = new TestEnv();
        MapAnonymous(env, MemoryBase, 3 * Page);
        Assert.Equal(-(int)Errno.EINVAL,
            await env.Call("SysMremap", MemoryBase, 2 * Page, 2 * Page, 3, MemoryBase + Page));
        Assert.Equal(MemoryBase + 3 * Page, env.Vma.FindVmArea(MemoryBase)!.End);
    }

    [Fact]
    public async Task Mremap_RejectsFixedNullTargetAndPrivateZeroSizeClone()
    {
        using var env = new TestEnv();
        MapAnonymous(env, MemoryBase, Page);
        Assert.Equal(-(int)Errno.EPERM, await env.Call("SysMremap", MemoryBase, Page, Page, 3, 0));
        Assert.Equal(-(int)Errno.EINVAL, await env.Call("SysMremap", MemoryBase, 0, Page, 1));
        Assert.NotNull(env.Vma.FindVmArea(MemoryBase));
    }

    [Fact]
    public async Task Mprotect_SharedReadOnlyFileCannotGainWritePermission()
    {
        using var env = new TestEnv();
        var fd = (uint)await env.CreateFile();
        Assert.Equal(0, await env.Call("SysFtruncate", fd, Page));
        fd = (uint)await env.Call("SysOpen", 0x10000, (uint)FileFlags.O_RDONLY);
        Assert.Equal(MemoryBase, (uint)await env.Call("SysMmap2", MemoryBase, Page, 1, 0x11, fd));
        Assert.Equal(-(int)Errno.EACCES, await env.Call("SysMprotect", MemoryBase, Page, 3));
        Assert.Equal(Protection.Read, env.Vma.FindVmArea(MemoryBase)!.Perms);
    }

    [Fact]
    public async Task Munmap_SplittingMiddleAtMaxMapCountFailsWithEnomem()
    {
        using var env = new TestEnv();
        var initialVmas = env.Vma.VMAs.Count;
        env.Syscalls.MemoryContext.MaxMapCount = initialVmas + 9;
        for (uint i = 0; i < 9; i++)
        {
            var addr = 0x10000000u + i * 3 * Page;
            Assert.Equal(addr, (uint)await env.Call("SysMmap2", addr, 3 * Page, 0, 0x32, uint.MaxValue));
        }

        // At max_map_count: unmapping middle page splits 1 VMA into 2, exceeding limit
        Assert.Equal(-(int)Errno.ENOMEM, await env.Call("SysMunmap", 0x10000000u + Page, Page));

        // Full removal does not split and succeeds
        Assert.Equal(0, await env.Call("SysMunmap", 0x10000000u, 3 * Page));

        // Now we are at initialVmas + 8: middle split is allowed
        Assert.Equal(0, await env.Call("SysMunmap", 0x10000000u + 3 * Page + Page, Page));
    }

    [Fact]
    public async Task RemapFilePages_ReordersSharedFileOffset()
    {
        using var env = new TestEnv();
        var fd = (uint)await env.CreateFile();
        Assert.Equal(0, await env.Call("SysFtruncate", fd, 3 * Page));
        Assert.True(env.Engine.CopyToUser(0x10000, new byte[] { 0x11, 0x22, 0x33 }));
        Assert.Equal(1, await env.Call("SysPWrite", fd, 0x10000, 1, 0));
        env.Engine.CopyToUser(0x10000, new byte[] { 0x44 });
        Assert.Equal(1, await env.Call("SysPWrite", fd, 0x10000, 1, Page));
        env.Engine.CopyToUser(0x10000, new byte[] { 0x77 });
        Assert.Equal(1, await env.Call("SysPWrite", fd, 0x10000, 1, 2 * Page));

        var mapped = (uint)await env.Call("SysMmap2", MemoryBase, 3 * Page, 3, 1, fd);
        Assert.Equal(MemoryBase, mapped);

        // Access page 0 -> should read 0x11
        Assert.Equal((byte)0x11, env.Read(MemoryBase, 1)[0]);
        // Access page 1 -> should read 0x44
        Assert.Equal((byte)0x44, env.Read(MemoryBase + Page, 1)[0]);

        // Remap page 0 to file pgoff 2 (0x77)
        Assert.Equal(0, await env.Call("SysRemapFilePages", MemoryBase, Page, 0, 2, 0));
        Assert.Equal((byte)0x77, env.Read(MemoryBase, 1)[0]);

        // Remap page 1 to file pgoff 0 (0x11)
        Assert.Equal(0, await env.Call("SysRemapFilePages", MemoryBase + Page, Page, 0, 0, 0));
        Assert.Equal((byte)0x11, env.Read(MemoryBase + Page, 1)[0]);
    }

    [Fact]
    public async Task Mremap_ProtNoneRangeMovesWithoutReadingOrEfault()
    {
        using var env = new TestEnv();
        var mapped = (uint)await env.Call("SysMmap2", MemoryBase, Page, 0, 0x32, uint.MaxValue);
        Assert.Equal(MemoryBase, mapped);

        // Moving PROT_NONE mapping with MREMAP_FIXED | MREMAP_MAYMOVE must not fail with EFAULT
        var remapped = (uint)await env.Call("SysMremap", MemoryBase, Page, Page, 3, MemoryTarget);
        Assert.Equal(MemoryTarget, remapped);
        Assert.Null(env.Vma.FindVmArea(MemoryBase));
        var targetVma = env.Vma.FindVmArea(remapped);
        Assert.NotNull(targetVma);
        Assert.Equal(Protection.None, targetVma!.Perms);
    }

    [Fact]
    public async Task Mremap_SparseLazyAnonymousMovesWithoutAllocatingUntouchedPages()
    {
        using var env = new TestEnv();
        const uint numPages = 64;
        var mapped = (uint)await env.Call("SysMmap2", MemoryBase, numPages * Page, 3, 0x32, uint.MaxValue);
        Assert.Equal(MemoryBase, mapped);

        // Touch only page 0 and page 63
        Assert.True(env.Engine.CopyToUser(MemoryBase, new byte[] { 0x55 }));
        Assert.True(env.Engine.CopyToUser(MemoryBase + 63 * Page, new byte[] { 0xAA }));

        var beforeCow = env.Syscalls.MemoryContext.GetAllocationClassStats().Single(s => s.Class == AllocationClass.Cow).AllocatedPages;
        var beforeAnon = env.Syscalls.MemoryContext.GetAllocationClassStats().Single(s => s.Class == AllocationClass.Anonymous).AllocatedPages;

        // Move the sparse 64-page mapping to MemoryTarget
        var remapped = (uint)await env.Call("SysMremap", MemoryBase, numPages * Page, numPages * Page, 3, MemoryTarget);
        Assert.Equal(MemoryTarget, remapped);

        // Verified touched pages have their data
        Assert.Equal((byte)0x55, env.Read(remapped, 1)[0]);
        Assert.Equal((byte)0xAA, env.Read(remapped + 63 * Page, 1)[0]);

        // Verified untouched pages did not cause 62 new page allocations
        var afterCow = env.Syscalls.MemoryContext.GetAllocationClassStats().Single(s => s.Class == AllocationClass.Cow).AllocatedPages;
        var afterAnon = env.Syscalls.MemoryContext.GetAllocationClassStats().Single(s => s.Class == AllocationClass.Anonymous).AllocatedPages;
        Assert.True((afterCow - beforeCow) + (afterAnon - beforeAnon) <= 4);
    }

    [Fact]
    public async Task Mremap_GrowingIntoOwnVmaTailWithoutMayMoveFails()
    {
        using var env = new TestEnv();
        MapAnonymous(env, MemoryBase, 2 * Page);
        Assert.Equal(-(int)Errno.ENOMEM, await env.Call("SysMremap", MemoryBase, Page, 2 * Page));
    }

    [Fact]
    public async Task Mremap_FileBackedMiddleSliceMovesWithCorrectOffset()
    {
        using var env = new TestEnv();
        var fd = (uint)await env.CreateFile();
        Assert.Equal(0, await env.Call("SysFtruncate", fd, 3 * Page));
        Assert.Equal(MemoryBase, (uint)await env.Call("SysMmap2", MemoryBase, 3 * Page, 3, 0x11, fd));
        Assert.True(env.Engine.CopyToUser(MemoryBase + Page, new byte[] { 0x5A }));
        Assert.Equal(MemoryTarget,
            (uint)await env.Call("SysMremap", MemoryBase + Page, Page, Page, 3, MemoryTarget));
        Assert.Equal((byte)0x5A, env.Read(MemoryTarget, 1)[0]);
        Assert.Null(env.Vma.FindVmArea(MemoryBase + Page));
        Assert.NotNull(env.Vma.FindVmArea(MemoryBase));
        Assert.NotNull(env.Vma.FindVmArea(MemoryBase + 2 * Page));
    }

    private static void MapAnonymous(TestEnv env, uint address, uint length)
    {
        env.Vma.Mmap(address, length, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed, null, 0, "[memory-ltp]", env.Engine);
    }

    [Fact]
    public async Task FilePastEof_QueuesOnlySigbusWhenCpuReportsSameFault()
    {
        using var env = new TestEnv();
        var fd = (uint)await env.CreateFile();
        Assert.Equal(MemoryBase, (uint)await env.Call("SysMmap2", MemoryBase, Page, 3, 0x11, fd));
        var memoryFault = typeof(FiberTask).GetMethod("HandlePageFault", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cpuFault = typeof(FiberTask).GetMethod("HandleCpuFault", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.True((bool)memoryFault.Invoke(env.Task, [MemoryBase, true])!);
        cpuFault.Invoke(env.Task, null);
        Assert.Equal(1UL << ((int)Signal.SIGBUS - 1), env.Task.PendingSignals);
        Assert.False(env.Task.Exited);
    }

    [Theory]
    [InlineData(100u)]
    [InlineData(101u)]
    public async Task Madvise_UnsupportedHardwarePoisonDoesNotPretendSuccess(uint advice)
    {
        using var env = new TestEnv();
        MapAnonymous(env, MemoryBase, Page);
        Assert.Equal(-(int)Errno.EINVAL, await env.Call("SysMadvise", MemoryBase, Page, advice));
    }

    [Theory]
    [InlineData("SysWrite")]
    [InlineData("SysPWrite")]
    [InlineData("SysWriteV")]
    [InlineData("SysPWriteV")]
    public async Task Memfd_WriteSealAppliesToMappedIoFastPath(string syscall)
    {
        using var env = new TestEnv();
        env.WriteString(0x10000, "sealed");
        var fd = (uint)await env.Call("SysMemfdCreate", 0x10000, 2);
        Assert.Equal(0, await env.Call("SysFtruncate", fd, Page));
        Assert.Equal(0, await env.Call("SysFcntl64", fd, 1033, 8));
        env.WriteString(0x12000, "test");
        env.WriteIovec(0x11000, 0x12000, 4);
        var pointer = syscall.EndsWith('V') ? 0x11000u : 0x12000u;
        var count = syscall.EndsWith('V') ? 1u : 4u;
        Assert.Equal(-(int)Errno.EPERM, await env.Call(syscall, fd, pointer, count));
        Assert.Equal(0, env.Syscalls.GetFD((int)fd)!.Position);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    public async Task Memfd_CannotAddWriteSealWhileSharedWritableCapableMappingExists(uint prot)
    {
        using var env = new TestEnv();
        env.WriteString(0x10000, "mapped");
        var fd = (uint)await env.Call("SysMemfdCreate", 0x10000, 2);
        Assert.Equal(0, await env.Call("SysFtruncate", fd, Page));
        Assert.Equal(MemoryBase, (uint)await env.Call("SysMmap2", MemoryBase, Page, prot, 0x11, fd));
        Assert.Equal(-(int)Errno.EBUSY, await env.Call("SysFcntl64", fd, 1033, 8));
        Assert.Equal(0, await env.Call("SysFcntl64", fd, 1034));
        Assert.Equal(0, await env.Call("SysMunmap", MemoryBase, Page));
        Assert.Equal(0, await env.Call("SysFcntl64", fd, 1033, 8));
        Assert.Equal(-(int)Errno.EPERM, await env.Call("SysMmap2", MemoryBase, Page, 3, 0x11, fd));
    }
}
