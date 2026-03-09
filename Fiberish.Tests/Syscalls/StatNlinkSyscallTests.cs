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

    private static async ValueTask<uint> ReadStatxNlink(TestEnv env, uint pathPtr, uint statxBuf)
    {
        var rc = await env.Call("SysStatx", LinuxConstants.AT_FDCWD, pathPtr, 0, LinuxConstants.STATX_BASIC_STATS,
            statxBuf);
        Assert.Equal(0, rc);
        return env.ReadUInt32(statxBuf + 0x10);
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
