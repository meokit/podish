using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class StatNlinkSyscallTests
{
    [Fact]
    public async Task Statx_Nlink_FollowsLinkAndUnlinkOnTmpfs()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x10000);
        env.MapUserPage(0x11000);
        env.MapUserPage(0x12000);
        env.MapUserPage(0x13000);

        env.WriteCString(0x10000, "/a");
        env.WriteCString(0x11000, "/b");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x10000, 0x8000 | 0x1A4, 0));
        Assert.Equal(1u, await ReadStatxNlink(env, 0x10000, 0x12000));

        Assert.Equal(0, await env.Call("SysLink", 0x10000, 0x11000));
        Assert.Equal(2u, await ReadStatxNlink(env, 0x10000, 0x12000));
        Assert.Equal(2u, await ReadStatxNlink(env, 0x11000, 0x13000));

        Assert.Equal(0, await env.Call("SysUnlink", 0x10000));
        Assert.Equal(1u, await ReadStatxNlink(env, 0x11000, 0x13000));
    }

    [Fact]
    public async Task RenameOverwrite_DropsReplacedInodeNlink_AndPathNlinkStaysOne()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x20000);
        env.MapUserPage(0x21000);
        env.MapUserPage(0x22000);
        env.MapUserPage(0x23000);

        env.WriteCString(0x20000, "/src");
        env.WriteCString(0x21000, "/dst");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x20000, 0x8000 | 0x1A4, 0));
        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x21000, 0x8000 | 0x1A4, 0));
        Assert.Equal(1u, await ReadStatxNlink(env, 0x20000, 0x22000));
        Assert.Equal(1u, await ReadStatxNlink(env, 0x21000, 0x23000));

        var oldDstFd = await env.Call("SysOpen", 0x21000, (uint)FileFlags.O_RDONLY, 0);
        Assert.True(oldDstFd >= 0);

        Assert.Equal(0, await env.Call("SysRename", 0x20000, 0x21000));
        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysStatx", LinuxConstants.AT_FDCWD, 0x20000, 0,
            LinuxConstants.STATX_BASIC_STATS, 0x22000));
        Assert.Equal(1u, await ReadStatxNlink(env, 0x21000, 0x23000));

        Assert.Equal(0, await env.Call("SysFstat64", (uint)oldDstFd, 0x22000));
        Assert.Equal(0u, ReadStat64Nlink(env, 0x22000));

        Assert.Equal(0, await env.Call("SysClose", (uint)oldDstFd));
    }

    [Fact]
    public async Task Rename_SameInodeAlias_IsNoOp_AndPreservesBothNames()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x24000);
        env.MapUserPage(0x25000);
        env.MapUserPage(0x26000);
        env.MapUserPage(0x27000);

        env.WriteCString(0x24000, "/src");
        env.WriteCString(0x25000, "/alias");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x24000, 0x8000 | 0x1A4, 0));
        Assert.Equal(0, await env.Call("SysLink", 0x24000, 0x25000));
        Assert.Equal(2u, await ReadStatxNlink(env, 0x24000, 0x26000));
        Assert.Equal(2u, await ReadStatxNlink(env, 0x25000, 0x27000));

        Assert.Equal(0, await env.Call("SysRename", 0x24000, 0x25000));
        Assert.Equal(2u, await ReadStatxNlink(env, 0x24000, 0x26000));
        Assert.Equal(2u, await ReadStatxNlink(env, 0x25000, 0x27000));
    }

    [Fact]
    public async Task RenameExchange_KeepsBothInodesNlinkUnchanged()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x28000);
        env.MapUserPage(0x29000);
        env.MapUserPage(0x2A000);
        env.MapUserPage(0x2B000);

        env.WriteCString(0x28000, "/left");
        env.WriteCString(0x29000, "/right");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x28000, 0x8000 | 0x1A4, 0));
        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x29000, 0x8000 | 0x1A4, 0));
        Assert.Equal(1u, await ReadStatxNlink(env, 0x28000, 0x2A000));
        Assert.Equal(1u, await ReadStatxNlink(env, 0x29000, 0x2B000));

        Assert.Equal(0, await env.Call("SysRenameAt2",
            LinuxConstants.AT_FDCWD, 0x28000, LinuxConstants.AT_FDCWD, 0x29000, LinuxConstants.RENAME_EXCHANGE));

        Assert.Equal(1u, await ReadStatxNlink(env, 0x28000, 0x2A000));
        Assert.Equal(1u, await ReadStatxNlink(env, 0x29000, 0x2B000));
    }

    private static async ValueTask<uint> ReadStatxNlink(TestEnv env, uint pathPtr, uint statxBuf)
    {
        var rc = await env.Call("SysStatx", LinuxConstants.AT_FDCWD, pathPtr, 0, LinuxConstants.STATX_BASIC_STATS,
            statxBuf);
        Assert.Equal(0, rc);
        return env.ReadUInt32(statxBuf + 0x10);
    }

    private static uint ReadStat64Nlink(TestEnv env, uint stat64Buf)
    {
        return env.ReadUInt32(stat64Buf + 20);
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
            SyscallManager = new SyscallManager(Engine, Vma, 0);

            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var sb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "statx-tmpfs", null);
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

        public uint ReadUInt32(uint addr)
        {
            Span<byte> buf = stackalloc byte[4];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return BinaryPrimitives.ReadUInt32LittleEndian(buf);
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
