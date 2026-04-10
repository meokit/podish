using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class PathWalkMknodSyscallTests
{
    private const uint SIfReg = 0x8000;
    private const uint SIfChr = 0x2000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mknod_MissingRegularFile_CreatesThenReturnsEexist(bool useMknodat)
    {
        var (root, mount) = CreateOverlayRootWithFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x10000u);
        env.WriteCString(0x10000u, "/newfile");

        Assert.Equal(0, await Mknod(env, 0x10000u, SIfReg | 0x1A4, 0, useMknodat));
        Assert.Equal(-(int)Errno.EEXIST, await Mknod(env, 0x10000u, SIfReg | 0x1A4, 0, useMknodat));

        var fd = await env.Call("SysOpen", 0x10000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mknod_InsideLowerDirectory_CreatesNodeAndPreservesSibling(bool useMknodat)
    {
        var (root, mount) = CreateOverlayRootWithFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x11000u);
        env.MapUserPage(0x12000u);
        env.MapUserPage(0x13000u);
        env.WriteCString(0x11000u, "/nonempty/newchr");
        env.WriteCString(0x12000u, "/nonempty/newchr");
        env.WriteCString(0x13000u, "/nonempty/a");

        const uint rdev = (1u << 8) | 7u;
        Assert.Equal(0, await Mknod(env, 0x11000u, SIfChr | 0x180, rdev, useMknodat));
        Assert.Equal(-(int)Errno.EEXIST, await Mknod(env, 0x11000u, SIfChr | 0x180, rdev, useMknodat));

        var loc = env.SyscallManager.PathWalkWithFlags("/nonempty/newchr", LookupFlags.None);
        Assert.True(loc.IsValid);
        Assert.Equal(InodeType.CharDev, loc.Dentry!.Inode!.Type);
        Assert.Equal(rdev, loc.Dentry.Inode.Rdev);

        var siblingFd = await env.Call("SysOpen", 0x13000u, (uint)FileFlags.O_RDONLY);
        Assert.True(siblingFd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)siblingFd));

        var createdFd = await env.Call("SysOpen", 0x12000u, (uint)FileFlags.O_RDONLY);
        Assert.True(createdFd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)createdFd));
    }

    [Theory]
    [InlineData("/file", false)]
    [InlineData("/file", true)]
    [InlineData("/empty", false)]
    [InlineData("/empty", true)]
    [InlineData("/nonempty", false)]
    [InlineData("/nonempty", true)]
    [InlineData("/link", false)]
    [InlineData("/link", true)]
    [InlineData("/indirect", false)]
    [InlineData("/indirect", true)]
    [InlineData("/dir-link", false)]
    [InlineData("/dir-link", true)]
    [InlineData("/dir-chain", false)]
    [InlineData("/dir-chain", true)]
    [InlineData("/broken", false)]
    [InlineData("/broken", true)]
    public async Task Mknod_OverExistingEntries_ReturnsEexist(string path, bool useMknodat)
    {
        var (root, mount) = CreateOverlayRootWithFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x14000u);
        env.MapUserPage(0x15000u);
        env.WriteCString(0x14000u, path);
        env.WriteCString(0x15000u, "/nonempty/a");

        Assert.Equal(-(int)Errno.EEXIST, await Mknod(env, 0x14000u, SIfReg | 0x1A4, 0, useMknodat));

        if (path.StartsWith("/dir", StringComparison.Ordinal))
        {
            var fd = await env.Call("SysOpen", 0x15000u, (uint)FileFlags.O_RDONLY);
            Assert.True(fd >= 0);
            Assert.Equal(0, await env.Call("SysClose", (uint)fd));
        }
    }

    [Theory]
    [InlineData("/missing-parent/sub", -(int)Errno.ENOENT)]
    [InlineData("/file/sub", -(int)Errno.ENOTDIR)]
    [InlineData("/link/sub", -(int)Errno.ENOTDIR)]
    [InlineData("/indirect/sub", -(int)Errno.ENOTDIR)]
    [InlineData("/broken/sub", -(int)Errno.ENOENT)]
    public async Task Mknod_PathwalkBoundaryPaths_ReturnExpectedErrors(string path, int errno)
    {
        var (root, mount) = CreateOverlayRootWithFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x16000u);
        env.WriteCString(0x16000u, path);

        Assert.Equal(errno, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x16000u, SIfReg | 0x1A4, 0));
    }

    [Theory]
    [InlineData("/dir-link/sub", false)]
    [InlineData("/dir-link/sub", true)]
    [InlineData("/dir-chain/sub", false)]
    [InlineData("/dir-chain/sub", true)]
    public async Task Mknod_UnderDirectorySymlink_CreatesInSymlinkTarget(string path, bool useMknodat)
    {
        var (root, mount) = CreateOverlayRootWithFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x17000u);
        env.MapUserPage(0x18000u);
        env.MapUserPage(0x19000u);
        env.WriteCString(0x17000u, path);
        env.WriteCString(0x18000u, "/nonempty/sub");
        env.WriteCString(0x19000u, "/dir-link/sub");

        Assert.Equal(0, await Mknod(env, 0x17000u, SIfReg | 0x1A4, 0, useMknodat));
        Assert.Equal(-(int)Errno.EEXIST, await Mknod(env, 0x17000u, SIfReg | 0x1A4, 0, useMknodat));

        var targetFd = await env.Call("SysOpen", 0x18000u, (uint)FileFlags.O_RDONLY);
        Assert.True(targetFd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)targetFd));

        if (path.StartsWith("/dir-link", StringComparison.Ordinal))
        {
            var viaLinkFd = await env.Call("SysOpen", 0x19000u, (uint)FileFlags.O_RDONLY);
            Assert.True(viaLinkFd >= 0);
            Assert.Equal(0, await env.Call("SysClose", (uint)viaLinkFd));
        }
    }

    private static async Task<int> Mknod(TestEnv env, uint pathAddr, uint mode, uint dev, bool useMknodat)
    {
        return useMknodat
            ? await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, pathAddr, mode, dev)
            : await env.Call("SysMknod", pathAddr, mode, dev);
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
                var sb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "pathwalk-mknod-tmpfs", null);
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

        public async ValueTask<int> Call(string methodName, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0,
            uint a5 = 0, uint a6 = 0)
        {
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var task = (ValueTask<int>)method!.Invoke(SyscallManager, [Engine, a1, a2, a3, a4, a5, a6])!;
            return await task;
        }
    }

    private static (Dentry Root, Mount Mount) CreateOverlayRootWithFixtures()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "mknod-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "mknod-upper", null);

        var lowerRoot = lowerSb.Root;

        var file = new Dentry("file", null, lowerRoot, lowerSb);
        lowerRoot.Inode!.Create(file, 0x1A4, 0, 0);

        var empty = new Dentry("empty", null, lowerRoot, lowerSb);
        lowerRoot.Inode.Mkdir(empty, 0x1ED, 0, 0);

        var nonEmpty = new Dentry("nonempty", null, lowerRoot, lowerSb);
        lowerRoot.Inode.Mkdir(nonEmpty, 0x1ED, 0, 0);
        var child = new Dentry("a", null, nonEmpty, lowerSb);
        nonEmpty.Inode!.Create(child, 0x1A4, 0, 0);

        lowerRoot.Inode.Symlink(new Dentry("link", null, lowerRoot, lowerSb), "file"u8.ToArray(), 0, 0);
        lowerRoot.Inode.Symlink(new Dentry("indirect", null, lowerRoot, lowerSb), "link"u8.ToArray(), 0, 0);
        lowerRoot.Inode.Symlink(new Dentry("broken", null, lowerRoot, lowerSb), "/missing-target"u8.ToArray(), 0, 0);
        lowerRoot.Inode.Symlink(new Dentry("dir-link", null, lowerRoot, lowerSb), "nonempty"u8.ToArray(), 0, 0);
        lowerRoot.Inode.Symlink(new Dentry("dir-chain", null, lowerRoot, lowerSb), "dir-link"u8.ToArray(), 0, 0);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "mknod-overlay",
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
