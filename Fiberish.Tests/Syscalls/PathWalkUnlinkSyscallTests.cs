using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class PathWalkUnlinkSyscallTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unlink_File_RemovesEntry(bool useUnlinkAt)
    {
        var (root, mount) = CreateOverlayRootWithMixedPaths();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x10000u);
        env.WriteCString(0x10000u, "/file");

        Assert.Equal(0, await Unlink(env, 0x10000u, useUnlinkAt));
        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysOpen", 0x10000u, (uint)FileFlags.O_RDONLY));
        Assert.Equal(-(int)Errno.ENOENT, await Unlink(env, 0x10000u, useUnlinkAt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unlink_DirectFileSymlink_RemovesLinkNotTarget(bool useUnlinkAt)
    {
        var (root, mount) = CreateOverlayRootWithMixedPaths();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x11000u);
        env.MapUserPage(0x12000u);
        env.WriteCString(0x11000u, "/link");
        env.WriteCString(0x12000u, "/file");

        Assert.Equal(0, await Unlink(env, 0x11000u, useUnlinkAt));
        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysOpen", 0x11000u, (uint)FileFlags.O_RDONLY));

        var fd = await env.Call("SysOpen", 0x12000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unlink_IndirectFileSymlink_RemovesOnlyOuterLink(bool useUnlinkAt)
    {
        var (root, mount) = CreateOverlayRootWithMixedPaths();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x13000u);
        env.MapUserPage(0x14000u);
        env.MapUserPage(0x15000u);
        env.WriteCString(0x13000u, "/indirect");
        env.WriteCString(0x14000u, "/link");
        env.WriteCString(0x15000u, "/file");

        Assert.Equal(0, await Unlink(env, 0x13000u, useUnlinkAt));
        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysOpen", 0x13000u, (uint)FileFlags.O_RDONLY));

        var directFd = await env.Call("SysOpen", 0x14000u, (uint)FileFlags.O_RDONLY);
        Assert.True(directFd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)directFd));

        var fileFd = await env.Call("SysOpen", 0x15000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fileFd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fileFd));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unlink_DirectDirectorySymlink_WithoutTrailingSlash_RemovesLinkNotTarget(bool useUnlinkAt)
    {
        var (root, mount) = CreateOverlayRootWithMixedPaths();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x16000u);
        env.MapUserPage(0x17000u);
        env.MapUserPage(0x18000u);
        env.WriteCString(0x16000u, "/dir-link");
        env.WriteCString(0x17000u, "/dir-link/a");
        env.WriteCString(0x18000u, "/dir/a");

        Assert.Equal(0, await Unlink(env, 0x16000u, useUnlinkAt));
        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysOpen", 0x17000u, (uint)FileFlags.O_RDONLY));

        var fd = await env.Call("SysOpen", 0x18000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unlink_IndirectDirectorySymlink_WithoutTrailingSlash_RemovesOnlyOuterLink(bool useUnlinkAt)
    {
        var (root, mount) = CreateOverlayRootWithMixedPaths();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x19000u);
        env.MapUserPage(0x1A000u);
        env.MapUserPage(0x1B000u);
        env.MapUserPage(0x1C000u);
        env.WriteCString(0x19000u, "/dir-chain");
        env.WriteCString(0x1A000u, "/dir-chain/a");
        env.WriteCString(0x1B000u, "/dir-link/a");
        env.WriteCString(0x1C000u, "/dir/a");

        Assert.Equal(0, await Unlink(env, 0x19000u, useUnlinkAt));
        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysOpen", 0x1A000u, (uint)FileFlags.O_RDONLY));

        var directFd = await env.Call("SysOpen", 0x1B000u, (uint)FileFlags.O_RDONLY);
        Assert.True(directFd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)directFd));

        var dirFd = await env.Call("SysOpen", 0x1C000u, (uint)FileFlags.O_RDONLY);
        Assert.True(dirFd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)dirFd));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unlink_Directory_ReturnsEisdir(bool useUnlinkAt)
    {
        var (root, mount) = CreateOverlayRootWithMixedPaths();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x1D000u);
        env.MapUserPage(0x1E000u);
        env.WriteCString(0x1D000u, "/dir");
        env.WriteCString(0x1E000u, "/dir/a");

        Assert.Equal(-(int)Errno.EISDIR, await Unlink(env, 0x1D000u, useUnlinkAt));

        var fd = await env.Call("SysOpen", 0x1E000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unlink_BrokenSymlink_RemovesLink(bool useUnlinkAt)
    {
        var (root, mount) = CreateOverlayRootWithMixedPaths();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x1F000u);
        env.WriteCString(0x1F000u, "/broken");

        Assert.Equal(0, await Unlink(env, 0x1F000u, useUnlinkAt));
        Assert.Equal(-(int)Errno.ENOENT, await Unlink(env, 0x1F000u, useUnlinkAt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unlink_MissingPath_ReturnsEnoent(bool useUnlinkAt)
    {
        var (root, mount) = CreateOverlayRootWithMixedPaths();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x20000u);
        env.WriteCString(0x20000u, "/missing");

        Assert.Equal(-(int)Errno.ENOENT, await Unlink(env, 0x20000u, useUnlinkAt));
        Assert.Equal(-(int)Errno.ENOENT, await Unlink(env, 0x20000u, useUnlinkAt));
    }

    [Theory]
    [InlineData("/file/")]
    [InlineData("/link/")]
    [InlineData("/indirect/")]
    [InlineData("/broken/")]
    public async Task Unlink_TrailingSlashOnFileLikePath_ReturnsEnotdir_AndPreservesObjects(string path)
    {
        var (root, mount) = CreateOverlayRootWithMixedPaths();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x21000u);
        env.MapUserPage(0x22000u);
        env.WriteCString(0x21000u, path);
        env.WriteCString(0x22000u, "/file");

        Assert.Equal(-(int)Errno.ENOTDIR, await env.Call("SysUnlink", 0x21000u));

        var fd = await env.Call("SysOpen", 0x22000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Theory]
    [InlineData("/dir-link/")]
    [InlineData("/dir-chain/")]
    public async Task Unlink_TrailingSlashOnDirectorySymlink_ReturnsEnotdir_AndPreservesLink(string path)
    {
        var (root, mount) = CreateOverlayRootWithMixedPaths();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x23000u);
        env.MapUserPage(0x24000u);
        env.WriteCString(0x23000u, path);
        env.WriteCString(0x24000u, "/dir/a");

        Assert.Equal(-(int)Errno.ENOTDIR, await env.Call("SysUnlink", 0x23000u));

        var fd = await env.Call("SysOpen", 0x24000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Theory]
    [InlineData("/link")]
    [InlineData("/indirect")]
    [InlineData("/broken")]
    [InlineData("/dir-link")]
    [InlineData("/dir-chain")]
    public void PathWalk_None_WithoutTrailingSlash_StopsAtSymlink(string path)
    {
        var (root, mount) = CreateOverlayRootWithMixedPaths();
        using var env = new TestEnv((root, mount));

        var nd = env.SyscallManager.PathWalker.PathWalkWithData(path, null, LookupFlags.None);
        Assert.False(nd.HasError);
        Assert.NotNull(nd.Path.Dentry);
        Assert.Equal(InodeType.Symlink, nd.Path.Dentry!.Inode!.Type);
    }

    [Theory]
    [InlineData("/link/")]
    [InlineData("/indirect/")]
    [InlineData("/broken/")]
    [InlineData("/dir-link/")]
    [InlineData("/dir-chain/")]
    public void PathWalk_None_WithTrailingSlashOnSymlink_ReturnsEnotdir(string path)
    {
        var (root, mount) = CreateOverlayRootWithMixedPaths();
        using var env = new TestEnv((root, mount));

        var nd = env.SyscallManager.PathWalker.PathWalkWithData(path, null, LookupFlags.None);
        Assert.True(nd.HasError);
        Assert.Equal(-(int)Errno.ENOTDIR, nd.ErrorCode);
    }

    private static async Task<int> Unlink(TestEnv env, uint pathAddr, bool useUnlinkAt)
    {
        return useUnlinkAt
            ? await env.Call("SysUnlinkAt", LinuxConstants.AT_FDCWD, pathAddr, 0)
            : await env.Call("SysUnlink", pathAddr);
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
                var sb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "pathwalk-unlink-tmpfs", null);
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

        public async ValueTask<int> Call(string methodName, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0,
            uint a5 = 0, uint a6 = 0)
        {
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var task = (ValueTask<int>)method!.Invoke(SyscallManager, [Engine, a1, a2, a3, a4, a5, a6])!;
            return await task;
        }
    }

    private static (Dentry Root, Mount Mount) CreateOverlayRootWithMixedPaths()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "unlink-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "unlink-upper", null);

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
            Assert.Equal(0, child.Inode!.WriteFromHost(null, childWriter, [], 0));
        }
        finally
        {
            childWriter.Close();
        }

        var link = new Dentry("link", null, lowerRoot, lowerSb);
        lowerRoot.Inode.Symlink(link, "file"u8.ToArray(), 0, 0);
        var indirect = new Dentry("indirect", null, lowerRoot, lowerSb);
        lowerRoot.Inode.Symlink(indirect, "link"u8.ToArray(), 0, 0);
        var broken = new Dentry("broken", null, lowerRoot, lowerSb);
        lowerRoot.Inode.Symlink(broken, "/missing-target"u8.ToArray(), 0, 0);

        var dirLink = new Dentry("dir-link", null, lowerRoot, lowerSb);
        lowerRoot.Inode.Symlink(dirLink, "dir"u8.ToArray(), 0, 0);
        var dirChain = new Dentry("dir-chain", null, lowerRoot, lowerSb);
        lowerRoot.Inode.Symlink(dirChain, "dir-link"u8.ToArray(), 0, 0);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "unlink-overlay",
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
