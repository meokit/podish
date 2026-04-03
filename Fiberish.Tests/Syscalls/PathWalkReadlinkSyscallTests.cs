using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class PathWalkReadlinkSyscallTests
{
    [Theory]
    [InlineData("/file", -(int)Errno.EINVAL, null)]
    [InlineData("/dir", -(int)Errno.EINVAL, null)]
    [InlineData("/missing", -(int)Errno.ENOENT, null)]
    [InlineData("/link", 4, "file")]
    [InlineData("/indirect", 4, "link")]
    [InlineData("/broken", 15, "/missing-target")]
    [InlineData("/dir-link", 3, "dir")]
    [InlineData("/dir-chain", 8, "dir-link")]
    public async Task Readlink_PathVariants_MatchExpected(string path, int expectedRc, string? expected)
    {
        var (root, mount) = CreateOverlayRootWithReadlinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x10000u);
        env.MapUserPage(0x11000u);
        env.WriteCString(0x10000u, path);

        var rc = await env.Call("SysReadlink", 0x10000u, 0x11000u, 64);
        Assert.Equal(expectedRc, rc);
        if (expected != null)
            Assert.Equal(expected, Encoding.ASCII.GetString(env.ReadBytes(0x11000u, rc)));
    }

    [Theory]
    [InlineData("link", 4, "file")]
    [InlineData("indirect", 4, "link")]
    [InlineData("broken", 15, "/missing-target")]
    [InlineData("dir-link", 3, "dir")]
    [InlineData("dir-chain", 8, "dir-link")]
    [InlineData("missing", -(int)Errno.ENOENT, null)]
    public async Task ReadlinkAt_RelativeToDirectoryFd_MatchExpected(string relativePath, int expectedRc,
        string? expected)
    {
        var (root, mount) = CreateOverlayRootWithReadlinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x12000u);
        env.MapUserPage(0x13000u);
        env.MapUserPage(0x14000u);
        env.WriteCString(0x12000u, "/");
        env.WriteCString(0x13000u, relativePath);

        var dirfd = await env.Call("SysOpen", 0x12000u, (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY));
        Assert.True(dirfd >= 0);

        var rc = await env.Call("SysReadlinkAt", (uint)dirfd, 0x13000u, 0x14000u, 64);
        Assert.Equal(expectedRc, rc);
        if (expected != null)
            Assert.Equal(expected, Encoding.ASCII.GetString(env.ReadBytes(0x14000u, rc)));

        Assert.Equal(0, await env.Call("SysClose", (uint)dirfd));
    }

    [Fact]
    public async Task Readlink_DirectoryWithTrailingSlash_RemainsEinval()
    {
        var (root, mount) = CreateOverlayRootWithReadlinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x17000u);
        env.MapUserPage(0x18000u);
        env.WriteCString(0x17000u, "/dir/");

        Assert.Equal(-(int)Errno.EINVAL, await env.Call("SysReadlink", 0x17000u, 0x18000u, 64));
    }

    [Theory]
    [InlineData("/link/")]
    [InlineData("/indirect/")]
    [InlineData("/broken/")]
    [InlineData("/dir-link/")]
    [InlineData("/dir-chain/")]
    public async Task Readlink_SymlinkWithTrailingSlash_ReturnsEnotdir(string path)
    {
        var (root, mount) = CreateOverlayRootWithReadlinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x19000u);
        env.MapUserPage(0x1A000u);
        env.WriteCString(0x19000u, path);

        Assert.Equal(-(int)Errno.ENOTDIR, await env.Call("SysReadlink", 0x19000u, 0x1A000u, 64));
    }

    [Theory]
    [InlineData("link/")]
    [InlineData("indirect/")]
    [InlineData("broken/")]
    [InlineData("dir-link/")]
    [InlineData("dir-chain/")]
    public async Task ReadlinkAt_RelativeSymlinkWithTrailingSlash_ReturnsEnotdir(string relativePath)
    {
        var (root, mount) = CreateOverlayRootWithReadlinkFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x1B000u);
        env.MapUserPage(0x1C000u);
        env.MapUserPage(0x1D000u);
        env.WriteCString(0x1B000u, "/");
        env.WriteCString(0x1C000u, relativePath);

        var dirfd = await env.Call("SysOpen", 0x1B000u, (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY));
        Assert.True(dirfd >= 0);
        Assert.Equal(-(int)Errno.ENOTDIR, await env.Call("SysReadlinkAt", (uint)dirfd, 0x1C000u, 0x1D000u, 64));
        Assert.Equal(0, await env.Call("SysClose", (uint)dirfd));
    }

    [Theory]
    [InlineData("/link", "file")]
    [InlineData("/indirect", "link")]
    [InlineData("/broken", "/missing-target")]
    [InlineData("/dir-link", "dir")]
    [InlineData("/dir-chain", "dir-link")]
    public void PathWalkWithData_NoFollowOnReadlinkTargets_StopsAtSymlink(string path, string expectedName)
    {
        var (root, mount) = CreateOverlayRootWithReadlinkFixtures();
        using var env = new TestEnv((root, mount));

        var nd = env.SyscallManager.PathWalker.PathWalkWithData(path, null, LookupFlags.NoFollow);
        Assert.False(nd.HasError);
        Assert.NotNull(nd.Path.Dentry);
        Assert.Equal(0, nd.Path.Dentry!.Inode!.Readlink(out var target));
        Assert.Equal(expectedName, target);
    }

    [Theory]
    [InlineData("/link/", -(int)Errno.ENOTDIR)]
    [InlineData("/indirect/", -(int)Errno.ENOTDIR)]
    [InlineData("/broken/", -(int)Errno.ENOTDIR)]
    [InlineData("/dir-link/", -(int)Errno.ENOTDIR)]
    [InlineData("/dir-chain/", -(int)Errno.ENOTDIR)]
    [InlineData("/missing/", -(int)Errno.ENOENT)]
    public void PathWalkWithData_ReadlinkTrailingSlashBoundaries_ReportExpectedErrors(string path, int errno)
    {
        var (root, mount) = CreateOverlayRootWithReadlinkFixtures();
        using var env = new TestEnv((root, mount));

        var nd = env.SyscallManager.PathWalker.PathWalkWithData(path, null, LookupFlags.None);
        Assert.True(nd.HasError);
        Assert.Equal(errno, nd.ErrorCode);
    }

    private static (Dentry Root, Mount Mount) CreateOverlayRootWithReadlinkFixtures()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "readlink-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "readlink-upper", null);

        var lowerRoot = lowerSb.Root;

        var file = new Dentry("file", null, lowerRoot, lowerSb);
        lowerRoot.Inode!.Create(file, 0x1A4, 0, 0);
        var fileWriter = new LinuxFile(file, FileFlags.O_WRONLY, null!);
        try
        {
            Assert.Equal(1, file.Inode!.WriteFromHost(null, fileWriter, "x"u8.ToArray(), 0));
        }
        finally
        {
            fileWriter.Close();
        }

        var dir = new Dentry("dir", null, lowerRoot, lowerSb);
        lowerRoot.Inode.Mkdir(dir, 0x1ED, 0, 0);
        var child = new Dentry("a", null, dir, lowerSb);
        dir.Inode!.Create(child, 0x1A4, 0, 0);
        var childWriter = new LinuxFile(child, FileFlags.O_WRONLY, null!);
        try
        {
            Assert.Equal(1, child.Inode!.WriteFromHost(null, childWriter, "y"u8.ToArray(), 0));
        }
        finally
        {
            childWriter.Close();
        }

        lowerRoot.Inode.Symlink(new Dentry("link", null, lowerRoot, lowerSb), "file", 0, 0);
        lowerRoot.Inode.Symlink(new Dentry("indirect", null, lowerRoot, lowerSb), "link", 0, 0);
        lowerRoot.Inode.Symlink(new Dentry("broken", null, lowerRoot, lowerSb), "/missing-target", 0, 0);
        lowerRoot.Inode.Symlink(new Dentry("dir-link", null, lowerRoot, lowerSb), "dir", 0, 0);
        lowerRoot.Inode.Symlink(new Dentry("dir-chain", null, lowerRoot, lowerSb), "dir-link", 0, 0);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "readlink-overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        var mount = new Mount(overlaySb, overlaySb.Root)
        {
            Source = "overlay",
            FsType = "overlay",
            Options = "rw"
        };

        return (overlaySb.Root, mount);
    }

    private sealed class TestEnv : IDisposable
    {
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
                var sb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "pathwalk-readlink-tmpfs", null);
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
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void WriteCString(uint addr, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            Assert.True(Engine.CopyToUser(addr, bytes));
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
}