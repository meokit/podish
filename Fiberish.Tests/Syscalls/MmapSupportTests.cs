using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Loader;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class MmapSupportTests
{
    [Fact]
    public void TaskSize32_Is4GBoundary()
    {
        Assert.Equal(0xffffe000u, LinuxConstants.TaskSize32);
    }

    [Fact]
    public void GuestAddressSpaceLayout_TaskSize_Is4GBoundary()
    {
        var layout = GuestAddressSpaceLayout.CreateCompat32(
            new ResourceLimit(LinuxConstants.RLIM64_INFINITY, LinuxConstants.RLIM64_INFINITY),
            new byte[32]);
        Assert.Equal(0xffffe000u, layout.TaskSize);
    }

    [Fact]
    public void GuestAddressSpaceLayout_StackTopMax_BelowTaskSize()
    {
        var layout = GuestAddressSpaceLayout.CreateCompat32(
            new ResourceLimit(LinuxConstants.RLIM64_INFINITY, LinuxConstants.RLIM64_INFINITY),
            new byte[32]);
        Assert.True(layout.StackTopMax < layout.TaskSize);
        Assert.True(layout.StackTopMax > 0);
    }

    [Fact]
    public void GuestAddressSpaceLayout_MmapBase_BelowStackTopMax()
    {
        var layout = GuestAddressSpaceLayout.CreateCompat32(
            new ResourceLimit(LinuxConstants.RLIM64_INFINITY, LinuxConstants.RLIM64_INFINITY),
            new byte[32]);
        Assert.True(layout.MmapBase < layout.StackTopMax);
        Assert.True(layout.MmapBase >= LinuxConstants.MinMmapAddr);
    }

    [Fact]
    public async Task Mmap_RegularTmpfsFile_Succeeds()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x10000);
        env.WriteCString(0x10000, "/file");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x10000, 0x8000 | 0x1A4));
        var fd = await env.Call("SysOpen", 0x10000);
        Assert.True(fd >= 0);

        var rc = await env.Call("SysMmap2", 0, LinuxConstants.PageSize, (uint)Protection.Read, (uint)MapFlags.Private,
            (uint)fd);
        Assert.True((uint)rc < 0xFFFFF000u);
    }

    [Fact]
    public async Task Mmap_CharDevice_Returns_ENODEV()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x11000);
        env.WriteCString(0x11000, "/devchar");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x11000, 0x2000 | 0x1A4));
        var fd = await env.Call("SysOpen", 0x11000);
        Assert.True(fd >= 0);

        var rc = await env.Call("SysMmap2", 0, LinuxConstants.PageSize, (uint)Protection.Read, (uint)MapFlags.Private,
            (uint)fd);
        Assert.Equal(-(int)Errno.ENODEV, rc);
    }

    [Fact]
    public async Task Mmap_EventFd_Returns_ENODEV()
    {
        using var env = new TestEnv();
        var fd = await env.Call("SysEventFd2");
        Assert.True(fd >= 0);

        var rc = await env.Call("SysMmap2", 0, LinuxConstants.PageSize, (uint)Protection.Read, (uint)MapFlags.Private,
            (uint)fd);
        Assert.Equal(-(int)Errno.ENODEV, rc);
    }

    [Fact]
    public async Task Mmap_AutoAllocation_PrefersHighestGap()
    {
        using var env = new TestEnv();
        const uint highRegionStart = 0x70000000;
        const uint highRegionLength = 0x10000000;
        const uint midRegionStart = 0x50000000;
        var mapLen = (uint)(LinuxConstants.PageSize * 2);

        Assert.Equal(highRegionStart, (uint)await env.Call("SysMmap2", highRegionStart, highRegionLength,
            (uint)Protection.Read,
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed)));
        Assert.Equal(midRegionStart, (uint)await env.Call("SysMmap2", midRegionStart, LinuxConstants.PageSize,
            (uint)Protection.Read,
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed)));

        var mapped = await env.Call("SysMmap2", 0, mapLen, (uint)Protection.Read,
            (uint)(MapFlags.Private | MapFlags.Anonymous));

        var gapEnd = env.Vma.Layout?.MmapBase ?? LinuxConstants.TaskSize32;
        if (env.SyscallManager.SigReturnAddr != 0)
            gapEnd = Math.Min(gapEnd, env.SyscallManager.SigReturnAddr);
        var expected = gapEnd - mapLen;
        Assert.Equal(expected, (uint)mapped);
    }

    [Fact]
    public async Task Mprotect_ReadOnly_RevokesWriteOnCleanMappedPage()
    {
        using var env = new TestEnv();
        var mapped = await env.Call("SysMmap2", 0, LinuxConstants.PageSize, (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous));
        Assert.True((uint)mapped < 0xFFFFF000u);
        var addr = (uint)mapped;

        // Fault in as readable (clean page, not dirty).
        Assert.Equal(FaultResult.Handled, env.Vma.HandleFaultDetailed(addr, false, env.Engine));

        var oneByte = new byte[1];

        Assert.Equal(0, await env.Call("SysMprotect", addr, LinuxConstants.PageSize, (uint)Protection.Read));

        oneByte[0] = 0x7F;
        Assert.False(env.Engine.CopyToUser(addr, oneByte));
    }

    [Fact]
    public async Task Mmap_FixedHighAddress_FitWithinTaskSize_Succeeds()
    {
        using var env = new TestEnv();
        var addr = LinuxConstants.TaskSize32 - LinuxConstants.PageSize * 2;
        var rc = await env.Call("SysMmap2", addr, LinuxConstants.PageSize, (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed));
        Assert.Equal(addr, (uint)rc);
        Assert.Equal(FaultResult.Handled, env.Vma.HandleFaultDetailed(addr, true, env.Engine));
    }

    [Fact]
    public async Task Mmap_FixedHighAddress_OverrunTaskSize_ReturnsEnomem()
    {
        using var env = new TestEnv();
        var addr = LinuxConstants.TaskSize32 - LinuxConstants.PageSize;
        var rc = await env.Call("SysMmap2", addr, LinuxConstants.PageSize * 2, (uint)Protection.Read,
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed));
        Assert.Equal(-(int)Errno.ENOMEM, rc);
    }

    [Fact]
    public async Task Mmap_FixedNoReplace_OverlappingRange_ReturnsEexist()
    {
        using var env = new TestEnv();
        const uint baseAddr = 0x50000000;

        var first = await env.Call("SysMmap2", baseAddr, LinuxConstants.PageSize * 2,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed));
        Assert.Equal(baseAddr, (uint)first);

        var overlap = await env.Call("SysMmap2", baseAddr + LinuxConstants.PageSize, LinuxConstants.PageSize,
            (uint)Protection.Read,
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.FixedNoReplace));
        Assert.Equal(-(int)Errno.EEXIST, overlap);
    }

    [Fact]
    public async Task Munmap_MiddleRange_UnmapsOnlyRequestedSlice()
    {
        using var env = new TestEnv();
        const uint baseAddr = 0x51000000;
        var mapLen = (uint)(LinuxConstants.PageSize * 3);

        var mapped = await env.Call("SysMmap2", baseAddr, mapLen, (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed));
        Assert.Equal(baseAddr, (uint)mapped);

        Assert.Equal(FaultResult.Handled, env.Vma.HandleFaultDetailed(baseAddr, false, env.Engine));
        Assert.Equal(FaultResult.Handled,
            env.Vma.HandleFaultDetailed(baseAddr + LinuxConstants.PageSize, false, env.Engine));
        Assert.Equal(FaultResult.Handled,
            env.Vma.HandleFaultDetailed(baseAddr + LinuxConstants.PageSize * 2, false, env.Engine));

        Assert.Equal(0, await env.Call("SysMunmap", baseAddr + LinuxConstants.PageSize, LinuxConstants.PageSize));

        Assert.Equal(FaultResult.Handled, env.Vma.HandleFaultDetailed(baseAddr, false, env.Engine));
        Assert.Equal(FaultResult.Segv,
            env.Vma.HandleFaultDetailed(baseAddr + LinuxConstants.PageSize, false, env.Engine));
        Assert.Equal(FaultResult.Handled,
            env.Vma.HandleFaultDetailed(baseAddr + LinuxConstants.PageSize * 2, false, env.Engine));
    }

    [Fact]
    public async Task Mprotect_RangeCrossingHole_ReturnsEnomem()
    {
        using var env = new TestEnv();
        const uint baseAddr = 0x52000000;

        Assert.Equal(baseAddr, (uint)await env.Call("SysMmap2", baseAddr, LinuxConstants.PageSize,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed)));
        Assert.Equal(baseAddr + LinuxConstants.PageSize * 2,
            (uint)await env.Call("SysMmap2", baseAddr + LinuxConstants.PageSize * 2, LinuxConstants.PageSize,
                (uint)(Protection.Read | Protection.Write),
                (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed)));

        var rc = await env.Call("SysMprotect", baseAddr, LinuxConstants.PageSize * 3, (uint)Protection.Read);
        Assert.Equal(-(int)Errno.ENOMEM, rc);
    }

    [Fact]
    public async Task Mprotect_PartialRange_SplitsVmaAndAppliesPermissions()
    {
        using var env = new TestEnv();
        const uint baseAddr = 0x53000000;
        var len = (uint)(LinuxConstants.PageSize * 2);

        Assert.Equal(baseAddr, (uint)await env.Call("SysMmap2", baseAddr, len,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed)));

        Assert.Equal(FaultResult.Handled, env.Vma.HandleFaultDetailed(baseAddr, false, env.Engine));
        Assert.Equal(FaultResult.Handled,
            env.Vma.HandleFaultDetailed(baseAddr + LinuxConstants.PageSize, false, env.Engine));

        Assert.Equal(0, await env.Call("SysMprotect", baseAddr + LinuxConstants.PageSize, LinuxConstants.PageSize,
            (uint)Protection.Read));

        var oneByte = new byte[1] { 0x11 };
        Assert.True(env.Engine.CopyToUser(baseAddr, oneByte));
        Assert.False(env.Engine.CopyToUser(baseAddr + LinuxConstants.PageSize, oneByte));
    }

    [Fact]
    public async Task Madvise_DontFork_PartialRange_SplitsVmaAndMarksSlice()
    {
        using var env = new TestEnv();
        const uint baseAddr = 0x53100000;
        var len = (uint)(LinuxConstants.PageSize * 3);

        Assert.Equal(baseAddr, (uint)await env.Call("SysMmap2", baseAddr, len,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed)));

        Assert.Equal(0, await env.Call("SysMadvise", baseAddr + LinuxConstants.PageSize, LinuxConstants.PageSize, 10));

        var vmas = env.Vma.VMAs
            .Where(v => v.Start >= baseAddr && v.End <= baseAddr + len)
            .OrderBy(v => v.Start)
            .ToArray();
        Assert.Equal(3, vmas.Length);
        Assert.False(vmas[0].DontFork);
        Assert.True(vmas[1].DontFork);
        Assert.False(vmas[2].DontFork);
    }

    [Fact]
    public async Task Madvise_Dofork_Subrange_ClearsInheritedForkSuppression()
    {
        using var env = new TestEnv();
        const uint baseAddr = 0x53110000;
        var len = (uint)(LinuxConstants.PageSize * 3);

        Assert.Equal(baseAddr, (uint)await env.Call("SysMmap2", baseAddr, len,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed)));

        Assert.Equal(0, await env.Call("SysMadvise", baseAddr, len, 10));
        Assert.Equal(0, await env.Call("SysMadvise", baseAddr + LinuxConstants.PageSize, LinuxConstants.PageSize, 11));

        var vmas = env.Vma.VMAs
            .Where(v => v.Start >= baseAddr && v.End <= baseAddr + len)
            .OrderBy(v => v.Start)
            .ToArray();
        Assert.Equal(3, vmas.Length);
        Assert.True(vmas[0].DontFork);
        Assert.False(vmas[1].DontFork);
        Assert.True(vmas[2].DontFork);
    }

    [Fact]
    public async Task Madvise_DontNeed_AnonymousPrivate_DropsDiscardedPagesToZero()
    {
        using var env = new TestEnv();
        const uint baseAddr = 0x53120000;
        var len = (uint)(LinuxConstants.PageSize * 2);

        Assert.Equal(baseAddr, (uint)await env.Call("SysMmap2", baseAddr, len,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed)));

        Assert.True(env.Engine.CopyToUser(baseAddr, new byte[] { 0x41 }));
        Assert.True(env.Engine.CopyToUser(baseAddr + LinuxConstants.PageSize, new byte[] { 0x42 }));

        Assert.Equal(0, await env.Call("SysMadvise", baseAddr, LinuxConstants.PageSize, LinuxConstants.MADV_DONTNEED));

        var probe = new byte[1];
        Assert.True(env.Engine.CopyFromUser(baseAddr, probe));
        Assert.Equal(0, probe[0]);
        Assert.True(env.Engine.CopyFromUser(baseAddr + LinuxConstants.PageSize, probe));
        Assert.Equal(0x42, probe[0]);
    }

    [Fact]
    public async Task Madvise_DontNeed_PrivateFileMapping_DropsCowPageBackToBackingFile()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x12000);
        env.MapUserPage(0x13000);
        env.WriteCString(0x12000, "/private-dontneed");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x12000, 0x8000 | 0x1A4));
        var fd = await env.Call("SysOpen", 0x12000, (uint)FileFlags.O_RDWR);
        Assert.True(fd >= 0);

        var filePage = new byte[LinuxConstants.PageSize];
        Array.Fill(filePage, (byte)0x31);
        Assert.True(env.Engine.CopyToUser(0x13000, filePage));
        Assert.Equal(LinuxConstants.PageSize, await env.Call("SysWrite", (uint)fd, 0x13000, LinuxConstants.PageSize));

        var mapped = await env.Call("SysMmap2", 0, LinuxConstants.PageSize, (uint)(Protection.Read | Protection.Write),
            (uint)MapFlags.Private, (uint)fd);
        Assert.True((uint)mapped < 0xFFFFF000u);
        var baseAddr = (uint)mapped;

        Assert.True(env.Engine.CopyToUser(baseAddr, new byte[] { 0x5A }));

        var probe = new byte[1];
        Assert.True(env.Engine.CopyFromUser(baseAddr, probe));
        Assert.Equal(0x5A, probe[0]);

        Assert.Equal(0,
            await env.Call("SysMadvise", baseAddr, LinuxConstants.PageSize, LinuxConstants.MADV_DONTNEED));

        Assert.True(env.Engine.CopyFromUser(baseAddr, probe));
        Assert.Equal(0x31, probe[0]);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task Mprotect_DoesNotCaptureOrUnmapSharedDirtyPages()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x12000);
        env.WriteCString(0x12000, "/shared");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x12000, 0x8000 | 0x1A4));
        var fd = await env.Call("SysOpen", 0x12000, (uint)FileFlags.O_RDWR);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysFtruncate", (uint)fd, LinuxConstants.PageSize * 2));

        var mapped = await env.Call("SysMmap2", 0, LinuxConstants.PageSize * 2,
            (uint)(Protection.Read | Protection.Write), (uint)MapFlags.Shared, (uint)fd);
        Assert.True((uint)mapped < 0xFFFFF000u);
        var baseAddr = (uint)mapped;
        var secondPage = baseAddr + LinuxConstants.PageSize;

        var payload = new byte[1] { 0x42 };
        Assert.Equal(FaultResult.Handled, env.Vma.HandleFaultDetailed(secondPage, true, env.Engine));
        Assert.True(env.Engine.CopyToUser(secondPage, payload));
        Assert.True(env.Engine.IsDirty(secondPage));

        var vma = env.Vma.FindVmArea(baseAddr);
        Assert.NotNull(vma);
        var secondPageIndex = vma!.GetPageIndex(secondPage);
        Assert.NotNull(vma.VmMapping);
        Assert.False(vma.VmMapping.IsDirty(secondPageIndex));
        Assert.True(env.Engine.HasMappedPage(secondPage, LinuxConstants.PageSize));

        Assert.Equal(0, await env.Call("SysMprotect", baseAddr, LinuxConstants.PageSize, (uint)Protection.Read));
        Assert.False(vma.VmMapping.IsDirty(secondPageIndex));
        Assert.True(env.Engine.HasMappedPage(secondPage, LinuxConstants.PageSize));

        Assert.Equal(0, await env.Call("SysMprotect", secondPage, LinuxConstants.PageSize, (uint)Protection.Read));
        Assert.False(vma.VmMapping.IsDirty(secondPageIndex));
        Assert.True(env.Engine.HasMappedPage(secondPage, LinuxConstants.PageSize));
    }

    [Fact]
    public async Task Mprotect_PartialPrivateFileMapping_RefaultsAfterUnmapWithoutSegv()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x14000);
        env.WriteCString(0x14000, "/cow-mprotect");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x14000, 0x8000 | 0x1A4));
        var fd = await env.Call("SysOpen", 0x14000, (uint)FileFlags.O_RDWR);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysFtruncate", (uint)fd, LinuxConstants.PageSize * 3));

        const uint baseAddr = 0x54000000;
        var mapLen = (uint)(LinuxConstants.PageSize * 3);
        var mapped = await env.Call("SysMmap2", baseAddr, mapLen, (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Fixed), (uint)fd);
        Assert.Equal(baseAddr, (uint)mapped);

        var middlePage = baseAddr + LinuxConstants.PageSize;
        Assert.Equal(FaultResult.Handled, env.Vma.HandleFaultDetailed(middlePage, true, env.Engine));

        Assert.Equal(0, await env.Call("SysMprotect", baseAddr, LinuxConstants.PageSize * 2,
            (uint)Protection.Read));

        Assert.Equal(FaultResult.Handled, env.Vma.HandleFaultDetailed(middlePage, false, env.Engine));
        var probe = new byte[1];
        Assert.True(env.Engine.CopyFromUser(middlePage, probe));
    }

    [Fact]
    public async Task Mprotect_PrivateFileSplit_ReusesSameVmAnonVma()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x15000);
        env.WriteCString(0x15000, "/cow-share-mprotect");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x15000, 0x8000 | 0x1A4));
        var fd = await env.Call("SysOpen", 0x15000, (uint)FileFlags.O_RDWR);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysFtruncate", (uint)fd, LinuxConstants.PageSize * 3));

        const uint baseAddr = 0x54100000;
        var mapLen = (uint)(LinuxConstants.PageSize * 3);
        Assert.Equal(baseAddr, (uint)await env.Call("SysMmap2", baseAddr, mapLen,
            (uint)(Protection.Read | Protection.Write), (uint)(MapFlags.Private | MapFlags.Fixed), (uint)fd));

        Assert.Equal(0, await env.Call("SysMprotect", baseAddr + LinuxConstants.PageSize, LinuxConstants.PageSize,
            (uint)Protection.Read));

        var splitVmas = env.Vma.VMAs
            .Where(v => v.Start >= baseAddr && v.End <= baseAddr + mapLen)
            .OrderBy(v => v.Start)
            .ToArray();
        Assert.Equal(3, splitVmas.Length);
        Assert.All(splitVmas, vma => Assert.Null(vma.VmAnonVma));
    }

    [Fact]
    public async Task Munmap_PrivateFileMiddleSplit_ReusesSameVmAnonVma()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x16000);
        env.WriteCString(0x16000, "/cow-share-munmap");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x16000, 0x8000 | 0x1A4));
        var fd = await env.Call("SysOpen", 0x16000, (uint)FileFlags.O_RDWR);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysFtruncate", (uint)fd, LinuxConstants.PageSize * 3));

        const uint baseAddr = 0x54200000;
        var mapLen = (uint)(LinuxConstants.PageSize * 3);
        Assert.Equal(baseAddr, (uint)await env.Call("SysMmap2", baseAddr, mapLen,
            (uint)(Protection.Read | Protection.Write), (uint)(MapFlags.Private | MapFlags.Fixed), (uint)fd));

        Assert.Equal(0, await env.Call("SysMunmap", baseAddr + LinuxConstants.PageSize, LinuxConstants.PageSize));

        var splitVmas = env.Vma.VMAs
            .Where(v => v.Start >= baseAddr && v.End <= baseAddr + mapLen)
            .OrderBy(v => v.Start)
            .ToArray();
        Assert.Equal(2, splitVmas.Length);
        Assert.All(splitVmas, vma => Assert.Null(vma.VmAnonVma));
    }

    [Fact]
    public async Task Munmap_PrivateAnonymousMiddleSplit_ReleasesUnmappedPrivatePages()
    {
        using var env = new TestEnv();
        const uint baseAddr = 0x54300000;
        var mapLen = (uint)(LinuxConstants.PageSize * 3);

        Assert.Equal(baseAddr, (uint)await env.Call("SysMmap2", baseAddr, mapLen,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed)));

        Assert.True(env.Engine.CopyToUser(baseAddr, new[] { (byte)0x11 }));
        Assert.True(env.Engine.CopyToUser(baseAddr + LinuxConstants.PageSize, new[] { (byte)0x22 }));
        Assert.True(env.Engine.CopyToUser(baseAddr + LinuxConstants.PageSize * 2, new[] { (byte)0x33 }));

        var initialVma = Assert.Single(env.Vma.VMAs.Where(v => v.Start == baseAddr));
        Assert.NotNull(initialVma.VmAnonVma);
        Assert.Equal(3, initialVma.VmAnonVma!.PageCount);

        Assert.Equal(0, await env.Call("SysMunmap", baseAddr + LinuxConstants.PageSize, LinuxConstants.PageSize));

        var splitVmas = env.Vma.VMAs
            .Where(v => v.Start >= baseAddr && v.End <= baseAddr + mapLen)
            .OrderBy(v => v.Start)
            .ToArray();
        Assert.Equal(2, splitVmas.Length);
        Assert.NotNull(splitVmas[0].VmAnonVma);
        Assert.Same(splitVmas[0].VmAnonVma, splitVmas[1].VmAnonVma);
        var privateObject = splitVmas[0].VmAnonVma!;
        Assert.Equal(2, privateObject.PageCount);

        Assert.Equal(0, await env.Call("SysMunmap", baseAddr, LinuxConstants.PageSize));
        Assert.Equal(1, privateObject.PageCount);
        Assert.Equal(0, await env.Call("SysMunmap", baseAddr + LinuxConstants.PageSize * 2, LinuxConstants.PageSize));
        Assert.Equal(0, privateObject.PageCount);
    }

    [Fact]
    public async Task Munmap_PrivateAnonymousHeadTrim_RefaultPreservesCowPageData()
    {
        using var env = new TestEnv();
        const uint baseAddr = 0x54380000;
        var mapLen = (uint)(LinuxConstants.PageSize * 3);

        Assert.Equal(baseAddr, (uint)await env.Call("SysMmap2", baseAddr, mapLen,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed)));

        var secondPage = baseAddr + LinuxConstants.PageSize;
        Assert.True(env.Engine.CopyToUser(secondPage, new byte[1] { 0x5A }));

        var originalVma = Assert.Single(env.Vma.VMAs.Where(v => v.Start == baseAddr));
        Assert.NotNull(originalVma.VmAnonVma);
        Assert.NotEqual(IntPtr.Zero, originalVma.VmAnonVma!.PeekPage(originalVma.GetPageIndex(secondPage)));

        Assert.Equal(0, await env.Call("SysMunmap", baseAddr, LinuxConstants.PageSize));

        var trimmedVma = Assert.Single(env.Vma.VMAs.Where(v => v.Start == secondPage && v.End == baseAddr + mapLen));
        Assert.Equal(1UL, trimmedVma.VmPgoff);

        env.Vma.TearDownNativeMappings(env.Engine, secondPage, LinuxConstants.PageSize,
            captureDirtySharedPages: false, invalidateCodeRange: true, releaseExternalPages: true,
            preserveOwnerBinding: true);

        var probe = new byte[1];
        Assert.True(env.Engine.CopyFromUser(secondPage, probe));
        Assert.Equal(0x5A, probe[0]);
    }

    [Fact]
    public async Task Munmap_PrivateFileHeadTrim_PreservesPageIndexBaseForRefaults()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x1A000);
        env.MapUserPage(0x1B000);
        env.WriteCString(0x1A000, "/cow-head-trim-file");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x1A000, 0x8000 | 0x1A4));
        var fd = await env.Call("SysOpen", 0x1A000, (uint)FileFlags.O_RDWR);
        Assert.True(fd >= 0);

        Assert.True(env.Engine.CopyToUser(0x1B000, new byte[1] { (byte)'A' }));
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x1B000, 1));
        Assert.Equal(LinuxConstants.PageSize, await env.Call("SysLseek", (uint)fd, LinuxConstants.PageSize, 0));
        Assert.True(env.Engine.CopyToUser(0x1B000, new byte[1] { (byte)'B' }));
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x1B000, 1));

        const uint baseAddr = 0x543C0000;
        var mapLen = (uint)(LinuxConstants.PageSize * 2);
        Assert.Equal(baseAddr, (uint)await env.Call("SysMmap2", baseAddr, mapLen,
            (uint)Protection.Read, (uint)(MapFlags.Private | MapFlags.Fixed), (uint)fd));

        var mappedVma = Assert.Single(env.Vma.VMAs.Where(v => v.Start == baseAddr));
        Assert.NotNull(mappedVma.VmMapping);
        Assert.True(mappedVma.VmMapping!.PageCount >= 2);

        Assert.Equal(0, await env.Call("SysMunmap", baseAddr, LinuxConstants.PageSize));

        var secondPage = baseAddr + LinuxConstants.PageSize;
        var trimmedVma = Assert.Single(env.Vma.VMAs.Where(v => v.Start == secondPage && v.End == baseAddr + mapLen));
        Assert.Equal(1UL, trimmedVma.VmPgoff);

        var probe = new byte[1];
        Assert.True(env.Engine.CopyFromUser(secondPage, probe));
        Assert.Equal((byte)'B', probe[0]);
    }

    [Fact]
    public async Task Munmap_SharedFileMiddleRange_DoesNotDropAddressSpacePages()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x17000);
        env.WriteCString(0x17000, "/shared-cache-munmap");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x17000, 0x8000 | 0x1A4));
        var fd = await env.Call("SysOpen", 0x17000, (uint)FileFlags.O_RDWR);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysFtruncate", (uint)fd, LinuxConstants.PageSize * 3));

        const uint baseAddr = 0x54400000;
        var mapLen = (uint)(LinuxConstants.PageSize * 3);
        Assert.Equal(baseAddr, (uint)await env.Call("SysMmap2", baseAddr, mapLen,
            (uint)(Protection.Read | Protection.Write), (uint)(MapFlags.Shared | MapFlags.Fixed), (uint)fd));

        Assert.Equal(FaultResult.Handled, env.Vma.HandleFaultDetailed(baseAddr, false, env.Engine));
        Assert.Equal(FaultResult.Handled,
            env.Vma.HandleFaultDetailed(baseAddr + LinuxConstants.PageSize, false, env.Engine));
        Assert.Equal(FaultResult.Handled,
            env.Vma.HandleFaultDetailed(baseAddr + LinuxConstants.PageSize * 2, false, env.Engine));

        var mappedVma = Assert.Single(env.Vma.VMAs.Where(v => v.Start == baseAddr));
        Assert.NotNull(mappedVma.VmMapping);
        Assert.Equal(3, mappedVma.VmMapping.PageCount);

        Assert.Equal(0, await env.Call("SysMunmap", baseAddr + LinuxConstants.PageSize, LinuxConstants.PageSize));

        var splitVmas = env.Vma.VMAs
            .Where(v => v.Start >= baseAddr && v.End <= baseAddr + mapLen)
            .OrderBy(v => v.Start)
            .ToArray();
        Assert.Equal(2, splitVmas.Length);
        Assert.NotNull(splitVmas[0].VmMapping);
        Assert.NotNull(splitVmas[1].VmMapping);
        var firstMapping = splitVmas[0].VmMapping!;
        var secondMapping = splitVmas[1].VmMapping!;
        Assert.Equal(3, firstMapping.PageCount);
        Assert.Same(firstMapping, secondMapping);
        Assert.Equal(FaultResult.Segv,
            env.Vma.HandleFaultDetailed(baseAddr + LinuxConstants.PageSize, false, env.Engine));
    }

    [Fact]
    public async Task Munmap_MultiVmaWindowReplacement_PreservesRemainingFragmentsInOrder()
    {
        using var env = new TestEnv();
        const uint baseAddr = 0x54500000;

        Assert.Equal(baseAddr, (uint)await env.Call("SysMmap2", baseAddr, LinuxConstants.PageSize * 2,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed)));
        Assert.Equal(baseAddr + LinuxConstants.PageSize * 2, (uint)await env.Call("SysMmap2",
            baseAddr + LinuxConstants.PageSize * 2, LinuxConstants.PageSize,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed)));
        Assert.Equal(baseAddr + LinuxConstants.PageSize * 3, (uint)await env.Call("SysMmap2",
            baseAddr + LinuxConstants.PageSize * 3, LinuxConstants.PageSize * 2,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed)));

        Assert.Equal(0, await env.Call("SysMunmap", baseAddr + LinuxConstants.PageSize, LinuxConstants.PageSize * 3));

        var vmas = env.Vma.VMAs
            .Where(v => v.Start >= baseAddr && v.End <= baseAddr + LinuxConstants.PageSize * 5)
            .OrderBy(v => v.Start)
            .ToArray();
        Assert.Equal(2, vmas.Length);
        Assert.Equal(baseAddr, vmas[0].Start);
        Assert.Equal(baseAddr + LinuxConstants.PageSize, vmas[0].End);
        Assert.Equal(baseAddr + LinuxConstants.PageSize * 4, vmas[1].Start);
        Assert.Equal(baseAddr + LinuxConstants.PageSize * 5, vmas[1].End);
    }

    [Fact]
    public async Task Mprotect_MultiVmaWindowReplacement_PreservesFragmentsInOrder()
    {
        using var env = new TestEnv();
        const uint baseAddr = 0x54600000;

        Assert.Equal(baseAddr, (uint)await env.Call("SysMmap2", baseAddr, LinuxConstants.PageSize * 2,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed)));
        Assert.Equal(baseAddr + LinuxConstants.PageSize * 2, (uint)await env.Call("SysMmap2",
            baseAddr + LinuxConstants.PageSize * 2, LinuxConstants.PageSize,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed)));
        Assert.Equal(baseAddr + LinuxConstants.PageSize * 3, (uint)await env.Call("SysMmap2",
            baseAddr + LinuxConstants.PageSize * 3, LinuxConstants.PageSize * 2,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed)));

        Assert.Equal(0, await env.Call("SysMprotect", baseAddr + LinuxConstants.PageSize, LinuxConstants.PageSize * 3,
            (uint)Protection.Read));

        var vmas = env.Vma.VMAs
            .Where(v => v.Start >= baseAddr && v.End <= baseAddr + LinuxConstants.PageSize * 5)
            .OrderBy(v => v.Start)
            .ToArray();
        Assert.Equal(5, vmas.Length);
        Assert.Equal((baseAddr, baseAddr + LinuxConstants.PageSize, Protection.Read | Protection.Write),
            (vmas[0].Start, vmas[0].End, vmas[0].Perms));
        Assert.Equal((baseAddr + LinuxConstants.PageSize, baseAddr + LinuxConstants.PageSize * 2, Protection.Read),
            (vmas[1].Start, vmas[1].End, vmas[1].Perms));
        Assert.Equal((baseAddr + LinuxConstants.PageSize * 2, baseAddr + LinuxConstants.PageSize * 3, Protection.Read),
            (vmas[2].Start, vmas[2].End, vmas[2].Perms));
        Assert.Equal((baseAddr + LinuxConstants.PageSize * 3, baseAddr + LinuxConstants.PageSize * 4, Protection.Read),
            (vmas[3].Start, vmas[3].End, vmas[3].Perms));
        Assert.Equal(
            (baseAddr + LinuxConstants.PageSize * 4, baseAddr + LinuxConstants.PageSize * 5,
                Protection.Read | Protection.Write),
            (vmas[4].Start, vmas[4].End, vmas[4].Perms));
    }

    [Fact]
    public async Task Open_PathFromUnfaultedFileMapping_FaultsStringPage()
    {
        using var env = new TestEnv();
        env.SyscallManager.MountStandardDev();
        env.MapUserPage(0x18000);
        env.MapUserPage(0x19000);
        env.WriteCString(0x18000, "/path-by-mmap");
        env.WriteCString(0x19000, "/dev/null");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x18000, 0x8000 | 0x1A4));

        var backingFd = await env.Call("SysOpen", 0x18000, (uint)FileFlags.O_RDWR);
        Assert.True(backingFd >= 0);
        Assert.Equal(10, await env.Call("SysWrite", (uint)backingFd, 0x19000, 10));

        var mapped = await env.Call("SysMmap2", 0, LinuxConstants.PageSize, (uint)Protection.Read,
            (uint)MapFlags.Private, (uint)backingFd);
        Assert.True((uint)mapped < 0xFFFFF000u);

        var openedFd = await env.Call("SysOpen", (uint)mapped, (uint)FileFlags.O_RDWR);
        Assert.True(openedFd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)openedFd));
        Assert.Equal(0, await env.Call("SysClose", (uint)backingFd));
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly TestRuntimeFactory _runtime = new();

        public TestEnv()
        {
            Engine = _runtime.CreateEngine();
            Vma = _runtime.CreateAddressSpace();
            Engine.PageFaultResolver = (addr, isWrite) => Vma.HandleFault(addr, isWrite, Engine);
            SyscallManager = new SyscallManager(Engine, Vma, 0);

            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var sb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "test-tmpfs", null);
            var mount = new Mount(sb, sb.Root)
            {
                Source = "tmpfs",
                FsType = "tmpfs",
                Options = "rw"
            };
            SyscallManager.InitializeRoot(sb.Root, mount);
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public SyscallManager SyscallManager { get; }

        public void Dispose()
        {
            SyscallManager.Close();
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void WriteCString(uint addr, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            Assert.True(Engine.CopyToUser(addr, bytes));
        }

        public async ValueTask<int> Call(string methodName, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0,
            uint a5 = 0, uint a6 = 0)
        {
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var task = (ValueTask<int>)method!.Invoke(SyscallManager, [Engine, a1, a2, a3, a4, a5, a6])!;
            return await task;
        }
    }
}
