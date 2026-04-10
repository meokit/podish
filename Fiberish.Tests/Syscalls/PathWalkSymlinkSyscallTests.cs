using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class PathWalkSymlinkSyscallTests
{
    [Theory]
    [InlineData("/link", (uint)FileFlags.O_RDONLY, null, ":xxx:yyy:zzz", null, ":xxx:yyy:zzz")]
    [InlineData("/link", (uint)FileFlags.O_WRONLY, "q", "qxxx:yyy:zzz", "p", "pxxx:yyy:zzz")]
    [InlineData("/link", (uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND), "q", ":xxx:yyy:zzzq", "p", ":xxx:yyy:zzzqp")]
    [InlineData("/link", (uint)FileFlags.O_RDWR, "q", "qxxx:yyy:zzz", "p", "pxxx:yyy:zzz")]
    [InlineData("/link", (uint)(FileFlags.O_RDWR | FileFlags.O_APPEND), "q", ":xxx:yyy:zzzq", "p", ":xxx:yyy:zzzqp")]
    [InlineData("/indirect", (uint)FileFlags.O_RDONLY, null, ":xxx:yyy:zzz", null, ":xxx:yyy:zzz")]
    [InlineData("/indirect", (uint)FileFlags.O_WRONLY, "q", "qxxx:yyy:zzz", "p", "pxxx:yyy:zzz")]
    [InlineData("/indirect", (uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND), "q", ":xxx:yyy:zzzq", "p", ":xxx:yyy:zzzqp")]
    [InlineData("/indirect", (uint)FileFlags.O_RDWR, "q", "qxxx:yyy:zzz", "p", "pxxx:yyy:zzz")]
    [InlineData("/indirect", (uint)(FileFlags.O_RDWR | FileFlags.O_APPEND), "q", ":xxx:yyy:zzzq", "p", ":xxx:yyy:zzzqp")]
    public async Task Open_ExistingFileSymlink_PlainModes_MatchUpstream(string path, uint flags, string? firstWrite,
        string expectedAfterFirst, string? secondWrite, string expectedAfterSecond)
    {
        await RunOpenSequence(path, flags, firstWrite, expectedAfterFirst, secondWrite, expectedAfterSecond);
    }

    [Theory]
    [InlineData("/link", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT), null, ":xxx:yyy:zzz", null, ":xxx:yyy:zzz")]
    [InlineData("/link", (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT), "q", "qxxx:yyy:zzz", "p", "pxxx:yyy:zzz")]
    [InlineData("/link", (uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND | FileFlags.O_CREAT), "q", ":xxx:yyy:zzzq", "p", ":xxx:yyy:zzzqp")]
    [InlineData("/link", (uint)(FileFlags.O_RDWR | FileFlags.O_CREAT), "q", "qxxx:yyy:zzz", "p", "pxxx:yyy:zzz")]
    [InlineData("/link", (uint)(FileFlags.O_RDWR | FileFlags.O_APPEND | FileFlags.O_CREAT), "q", ":xxx:yyy:zzzq", "p", ":xxx:yyy:zzzqp")]
    [InlineData("/indirect", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT), null, ":xxx:yyy:zzz", null, ":xxx:yyy:zzz")]
    [InlineData("/indirect", (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT), "q", "qxxx:yyy:zzz", "p", "pxxx:yyy:zzz")]
    [InlineData("/indirect", (uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND | FileFlags.O_CREAT), "q", ":xxx:yyy:zzzq", "p", ":xxx:yyy:zzzqp")]
    [InlineData("/indirect", (uint)(FileFlags.O_RDWR | FileFlags.O_CREAT), "q", "qxxx:yyy:zzz", "p", "pxxx:yyy:zzz")]
    [InlineData("/indirect", (uint)(FileFlags.O_RDWR | FileFlags.O_APPEND | FileFlags.O_CREAT), "q", ":xxx:yyy:zzzq", "p", ":xxx:yyy:zzzqp")]
    public async Task Open_ExistingFileSymlink_CreatModes_MatchUpstream(string path, uint flags, string? firstWrite,
        string expectedAfterFirst, string? secondWrite, string expectedAfterSecond)
    {
        await RunOpenSequence(path, flags, firstWrite, expectedAfterFirst, secondWrite, expectedAfterSecond);
    }

    [Theory]
    [InlineData("/link", (uint)(FileFlags.O_RDONLY | FileFlags.O_TRUNC), null, "", null, "")]
    [InlineData("/link", (uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC), "q", "q", "p", "p")]
    [InlineData("/link", (uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND | FileFlags.O_TRUNC), "q", "q", "p", "p")]
    [InlineData("/link", (uint)(FileFlags.O_RDWR | FileFlags.O_TRUNC), "q", "q", "p", "p")]
    [InlineData("/link", (uint)(FileFlags.O_RDWR | FileFlags.O_APPEND | FileFlags.O_TRUNC), "q", "q", "p", "p")]
    [InlineData("/indirect", (uint)(FileFlags.O_RDONLY | FileFlags.O_TRUNC), null, "", null, "")]
    [InlineData("/indirect", (uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC), "q", "q", "p", "p")]
    [InlineData("/indirect", (uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND | FileFlags.O_TRUNC), "q", "q", "p", "p")]
    [InlineData("/indirect", (uint)(FileFlags.O_RDWR | FileFlags.O_TRUNC), "q", "q", "p", "p")]
    [InlineData("/indirect", (uint)(FileFlags.O_RDWR | FileFlags.O_APPEND | FileFlags.O_TRUNC), "q", "q", "p", "p")]
    public async Task Open_ExistingFileSymlink_TruncModes_MatchUpstream(string path, uint flags, string? firstWrite,
        string expectedAfterFirst, string? secondWrite, string expectedAfterSecond)
    {
        await RunOpenSequence(path, flags, firstWrite, expectedAfterFirst, secondWrite, expectedAfterSecond);
    }

    [Theory]
    [InlineData("/link", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData("/link", (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData("/link", (uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData("/link", (uint)(FileFlags.O_RDWR | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData("/link", (uint)(FileFlags.O_RDWR | FileFlags.O_APPEND | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData("/indirect", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData("/indirect", (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData("/indirect", (uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData("/indirect", (uint)(FileFlags.O_RDWR | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData("/indirect", (uint)(FileFlags.O_RDWR | FileFlags.O_APPEND | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    public async Task Open_ExistingFileSymlink_CreatExclModes_ReturnEexist(string path, uint flags)
    {
        var (root, mount) = CreateOverlayRootWithSymlinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x10000u);
        env.WriteCString(0x10000u, path);

        Assert.Equal(-(int)Errno.EEXIST, await env.Call("SysOpen", 0x10000u, flags));
        await AssertFileContent(env, "/file", ":xxx:yyy:zzz");
    }

    [Theory]
    [InlineData((uint)FileFlags.O_RDONLY)]
    [InlineData((uint)FileFlags.O_WRONLY)]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND))]
    [InlineData((uint)FileFlags.O_RDWR)]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_APPEND))]
    public async Task Open_BrokenSymlink_PlainModes_ReturnEnoent(uint flags)
    {
        var (root, mount) = CreateOverlayRootWithSymlinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x10000u);
        env.WriteCString(0x10000u, "/broken");

        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysOpen", 0x10000u, flags));
        Assert.Equal(-(int)Errno.ENOENT, await OpenPath(env, "/missing-target", (uint)FileFlags.O_RDONLY));
    }

    [Theory]
    [InlineData((uint)(FileFlags.O_RDONLY | FileFlags.O_TRUNC))]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC))]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND | FileFlags.O_TRUNC))]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_TRUNC))]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_APPEND | FileFlags.O_TRUNC))]
    public async Task Open_BrokenSymlink_TruncModes_ReturnEnoent(uint flags)
    {
        var (root, mount) = CreateOverlayRootWithSymlinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x10000u);
        env.WriteCString(0x10000u, "/broken");

        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysOpen", 0x10000u, flags));
        Assert.Equal(-(int)Errno.ENOENT, await OpenPath(env, "/missing-target", (uint)FileFlags.O_RDONLY));
    }

    [Theory]
    [InlineData((uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT), null, "")]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT), "q", "q")]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND | FileFlags.O_CREAT), "q", "q")]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_CREAT), "q", "q")]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_APPEND | FileFlags.O_CREAT), "q", "q")]
    [InlineData((uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_TRUNC), null, "")]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_TRUNC), "q", "q")]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND | FileFlags.O_CREAT | FileFlags.O_TRUNC), "q", "q")]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_CREAT | FileFlags.O_TRUNC), "q", "q")]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_APPEND | FileFlags.O_CREAT | FileFlags.O_TRUNC), "q", "q")]
    public async Task Open_BrokenSymlink_CreatLikeModes_CreateTarget(uint flags, string? writePayload, string expected)
    {
        var (root, mount) = CreateOverlayRootWithSymlinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x10000u);
        env.MapUserPage(0x11000u);
        env.WriteCString(0x10000u, "/broken");

        var fd = await env.Call("SysOpen", 0x10000u, flags);
        Assert.True(fd >= 0);
        if (writePayload != null)
        {
            env.WriteBytes(0x11000u, Encoding.ASCII.GetBytes(writePayload));
            Assert.Equal(writePayload.Length, await env.Call("SysWrite", (uint)fd, 0x11000u, (uint)writePayload.Length));
        }

        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
        await AssertFileContent(env, "/missing-target", expected);
    }

    [Theory]
    [InlineData((uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    [InlineData((uint)(FileFlags.O_RDWR | FileFlags.O_APPEND | FileFlags.O_CREAT | FileFlags.O_EXCL))]
    public async Task Open_BrokenSymlink_CreatExclModes_ReturnEexist(uint flags)
    {
        var (root, mount) = CreateOverlayRootWithSymlinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x10000u);
        env.WriteCString(0x10000u, "/broken");

        Assert.Equal(-(int)Errno.EEXIST, await env.Call("SysOpen", 0x10000u, flags));
        Assert.Equal(-(int)Errno.ENOENT, await OpenPath(env, "/missing-target", (uint)FileFlags.O_RDONLY));
    }

    private static async Task RunOpenSequence(string path, uint flags, string? firstWrite, string expectedAfterFirst,
        string? secondWrite, string expectedAfterSecond)
    {
        var (root, mount) = CreateOverlayRootWithSymlinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x10000u);
        env.MapUserPage(0x11000u);
        env.WriteCString(0x10000u, path);

        await OpenMaybeWriteAndClose(env, 0x10000u, flags, 0x11000u, firstWrite);
        await AssertFileContent(env, "/file", expectedAfterFirst);

        await OpenMaybeWriteAndClose(env, 0x10000u, flags, 0x11000u, secondWrite);
        await AssertFileContent(env, "/file", expectedAfterSecond);
    }

    private static async Task OpenMaybeWriteAndClose(TestEnv env, uint pathAddr, uint flags, uint writeAddr,
        string? payload)
    {
        var fd = await env.Call("SysOpen", pathAddr, flags);
        Assert.True(fd >= 0);
        if (payload != null)
        {
            env.WriteBytes(writeAddr, Encoding.ASCII.GetBytes(payload));
            Assert.Equal(payload.Length, await env.Call("SysWrite", (uint)fd, writeAddr, (uint)payload.Length));
        }

        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    private static async Task<int> OpenPath(TestEnv env, string path, uint flags)
    {
        env.MapUserPage(0x12000u);
        env.WriteCString(0x12000u, path);
        return await env.Call("SysOpen", 0x12000u, flags);
    }

    private static async Task AssertFileContent(TestEnv env, string path, string expected)
    {
        env.MapUserPage(0x12000u);
        env.MapUserPage(0x13000u);
        env.WriteCString(0x12000u, path);
        var fd = await env.Call("SysOpen", 0x12000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        var rc = await env.Call("SysRead", (uint)fd, 0x13000u, 64);
        Assert.Equal(expected.Length, rc);
        Assert.Equal(expected, Encoding.ASCII.GetString(env.ReadBytes(0x13000u, rc)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly HashSet<uint> _mappedPages = [];

        public TestEnv((Dentry Root, Mount Mount)? rootOverride = null)
        {
            Engine = new Engine();
            Vma = new VMAManager();
            Engine.PageFaultResolver = (addr, isWrite) => Vma.HandleFault(addr, isWrite, Engine);
            SyscallManager = new SyscallManager(Engine, Vma, 0);

            if (rootOverride is { } root)
            {
                SyscallManager.InitializeRoot(root.Root, root.Mount);
            }
            else
            {
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var sb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "pathwalk-symlink-tmpfs",
                    null);
                var mount = new Mount(sb, sb.Root)
                {
                    Source = "tmpfs",
                    FsType = "tmpfs",
                    Options = "rw"
                };
                SyscallManager.InitializeRoot(sb.Root, mount);
            }
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
            if (!_mappedPages.Add(addr))
                return;

            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void WriteCString(uint addr, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            Assert.True(Engine.CopyToUser(addr, bytes));
        }

        public void WriteBytes(uint addr, byte[] data)
        {
            Assert.True(Engine.CopyToUser(addr, data));
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
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var task = (ValueTask<int>)method!.Invoke(SyscallManager, [Engine, a1, a2, a3, a4, a5, a6])!;
            return await task;
        }
    }

    private static (Dentry Root, Mount Mount) CreateOverlayRootWithSymlinkFixtures()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "sym-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "sym-upper", null);

        var lowerRoot = lowerSb.Root;

        var file = new Dentry("file", null, lowerRoot, lowerSb);
        lowerRoot.Inode!.Create(file, 0x1A4, 0, 0);
        var fileWriter = new LinuxFile(file, FileFlags.O_WRONLY, null!);
        try
        {
            Assert.Equal(12, file.Inode!.WriteFromHost(null, fileWriter, Encoding.ASCII.GetBytes(":xxx:yyy:zzz"), 0));
        }
        finally
        {
            fileWriter.Close();
        }

        lowerRoot.Inode.Symlink(new Dentry("link", null, lowerRoot, lowerSb), "file"u8.ToArray(), 0, 0);
        lowerRoot.Inode.Symlink(new Dentry("indirect", null, lowerRoot, lowerSb), "link"u8.ToArray(), 0, 0);
        lowerRoot.Inode.Symlink(new Dentry("broken", null, lowerRoot, lowerSb), "/missing-target"u8.ToArray(), 0, 0);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "sym-overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        var mount = new Mount(overlaySb, overlaySb.Root)
        {
            Source = "overlay",
            FsType = "overlay",
            Options = "rw"
        };

        return (overlaySb.Root, mount);
    }
}
