using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class OpenTruncateSyscallTests
{
    [Fact]
    public async Task OpenWithTrunc_FollowedByShortWrite_MustDropOldTail()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x10000); // path
        env.MapUserPage(0x11000); // first payload
        env.MapUserPage(0x12000); // second payload
        env.MapUserPage(0x13000); // read buffer
        env.WriteCString(0x10000, "/trunc-open");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x10000, 0x8000 | 0x1A4));

        var fd = await env.Call("SysOpen", 0x10000, (uint)FileFlags.O_RDWR);
        Assert.True(fd >= 0);
        env.WriteBytes(0x11000, "AAAAAA"u8.ToArray());
        Assert.Equal(6, await env.Call("SysWrite", (uint)fd, 0x11000, 6));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x10000, (uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC));
        Assert.True(fd >= 0);
        env.WriteBytes(0x12000, "B"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x12000, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x10000);
        Assert.True(fd >= 0);
        Assert.Equal(1, await env.Call("SysRead", (uint)fd, 0x13000, 16));
        Assert.Equal("B", Encoding.ASCII.GetString(env.ReadBytes(0x13000, 1)));
        Assert.Equal(1, await env.Call("SysLseek", (uint)fd, 0, 2));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task Creat_OnExistingFile_WithShortWrite_MustDropOldTail()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x20000); // path
        env.MapUserPage(0x21000); // first payload
        env.MapUserPage(0x22000); // second payload
        env.MapUserPage(0x23000); // read buffer
        env.WriteCString(0x20000, "/trunc-creat");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x20000, 0x8000 | 0x1A4));

        var fd = await env.Call("SysOpen", 0x20000, (uint)FileFlags.O_RDWR);
        Assert.True(fd >= 0);
        env.WriteBytes(0x21000, "AAAAAA"u8.ToArray());
        Assert.Equal(6, await env.Call("SysWrite", (uint)fd, 0x21000, 6));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysCreat", 0x20000, 0x1A4);
        Assert.True(fd >= 0);
        env.WriteBytes(0x22000, "B"u8.ToArray());
        Assert.Equal(1, await env.Call("SysWrite", (uint)fd, 0x22000, 1));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", 0x20000);
        Assert.True(fd >= 0);
        Assert.Equal(1, await env.Call("SysRead", (uint)fd, 0x23000, 16));
        Assert.Equal("B", Encoding.ASCII.GetString(env.ReadBytes(0x23000, 1)));
        Assert.Equal(1, await env.Call("SysLseek", (uint)fd, 0, 2));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task PWrite_GrowsFile_MustRefreshMappedFileBackingLength()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x30000); // path
        env.MapUserPage(0x31000); // first-page payload
        env.MapUserPage(0x32000); // growth payload
        env.WriteCString(0x30000, "/grow-write");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x30000, 0x8000 | 0x1A4));

        var fd = await env.Call("SysOpen", 0x30000, (uint)FileFlags.O_RDWR);
        Assert.True(fd >= 0);

        var firstPage = new byte[LinuxConstants.PageSize];
        firstPage.AsSpan().Fill((byte)'A');
        env.WriteBytes(0x31000, firstPage);
        Assert.Equal(LinuxConstants.PageSize, await env.Call("SysWrite", (uint)fd, 0x31000, LinuxConstants.PageSize));

        const uint mapAddr = 0x50000000;
        var mmapRc = await env.Call(
            "SysMmap2",
            mapAddr,
            LinuxConstants.PageSize * 2,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Shared | MapFlags.Fixed),
            (uint)fd);
        Assert.Equal((int)mapAddr, mmapRc);

        Assert.Equal(FaultResult.BusError,
            env.Vma.HandleFaultDetailed(mapAddr + LinuxConstants.PageSize, true, env.Engine));

        env.WriteBytes(0x32000, "Z"u8.ToArray());
        Assert.Equal(1,
            await env.Call("SysPWrite", (uint)fd, 0x32000, 1, LinuxConstants.PageSize));

        Assert.Equal(FaultResult.Handled,
            env.Vma.HandleFaultDetailed(mapAddr + LinuxConstants.PageSize, true, env.Engine));

        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
            Engine.PageFaultResolver = (addr, isWrite) => Vma.HandleFault(addr, isWrite, Engine);
            SyscallManager = new SyscallManager(Engine, Vma, 0);

            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var sb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "trunc-tmpfs", null);
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

        public void WriteBytes(uint addr, ReadOnlySpan<byte> bytes)
        {
            Assert.True(Engine.CopyToUser(addr, bytes.ToArray()));
        }

        public byte[] ReadBytes(uint addr, int len)
        {
            var buf = new byte[len];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return buf;
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