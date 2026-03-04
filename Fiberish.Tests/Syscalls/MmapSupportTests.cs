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
