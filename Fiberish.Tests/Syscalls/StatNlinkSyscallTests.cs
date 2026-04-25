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

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x10000, 0x8000 | 0x1A4));
        Assert.Equal(1u, await ReadStatxNlink(env, 0x10000, 0x12000));

        Assert.Equal(0, await env.Call("SysLink", 0x10000, 0x11000));
        Assert.Equal(2u, await ReadStatxNlink(env, 0x10000, 0x12000));
        Assert.Equal(2u, await ReadStatxNlink(env, 0x11000, 0x13000));

        Assert.Equal(0, await env.Call("SysUnlink", 0x10000));
        Assert.Equal(1u, await ReadStatxNlink(env, 0x11000, 0x13000));
    }

    [Fact]
    public async Task Statx_Nlink_ForDirectories_TracksMkdirRmdir()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x14000);
        env.MapUserPage(0x15000);
        env.MapUserPage(0x16000);
        env.MapUserPage(0x17000);
        env.MapUserPage(0x18000);

        env.WriteCString(0x14000, "/");
        env.WriteCString(0x15000, "/a");
        env.WriteCString(0x16000, "/a/b");

        Assert.Equal(2u, await ReadStatxNlink(env, 0x14000, 0x17000));

        Assert.Equal(0, await env.Call("SysMkdir", 0x15000, 0x1ED));
        Assert.Equal(3u, await ReadStatxNlink(env, 0x14000, 0x17000));
        Assert.Equal(2u, await ReadStatxNlink(env, 0x15000, 0x18000));

        Assert.Equal(0, await env.Call("SysMkdir", 0x16000, 0x1ED));
        Assert.Equal(3u, await ReadStatxNlink(env, 0x15000, 0x18000));

        Assert.Equal(0, await env.Call("SysRmdir", 0x16000));
        Assert.Equal(2u, await ReadStatxNlink(env, 0x15000, 0x18000));
        Assert.Equal(0, await env.Call("SysRmdir", 0x15000));
        Assert.Equal(2u, await ReadStatxNlink(env, 0x14000, 0x17000));
    }

    [Fact]
    public async Task Statx_Nlink_ForDirectoryRenameAcrossParents_TracksParentCounts()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x19000);
        env.MapUserPage(0x1A000);
        env.MapUserPage(0x1B000);
        env.MapUserPage(0x1C000);
        env.MapUserPage(0x1D000);
        env.MapUserPage(0x1E000);

        env.WriteCString(0x19000, "/from");
        env.WriteCString(0x1A000, "/to");
        env.WriteCString(0x1B000, "/from/child");
        env.WriteCString(0x1C000, "/to/moved");

        Assert.Equal(0, await env.Call("SysMkdir", 0x19000, 0x1ED));
        Assert.Equal(0, await env.Call("SysMkdir", 0x1A000, 0x1ED));
        Assert.Equal(0, await env.Call("SysMkdir", 0x1B000, 0x1ED));

        Assert.Equal(3u, await ReadStatxNlink(env, 0x19000, 0x1D000));
        Assert.Equal(2u, await ReadStatxNlink(env, 0x1A000, 0x1E000));

        Assert.Equal(0, await env.Call("SysRename", 0x1B000, 0x1C000));

        Assert.Equal(2u, await ReadStatxNlink(env, 0x19000, 0x1D000));
        Assert.Equal(3u, await ReadStatxNlink(env, 0x1A000, 0x1E000));
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

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x20000, 0x8000 | 0x1A4));
        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x21000, 0x8000 | 0x1A4));
        Assert.Equal(1u, await ReadStatxNlink(env, 0x20000, 0x22000));
        Assert.Equal(1u, await ReadStatxNlink(env, 0x21000, 0x23000));

        var oldDstFd = await env.Call("SysOpen", 0x21000);
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

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x24000, 0x8000 | 0x1A4));
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

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x28000, 0x8000 | 0x1A4));
        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x29000, 0x8000 | 0x1A4));
        Assert.Equal(1u, await ReadStatxNlink(env, 0x28000, 0x2A000));
        Assert.Equal(1u, await ReadStatxNlink(env, 0x29000, 0x2B000));

        Assert.Equal(0, await env.Call("SysRenameAt2",
            LinuxConstants.AT_FDCWD, 0x28000, LinuxConstants.AT_FDCWD, 0x29000, LinuxConstants.RENAME_EXCHANGE));

        Assert.Equal(1u, await ReadStatxNlink(env, 0x28000, 0x2A000));
        Assert.Equal(1u, await ReadStatxNlink(env, 0x29000, 0x2B000));
    }

    [Fact]
    public async Task Statx_MountIdAndEmptyPath_OnAtFdcwd_ReportCurrentWorkingDirectoryMount()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x2C000);
        env.MapUserPage(0x2D000);

        env.WriteCString(0x2C000, string.Empty);

        var rc = await env.Call("SysStatx", LinuxConstants.AT_FDCWD, 0x2C000,
            LinuxConstants.AT_EMPTY_PATH | LinuxConstants.AT_STATX_DONT_SYNC,
            LinuxConstants.STATX_MNT_ID,
            0x2D000);

        Assert.Equal(0, rc);
        var returnedMask = env.ReadUInt32(0x2D000);
        Assert.True((returnedMask & LinuxConstants.STATX_BASIC_STATS) == LinuxConstants.STATX_BASIC_STATS);
        Assert.True((returnedMask & LinuxConstants.STATX_MNT_ID) != 0);
        Assert.Equal((ulong)env.SyscallManager.RootMount!.Id, env.ReadUInt64(0x2D000 + 0x90));
    }

    [Fact]
    public async Task Statx_NullPathAndEmptyPath_OnAtFdcwd_ReportCurrentWorkingDirectoryMount()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x32000);

        var rc = await env.Call("SysStatx", LinuxConstants.AT_FDCWD, 0,
            LinuxConstants.AT_EMPTY_PATH | LinuxConstants.AT_STATX_DONT_SYNC,
            LinuxConstants.STATX_MNT_ID,
            0x32000);

        Assert.Equal(0, rc);
        var returnedMask = env.ReadUInt32(0x32000);
        Assert.True((returnedMask & LinuxConstants.STATX_BASIC_STATS) == LinuxConstants.STATX_BASIC_STATS);
        Assert.True((returnedMask & LinuxConstants.STATX_MNT_ID) != 0);
        Assert.Equal((ulong)env.SyscallManager.RootMount!.Id, env.ReadUInt64(0x32000 + 0x90));
    }

    [Fact]
    public async Task Statx_InvalidSyncFlagsCombination_ReturnsEinval()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x2E000);
        env.MapUserPage(0x2F000);

        env.WriteCString(0x2E000, "/");

        var rc = await env.Call("SysStatx", LinuxConstants.AT_FDCWD, 0x2E000,
            LinuxConstants.AT_STATX_FORCE_SYNC | LinuxConstants.AT_STATX_DONT_SYNC,
            LinuxConstants.STATX_BASIC_STATS,
            0x2F000);

        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task Statx_UnsupportedRequestedBits_AreClearedFromReturnedMask()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x30000);
        env.MapUserPage(0x31000);

        env.WriteCString(0x30000, "/");

        var rc = await env.Call("SysStatx", LinuxConstants.AT_FDCWD, 0x30000, 0,
            LinuxConstants.STATX_BTIME | LinuxConstants.STATX_DIOALIGN | LinuxConstants.STATX_MNT_ID,
            0x31000);

        Assert.Equal(0, rc);
        var returnedMask = env.ReadUInt32(0x31000);
        Assert.True((returnedMask & LinuxConstants.STATX_MNT_ID) != 0);
        Assert.Equal(0u, returnedMask & LinuxConstants.STATX_BTIME);
        Assert.Equal(0u, returnedMask & LinuxConstants.STATX_DIOALIGN);
        Assert.Equal(0UL, env.ReadUInt64(0x31000 + 0x50));
        Assert.Equal(0u, env.ReadUInt32(0x31000 + 0x98));
        Assert.Equal(0u, env.ReadUInt32(0x31000 + 0x9C));
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
        private readonly TestRuntimeFactory _runtime = new();

        public TestEnv()
        {
            Engine = _runtime.CreateEngine();
            Vma = _runtime.CreateAddressSpace();
            SyscallManager = new SyscallManager(Engine, Vma, 0);

            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var sb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "statx-tmpfs", null);
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

        public uint ReadUInt32(uint addr)
        {
            Span<byte> buf = stackalloc byte[4];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return BinaryPrimitives.ReadUInt32LittleEndian(buf);
        }

        public ulong ReadUInt64(uint addr)
        {
            Span<byte> buf = stackalloc byte[8];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return BinaryPrimitives.ReadUInt64LittleEndian(buf);
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
