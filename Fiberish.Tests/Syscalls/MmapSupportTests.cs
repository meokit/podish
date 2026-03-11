using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class MmapSupportTests
{
    [Fact]
    public async Task Mmap_RegularTmpfsFile_Succeeds()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x10000);
        env.WriteCString(0x10000, "/file");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x10000, 0x8000 | 0x1A4, 0));
        var fd = await env.Call("SysOpen", 0x10000, (uint)FileFlags.O_RDONLY, 0);
        Assert.True(fd >= 0);

        var rc = await env.Call("SysMmap2", 0, LinuxConstants.PageSize, (uint)Protection.Read, (uint)MapFlags.Private, (uint)fd, 0);
        Assert.True(rc > 0);
    }

    [Fact]
    public async Task Mmap_CharDevice_Returns_ENODEV()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x11000);
        env.WriteCString(0x11000, "/devchar");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x11000, 0x2000 | 0x1A4, 0));
        var fd = await env.Call("SysOpen", 0x11000, (uint)FileFlags.O_RDONLY, 0);
        Assert.True(fd >= 0);

        var rc = await env.Call("SysMmap2", 0, LinuxConstants.PageSize, (uint)Protection.Read, (uint)MapFlags.Private, (uint)fd, 0);
        Assert.Equal(-(int)Errno.ENODEV, rc);
    }

    [Fact]
    public async Task Mmap_EventFd_Returns_ENODEV()
    {
        using var env = new TestEnv();
        var fd = await env.Call("SysEventFd2", 0, 0);
        Assert.True(fd >= 0);

        var rc = await env.Call("SysMmap2", 0, LinuxConstants.PageSize, (uint)Protection.Read, (uint)MapFlags.Private, (uint)fd, 0);
        Assert.Equal(-(int)Errno.ENODEV, rc);
    }

    [Fact]
    public async Task Mprotect_ReadOnly_RevokesWriteOnCleanMappedPage()
    {
        using var env = new TestEnv();
        var mapped = await env.Call("SysMmap2", 0, LinuxConstants.PageSize, (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous), 0, 0);
        Assert.True(mapped > 0);
        var addr = (uint)mapped;

        // Fault in as readable (clean page, not dirty).
        Assert.Equal(FaultResult.Handled, env.Vma.HandleFaultDetailed(addr, isWrite: false, env.Engine));

        var oneByte = new byte[1];

        Assert.Equal(0, await env.Call("SysMprotect", addr, LinuxConstants.PageSize, (uint)Protection.Read));

        oneByte[0] = 0x7F;
        Assert.False(env.Engine.CopyToUser(addr, oneByte));
    }

    [Fact]
    public async Task Mmap_FixedHighAddress_FitWithinTaskSize_Succeeds()
    {
        using var env = new TestEnv();
        var addr = LinuxConstants.TaskSize32 - (uint)(LinuxConstants.PageSize * 2);
        var rc = await env.Call("SysMmap2", addr, LinuxConstants.PageSize, (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed), 0, 0);
        Assert.Equal((int)addr, rc);
        Assert.Equal(FaultResult.Handled, env.Vma.HandleFaultDetailed(addr, isWrite: true, env.Engine));
    }

    [Fact]
    public async Task Mmap_FixedHighAddress_OverrunTaskSize_ReturnsEnomem()
    {
        using var env = new TestEnv();
        var addr = LinuxConstants.TaskSize32 - (uint)LinuxConstants.PageSize;
        var rc = await env.Call("SysMmap2", addr, (uint)(LinuxConstants.PageSize * 2), (uint)Protection.Read,
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed), 0, 0);
        Assert.Equal(-(int)Errno.ENOMEM, rc);
    }

    [Fact]
    public async Task Mmap_FixedNoReplace_OverlappingRange_ReturnsEexist()
    {
        using var env = new TestEnv();
        const uint baseAddr = 0x50000000;

        var first = await env.Call("SysMmap2", baseAddr, (uint)(LinuxConstants.PageSize * 2),
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed), 0, 0);
        Assert.Equal((int)baseAddr, first);

        var overlap = await env.Call("SysMmap2", baseAddr + (uint)LinuxConstants.PageSize, LinuxConstants.PageSize,
            (uint)Protection.Read,
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.FixedNoReplace), 0, 0);
        Assert.Equal(-(int)Errno.EEXIST, overlap);
    }

    [Fact]
    public async Task Munmap_MiddleRange_UnmapsOnlyRequestedSlice()
    {
        using var env = new TestEnv();
        const uint baseAddr = 0x51000000;
        var mapLen = (uint)(LinuxConstants.PageSize * 3);

        var mapped = await env.Call("SysMmap2", baseAddr, mapLen, (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed), 0, 0);
        Assert.Equal((int)baseAddr, mapped);

        Assert.Equal(FaultResult.Handled, env.Vma.HandleFaultDetailed(baseAddr, isWrite: false, env.Engine));
        Assert.Equal(FaultResult.Handled,
            env.Vma.HandleFaultDetailed(baseAddr + (uint)LinuxConstants.PageSize, isWrite: false, env.Engine));
        Assert.Equal(FaultResult.Handled,
            env.Vma.HandleFaultDetailed(baseAddr + (uint)(LinuxConstants.PageSize * 2), isWrite: false, env.Engine));

        Assert.Equal(0, await env.Call("SysMunmap", baseAddr + (uint)LinuxConstants.PageSize, LinuxConstants.PageSize));

        Assert.Equal(FaultResult.Handled, env.Vma.HandleFaultDetailed(baseAddr, isWrite: false, env.Engine));
        Assert.Equal(FaultResult.Segv,
            env.Vma.HandleFaultDetailed(baseAddr + (uint)LinuxConstants.PageSize, isWrite: false, env.Engine));
        Assert.Equal(FaultResult.Handled,
            env.Vma.HandleFaultDetailed(baseAddr + (uint)(LinuxConstants.PageSize * 2), isWrite: false, env.Engine));
    }

    [Fact]
    public async Task Mprotect_RangeCrossingHole_ReturnsEnomem()
    {
        using var env = new TestEnv();
        const uint baseAddr = 0x52000000;

        Assert.Equal((int)baseAddr, await env.Call("SysMmap2", baseAddr, LinuxConstants.PageSize,
            (uint)(Protection.Read | Protection.Write), (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed),
            0, 0));
        Assert.Equal((int)(baseAddr + (uint)(LinuxConstants.PageSize * 2)),
            await env.Call("SysMmap2", baseAddr + (uint)(LinuxConstants.PageSize * 2), LinuxConstants.PageSize,
                (uint)(Protection.Read | Protection.Write), (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed),
                0, 0));

        var rc = await env.Call("SysMprotect", baseAddr, (uint)(LinuxConstants.PageSize * 3), (uint)Protection.Read);
        Assert.Equal(-(int)Errno.ENOMEM, rc);
    }

    [Fact]
    public async Task Mprotect_PartialRange_SplitsVmaAndAppliesPermissions()
    {
        using var env = new TestEnv();
        const uint baseAddr = 0x53000000;
        var len = (uint)(LinuxConstants.PageSize * 2);

        Assert.Equal((int)baseAddr, await env.Call("SysMmap2", baseAddr, len, (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed), 0, 0));

        Assert.Equal(FaultResult.Handled, env.Vma.HandleFaultDetailed(baseAddr, isWrite: false, env.Engine));
        Assert.Equal(FaultResult.Handled,
            env.Vma.HandleFaultDetailed(baseAddr + (uint)LinuxConstants.PageSize, isWrite: false, env.Engine));

        Assert.Equal(0, await env.Call("SysMprotect", baseAddr + (uint)LinuxConstants.PageSize, LinuxConstants.PageSize,
            (uint)Protection.Read));

        var oneByte = new byte[1] { 0x11 };
        Assert.True(env.Engine.CopyToUser(baseAddr, oneByte));
        Assert.False(env.Engine.CopyToUser(baseAddr + (uint)LinuxConstants.PageSize, oneByte));
    }

    [Fact]
    public async Task Mprotect_DoesNotCaptureOrUnmapSharedDirtyPages()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x12000);
        env.WriteCString(0x12000, "/shared");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x12000, 0x8000 | 0x1A4, 0));
        var fd = await env.Call("SysOpen", 0x12000, (uint)FileFlags.O_RDWR, 0);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysFtruncate", (uint)fd, (uint)(LinuxConstants.PageSize * 2)));

        var mapped = await env.Call("SysMmap2", 0, (uint)(LinuxConstants.PageSize * 2),
            (uint)(Protection.Read | Protection.Write), (uint)MapFlags.Shared, (uint)fd, 0);
        Assert.True(mapped > 0);
        var baseAddr = (uint)mapped;
        var secondPage = baseAddr + (uint)LinuxConstants.PageSize;

        var payload = new byte[1] { 0x42 };
        Assert.Equal(FaultResult.Handled, env.Vma.HandleFaultDetailed(secondPage, isWrite: true, env.Engine));
#pragma warning disable CS0618
        env.Engine.MemWrite(secondPage, payload);
#pragma warning restore CS0618
        Assert.True(env.Engine.IsDirty(secondPage));

        var vma = env.Vma.FindVMA(baseAddr);
        Assert.NotNull(vma);
        var secondPageIndex = vma!.ViewPageOffset + 1;
        Assert.False(vma.SharedObject.IsDirty(secondPageIndex));
        Assert.True(env.Engine.HasMappedPage(secondPage, LinuxConstants.PageSize));

        Assert.Equal(0, await env.Call("SysMprotect", baseAddr, LinuxConstants.PageSize, (uint)Protection.Read));
        Assert.False(vma.SharedObject.IsDirty(secondPageIndex));
        Assert.True(env.Engine.HasMappedPage(secondPage, LinuxConstants.PageSize));

        Assert.Equal(0, await env.Call("SysMprotect", secondPage, LinuxConstants.PageSize, (uint)Protection.Read));
        Assert.False(vma.SharedObject.IsDirty(secondPageIndex));
        Assert.True(env.Engine.HasMappedPage(secondPage, LinuxConstants.PageSize));
    }

    [Fact]
    public async Task Mprotect_PartialPrivateFileMapping_RefaultsAfterUnmapWithoutSegv()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x14000);
        env.WriteCString(0x14000, "/cow-mprotect");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x14000, 0x8000 | 0x1A4, 0));
        var fd = await env.Call("SysOpen", 0x14000, (uint)FileFlags.O_RDWR, 0);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysFtruncate", (uint)fd, (uint)(LinuxConstants.PageSize * 3)));

        const uint baseAddr = 0x54000000;
        var mapLen = (uint)(LinuxConstants.PageSize * 3);
        var mapped = await env.Call("SysMmap2", baseAddr, mapLen, (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Fixed), (uint)fd, 0);
        Assert.Equal((int)baseAddr, mapped);

        var middlePage = baseAddr + (uint)LinuxConstants.PageSize;
        Assert.Equal(FaultResult.Handled, env.Vma.HandleFaultDetailed(middlePage, isWrite: true, env.Engine));

        Assert.Equal(0, await env.Call("SysMprotect", baseAddr, (uint)(LinuxConstants.PageSize * 2),
            (uint)Protection.Read));

        Assert.Equal(FaultResult.Handled, env.Vma.HandleFaultDetailed(middlePage, isWrite: false, env.Engine));
        var probe = new byte[1];
        Assert.True(env.Engine.CopyFromUser(middlePage, probe));
    }

    [Fact]
    public async Task Mprotect_PrivateFileSplit_ReusesSamePrivateObject()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x15000);
        env.WriteCString(0x15000, "/cow-share-mprotect");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x15000, 0x8000 | 0x1A4, 0));
        var fd = await env.Call("SysOpen", 0x15000, (uint)FileFlags.O_RDWR, 0);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysFtruncate", (uint)fd, (uint)(LinuxConstants.PageSize * 3)));

        const uint baseAddr = 0x54100000;
        var mapLen = (uint)(LinuxConstants.PageSize * 3);
        Assert.Equal((int)baseAddr, await env.Call("SysMmap2", baseAddr, mapLen,
            (uint)(Protection.Read | Protection.Write), (uint)(MapFlags.Private | MapFlags.Fixed), (uint)fd, 0));

        Assert.Equal(0, await env.Call("SysMprotect", baseAddr + (uint)LinuxConstants.PageSize, LinuxConstants.PageSize,
            (uint)Protection.Read));

        var splitVmas = env.Vma.VMAs
            .Where(v => v.Start >= baseAddr && v.End <= baseAddr + mapLen)
            .OrderBy(v => v.Start)
            .ToArray();
        Assert.Equal(3, splitVmas.Length);
        Assert.NotNull(splitVmas[0].PrivateObject);
        Assert.Same(splitVmas[0].PrivateObject, splitVmas[1].PrivateObject);
        Assert.Same(splitVmas[0].PrivateObject, splitVmas[2].PrivateObject);
    }

    [Fact]
    public async Task Munmap_PrivateFileMiddleSplit_ReusesSamePrivateObject()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x16000);
        env.WriteCString(0x16000, "/cow-share-munmap");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x16000, 0x8000 | 0x1A4, 0));
        var fd = await env.Call("SysOpen", 0x16000, (uint)FileFlags.O_RDWR, 0);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysFtruncate", (uint)fd, (uint)(LinuxConstants.PageSize * 3)));

        const uint baseAddr = 0x54200000;
        var mapLen = (uint)(LinuxConstants.PageSize * 3);
        Assert.Equal((int)baseAddr, await env.Call("SysMmap2", baseAddr, mapLen,
            (uint)(Protection.Read | Protection.Write), (uint)(MapFlags.Private | MapFlags.Fixed), (uint)fd, 0));

        Assert.Equal(0, await env.Call("SysMunmap", baseAddr + (uint)LinuxConstants.PageSize, LinuxConstants.PageSize));

        var splitVmas = env.Vma.VMAs
            .Where(v => v.Start >= baseAddr && v.End <= baseAddr + mapLen)
            .OrderBy(v => v.Start)
            .ToArray();
        Assert.Equal(2, splitVmas.Length);
        Assert.NotNull(splitVmas[0].PrivateObject);
        Assert.Same(splitVmas[0].PrivateObject, splitVmas[1].PrivateObject);
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
            SyscallManager = new SyscallManager(Engine, Vma, 0);

            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var sb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-tmpfs", null);
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
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, LinuxConstants.PageSize, "[test]",
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
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var task = (ValueTask<int>)method!.Invoke(null, [Engine.State, a1, a2, a3, a4, a5, a6])!;
            return await task;
        }
    }
}
