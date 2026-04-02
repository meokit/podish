using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class PathWalkSyscallTests
{
    [Theory]
    [InlineData("/link")]
    [InlineData("/indirect")]
    [InlineData("/broken")]
    public async Task Open_NoFollowOnFinalFileSymlink_ReturnsEloop(string path)
    {
        var (root, mount) = CreateOverlayRootWithFileAndSymlinks();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x08000u);
        env.WriteCString(0x08000u, path);

        Assert.Equal(-(int)Errno.ELOOP,
            await env.Call("SysOpen", 0x08000u, (uint)(FileFlags.O_RDONLY | FileFlags.O_NOFOLLOW)));
    }

    [Theory]
    [InlineData("/dir-link")]
    [InlineData("/dir-chain")]
    public async Task Open_NoFollowOnFinalDirectorySymlink_ReturnsEloop(string path)
    {
        var (root, mount) = CreateOverlayRootWithDirectoryAndSymlinks();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x09000u);
        env.WriteCString(0x09000u, path);

        Assert.Equal(-(int)Errno.ELOOP,
            await env.Call("SysOpen", 0x09000u, (uint)(FileFlags.O_RDONLY | FileFlags.O_NOFOLLOW)));
        Assert.Equal(-(int)Errno.ELOOP,
            await env.Call("SysOpen", 0x09000u,
                (uint)(FileFlags.O_RDONLY | FileFlags.O_NOFOLLOW | FileFlags.O_DIRECTORY)));
    }

    [Theory]
    [InlineData("/link")]
    [InlineData("/indirect")]
    [InlineData("/broken")]
    public void PathWalkWithData_NoFollowFinalFileSymlink_StopsAtSymlink(string path)
    {
        var (root, mount) = CreateOverlayRootWithFileAndSymlinks();
        using var env = new TestEnv((root, mount));

        var nd = env.SyscallManager.PathWalker.PathWalkWithData(path, null, LookupFlags.NoFollow);
        Assert.False(nd.HasError);
        Assert.NotNull(nd.Path.Dentry);
        Assert.Equal(InodeType.Symlink, nd.Path.Dentry!.Inode!.Type);
    }

    [Theory]
    [InlineData("/dir-link")]
    [InlineData("/dir-chain")]
    public void PathWalkWithData_NoFollowFinalDirectorySymlink_StopsAtSymlink(string path)
    {
        var (root, mount) = CreateOverlayRootWithDirectoryAndSymlinks();
        using var env = new TestEnv((root, mount));

        var nd = env.SyscallManager.PathWalker.PathWalkWithData(path, null, LookupFlags.NoFollow);
        Assert.False(nd.HasError);
        Assert.NotNull(nd.Path.Dentry);
        Assert.Equal(InodeType.Symlink, nd.Path.Dentry!.Inode!.Type);
    }

    [Theory]
    [InlineData("/link/child")]
    [InlineData("/indirect/child")]
    public void PathWalkWithData_NoFollowStillFollowsNonFinalFileSymlink(string path)
    {
        var (root, mount) = CreateOverlayRootWithFileAndSymlinks();
        using var env = new TestEnv((root, mount));

        var nd = env.SyscallManager.PathWalker.PathWalkWithData(path, null, LookupFlags.NoFollow);
        Assert.True(nd.HasError);
        Assert.Equal(-(int)Errno.ENOTDIR, nd.ErrorCode);
    }

    [Theory]
    [InlineData("/dir-link/a")]
    [InlineData("/dir-chain/a")]
    public void PathWalkWithData_NoFollowStillFollowsNonFinalDirectorySymlink(string path)
    {
        var (root, mount) = CreateOverlayRootWithDirectoryAndSymlinks();
        using var env = new TestEnv((root, mount));

        var nd = env.SyscallManager.PathWalker.PathWalkWithData(path, null, LookupFlags.NoFollow);
        Assert.False(nd.HasError);
        Assert.NotNull(nd.Path.Dentry);
        Assert.Equal("a", nd.Path.Dentry!.Name);
    }

    [Theory]
    [InlineData("/file/", (uint)FileFlags.O_RDONLY)]
    [InlineData("/file/", (uint)FileFlags.O_WRONLY)]
    [InlineData("/file/", (uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC))]
    [InlineData("/file/", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT))]
    [InlineData("/file/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY))]
    [InlineData("/link/", (uint)FileFlags.O_RDONLY)]
    [InlineData("/link/", (uint)FileFlags.O_WRONLY)]
    [InlineData("/link/", (uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC))]
    [InlineData("/link/", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT))]
    [InlineData("/link/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY))]
    [InlineData("/indirect/", (uint)FileFlags.O_RDONLY)]
    [InlineData("/indirect/", (uint)FileFlags.O_WRONLY)]
    [InlineData("/indirect/", (uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC))]
    [InlineData("/indirect/", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT))]
    [InlineData("/indirect/", (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY))]
    public async Task Open_RegularFileAndFileSymlinkWithTrailingSlash_ReturnEnotdir(string path, uint flags)
    {
        var (root, mount) = CreateOverlayRootWithFileAndSymlinks();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x10000u);
        env.WriteCString(0x10000u, path);

        Assert.Equal(-(int)Errno.ENOTDIR, await env.Call("SysOpen", 0x10000u, flags));
    }

    [Theory]
    [InlineData("/file/child", (uint)FileFlags.O_RDONLY)]
    [InlineData("/file/child", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT))]
    [InlineData("/link/child", (uint)FileFlags.O_RDONLY)]
    [InlineData("/link/child", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT))]
    [InlineData("/indirect/child", (uint)FileFlags.O_RDONLY)]
    [InlineData("/indirect/child", (uint)(FileFlags.O_RDONLY | FileFlags.O_CREAT))]
    public async Task Open_RegularFileComponentInMiddle_ReturnEnotdir(string path, uint flags)
    {
        var (root, mount) = CreateOverlayRootWithFileAndSymlinks();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x11000u);
        env.WriteCString(0x11000u, path);

        Assert.Equal(-(int)Errno.ENOTDIR, await env.Call("SysOpen", 0x11000u, flags));
    }

    [Theory]
    [InlineData("/file/")]
    [InlineData("/link/")]
    [InlineData("/indirect/")]
    public void PathWalkWithData_FinalRegularFileWithTrailingSlash_ReturnsEnotdir(string path)
    {
        var (root, mount) = CreateOverlayRootWithFileAndSymlinks();
        using var env = new TestEnv((root, mount));

        var nd = env.SyscallManager.PathWalker.PathWalkWithData(path);
        Assert.True(nd.HasError);
        Assert.Equal(-(int)Errno.ENOTDIR, nd.ErrorCode);
    }

    [Theory]
    [InlineData("/file/child")]
    [InlineData("/link/child")]
    [InlineData("/indirect/child")]
    public void PathWalkWithData_NonDirectoryMiddleComponent_ReturnsEnotdir(string path)
    {
        var (root, mount) = CreateOverlayRootWithFileAndSymlinks();
        using var env = new TestEnv((root, mount));

        var nd = env.SyscallManager.PathWalker.PathWalkWithData(path);
        Assert.True(nd.HasError);
        Assert.Equal(-(int)Errno.ENOTDIR, nd.ErrorCode);
    }

    [Theory]
    [InlineData("/file/new")]
    [InlineData("/link/new")]
    [InlineData("/indirect/new")]
    public void PathWalkForCreate_NonDirectoryParent_ReturnsEnotdir(string path)
    {
        var (root, mount) = CreateOverlayRootWithFileAndSymlinks();
        using var env = new TestEnv((root, mount));

        var (parent, name, error) = env.SyscallManager.PathWalkForCreate(path);
        Assert.False(parent.IsValid);
        Assert.Equal("new", name);
        Assert.Equal(-(int)Errno.ENOTDIR, error);
    }

    [Theory]
    [InlineData("/file", -(int)Errno.EINVAL, null)]
    [InlineData("/dir", -(int)Errno.EINVAL, null)]
    [InlineData("/missing", -(int)Errno.ENOENT, null)]
    [InlineData("/link", 4, "file")]
    [InlineData("/indirect", 4, "link")]
    [InlineData("/broken", 15, "/missing-target")]
    [InlineData("/dir-link", 3, "dir")]
    [InlineData("/dir-chain", 8, "dir-link")]
    public async Task Readlink_PathwalkSymlinkSemantics_MatchExpected(string path, int expectedRc, string? expected)
    {
        var factory = path.StartsWith("/dir", StringComparison.Ordinal)
            ? CreateOverlayRootWithDirectoryAndSymlinks()
            : CreateOverlayRootWithFileAndSymlinks();
        using var env = new TestEnv(factory);
        env.MapUserPage(0x12000u);
        env.MapUserPage(0x13000u);
        env.WriteCString(0x12000u, path);

        var rc = await env.Call("SysReadlink", 0x12000u, 0x13000u, 64);
        Assert.Equal(expectedRc, rc);
        if (expected != null)
            Assert.Equal(expected, Encoding.ASCII.GetString(env.ReadBytes(0x13000u, rc)));
    }

    [Fact]
    public async Task Unlink_DirectFileSymlink_RemovesLinkNotTarget()
    {
        var (root, mount) = CreateOverlayRootWithFileAndSymlinks();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x14000u);
        env.MapUserPage(0x15000u);
        env.WriteCString(0x14000u, "/link");
        env.WriteCString(0x15000u, "/file");

        Assert.Equal(0, await env.Call("SysUnlink", 0x14000u));
        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysOpen", 0x14000u, (uint)FileFlags.O_RDONLY));

        var fd = await env.Call("SysOpen", 0x15000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task Unlink_IndirectFileSymlink_RemovesOnlyOuterLink()
    {
        var (root, mount) = CreateOverlayRootWithFileAndSymlinks();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x16000u);
        env.MapUserPage(0x17000u);
        env.MapUserPage(0x18000u);
        env.WriteCString(0x16000u, "/indirect");
        env.WriteCString(0x17000u, "/link");
        env.WriteCString(0x18000u, "/file");

        Assert.Equal(0, await env.Call("SysUnlink", 0x16000u));
        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysOpen", 0x16000u, (uint)FileFlags.O_RDONLY));
        Assert.True(await env.Call("SysOpen", 0x17000u, (uint)FileFlags.O_RDONLY) >= 0);
        Assert.True(await env.Call("SysOpen", 0x18000u, (uint)FileFlags.O_RDONLY) >= 0);
    }

    [Theory]
    [InlineData("/file/")]
    [InlineData("/link/")]
    [InlineData("/indirect/")]
    [InlineData("/broken/")]
    public async Task Rmdir_FileOrFileSymlinkPath_ReturnsEnotdir(string path)
    {
        var (root, mount) = CreateOverlayRootWithFileAndSymlinks();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x19000u);
        env.WriteCString(0x19000u, path);

        Assert.Equal(-(int)Errno.ENOTDIR, await env.Call("SysRmdir", 0x19000u));
    }

    [Theory]
    [InlineData("/dir-link/")]
    [InlineData("/dir-chain/")]
    public async Task Rmdir_DirectorySymlinkPath_ReturnsEnotdir(string path)
    {
        var (root, mount) = CreateOverlayRootWithDirectoryAndSymlinks();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x1A000u);
        env.MapUserPage(0x1B000u);
        env.WriteCString(0x1A000u, path);
        env.WriteCString(0x1B000u, "/dir/a");

        Assert.Equal(-(int)Errno.ENOTDIR, await env.Call("SysRmdir", 0x1A000u));
        var fd = await env.Call("SysOpen", 0x1B000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
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
                var sb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "pathwalk-tmpfs", null);
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
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]",
                Engine);
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

    private static (Dentry Root, Mount Mount) CreateOverlayRootWithFileAndSymlinks()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "file-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "file-upper", null);

        var lowerRoot = lowerSb.Root;
        var file = new Dentry("file", null, lowerRoot, lowerSb);
        lowerRoot.Inode!.Create(file, 0x1A4, 0, 0);
        var writer = new LinuxFile(file, FileFlags.O_WRONLY, null!);
        try
        {
            Assert.Equal(1, file.Inode!.WriteFromHost(null, writer, "x"u8.ToArray(), 0));
        }
        finally
        {
            writer.Close();
        }

        var direct = new Dentry("link", null, lowerRoot, lowerSb);
        lowerRoot.Inode.Symlink(direct, "file", 0, 0);
        var indirect = new Dentry("indirect", null, lowerRoot, lowerSb);
        lowerRoot.Inode.Symlink(indirect, "link", 0, 0);
        var broken = new Dentry("broken", null, lowerRoot, lowerSb);
        lowerRoot.Inode.Symlink(broken, "/missing-target", 0, 0);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "file-overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        var mount = new Mount(overlaySb, overlaySb.Root)
        {
            Source = "overlay",
            FsType = "overlay",
            Options = "rw"
        };

        return (overlaySb.Root, mount);
    }

    private static (Dentry Root, Mount Mount) CreateOverlayRootWithDirectoryAndSymlinks()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "dir-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "dir-upper", null);

        var lowerRoot = lowerSb.Root;
        var dir = new Dentry("dir", null, lowerRoot, lowerSb);
        lowerRoot.Inode!.Mkdir(dir, 0x1ED, 0, 0);

        var child = new Dentry("a", null, dir, lowerSb);
        dir.Inode!.Create(child, 0x1A4, 0, 0);
        var writer = new LinuxFile(child, FileFlags.O_WRONLY, null!);
        try
        {
            Assert.Equal(1, child.Inode!.WriteFromHost(null, writer, "x"u8.ToArray(), 0));
        }
        finally
        {
            writer.Close();
        }

        var direct = new Dentry("dir-link", null, lowerRoot, lowerSb);
        lowerRoot.Inode.Symlink(direct, "dir", 0, 0);
        var indirect = new Dentry("dir-chain", null, lowerRoot, lowerSb);
        lowerRoot.Inode.Symlink(indirect, "dir-link", 0, 0);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "dir-overlay",
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
