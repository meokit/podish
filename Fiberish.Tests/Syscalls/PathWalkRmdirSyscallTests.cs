using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class PathWalkRmdirSyscallTests
{
    private const uint AtRemovedir = 0x200;

    [Theory]
    [InlineData("/missing")]
    [InlineData("/missing/")]
    [InlineData("/missing/sub")]
    [InlineData("/missing/sub/")]
    public async Task Rmdir_MissingPaths_ReturnEnoent(string path)
    {
        var (root, mount) = CreateOverlayRootWithRmdirFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x10000u);
        env.WriteCString(0x10000u, path);

        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysRmdir", 0x10000u));
    }

    [Theory]
    [InlineData("/missing", -(int)Errno.ENOENT)]
    [InlineData("/missing/", -(int)Errno.ENOENT)]
    [InlineData("/missing/sub", -(int)Errno.ENOENT)]
    [InlineData("/missing/sub/", -(int)Errno.ENOENT)]
    [InlineData("/file/", -(int)Errno.ENOTDIR)]
    [InlineData("/file/sub", -(int)Errno.ENOTDIR)]
    [InlineData("/file/sub/", -(int)Errno.ENOTDIR)]
    [InlineData("/link/", -(int)Errno.ENOTDIR)]
    [InlineData("/indirect/", -(int)Errno.ENOTDIR)]
    [InlineData("/broken/", -(int)Errno.ENOTDIR)]
    [InlineData("/dir-link/", -(int)Errno.ENOTDIR)]
    [InlineData("/dir-chain/", -(int)Errno.ENOTDIR)]
    public async Task UnlinkAtRemovedir_PathwalkBoundaries_MatchExpected(string path, int expectedErr)
    {
        var (root, mount) = CreateOverlayRootWithRmdirFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x11000u);
        env.WriteCString(0x11000u, path);

        Assert.Equal(expectedErr,
            await env.Call("SysUnlinkAt", LinuxConstants.AT_FDCWD, 0x11000u, AtRemovedir));
    }

    [Theory]
    [InlineData("/file/")]
    [InlineData("/file/sub")]
    [InlineData("/file/sub/")]
    [InlineData("/link/")]
    [InlineData("/indirect/")]
    [InlineData("/broken/")]
    public async Task Rmdir_FileAndFileSymlinkPaths_ReturnEnotdir(string path)
    {
        var (root, mount) = CreateOverlayRootWithRmdirFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x12000u);
        env.MapUserPage(0x13000u);
        env.WriteCString(0x12000u, path);
        env.WriteCString(0x13000u, "/file");

        Assert.Equal(-(int)Errno.ENOTDIR, await env.Call("SysRmdir", 0x12000u));
        var fd = await env.Call("SysOpen", 0x13000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Theory]
    [InlineData("/dir-link/")]
    [InlineData("/dir-chain/")]
    public async Task Rmdir_DirectorySymlinkPaths_ReturnEnotdir_AndPreserveTarget(string path)
    {
        var (root, mount) = CreateOverlayRootWithRmdirFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x14000u);
        env.MapUserPage(0x15000u);
        env.WriteCString(0x14000u, path);
        env.WriteCString(0x15000u, "/nonempty/a");

        Assert.Equal(-(int)Errno.ENOTDIR, await env.Call("SysRmdir", 0x14000u));
        var fd = await env.Call("SysOpen", 0x15000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Fact]
    public async Task Rmdir_EmptyDirectory_SucceedsThenDisappears()
    {
        var (root, mount) = CreateOverlayRootWithRmdirFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x16000u);
        env.MapUserPage(0x17000u);
        env.MapUserPage(0x18000u);
        env.WriteCString(0x16000u, "/empty/");
        env.WriteCString(0x17000u, "/empty/");
        env.WriteCString(0x18000u, "/empty/sub");

        Assert.Equal(0, await env.Call("SysRmdir", 0x16000u));
        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysRmdir", 0x17000u));
        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysRmdir", 0x18000u));
    }

    [Fact]
    public async Task UnlinkAtRemovedir_EmptyDirectory_SucceedsThenDisappears()
    {
        var (root, mount) = CreateOverlayRootWithRmdirFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x19000u);
        env.MapUserPage(0x1A000u);
        env.WriteCString(0x19000u, "/empty/");
        env.WriteCString(0x1A000u, "/empty/");

        Assert.Equal(0,
            await env.Call("SysUnlinkAt", LinuxConstants.AT_FDCWD, 0x19000u, AtRemovedir));
        Assert.Equal(-(int)Errno.ENOENT,
            await env.Call("SysUnlinkAt", LinuxConstants.AT_FDCWD, 0x1A000u, AtRemovedir));
    }

    [Theory]
    [InlineData("/nonempty/")]
    [InlineData("/nonempty/pop/")]
    public async Task Rmdir_NonEmptyDirectory_ReturnsEnotempty(string path)
    {
        var (root, mount) = CreateOverlayRootWithRmdirFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x1B000u);
        env.MapUserPage(0x1C000u);
        env.WriteCString(0x1B000u, path);
        env.WriteCString(0x1C000u, "/nonempty/a");

        Assert.Equal(-(int)Errno.ENOTEMPTY, await env.Call("SysRmdir", 0x1B000u));
        var fd = await env.Call("SysOpen", 0x1C000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    [Theory]
    [InlineData("/nonempty/")]
    [InlineData("/nonempty/pop/")]
    public async Task UnlinkAtRemovedir_NonEmptyDirectory_ReturnsEnotempty(string path)
    {
        var (root, mount) = CreateOverlayRootWithRmdirFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x1D000u);
        env.WriteCString(0x1D000u, path);

        Assert.Equal(-(int)Errno.ENOTEMPTY,
            await env.Call("SysUnlinkAt", LinuxConstants.AT_FDCWD, 0x1D000u, AtRemovedir));
    }

    [Theory]
    [InlineData("/link/")]
    [InlineData("/dir-link/")]
    [InlineData("/broken/")]
    public void PathWalkForCreate_TrailingSlashSymlinkParent_ResolvesParentAndTerminalName(string path)
    {
        var (root, mount) = CreateOverlayRootWithRmdirFixtures();
        using var env = new TestEnv((root, mount));

        var (parent, name, error) = env.SyscallManager.PathWalkForCreate(path);
        Assert.True(parent.IsValid);
        Assert.Equal(env.SyscallManager.Root.Dentry, parent.Dentry);
        Assert.Equal(path.Trim('/'), name.ToString());
        Assert.Equal(0, error);
    }

    [Theory]
    [InlineData("/file/sub")]
    [InlineData("/file/sub/")]
    public async Task Rmdir_SubdirectoryOfFile_ReturnsEnotdir(string path)
    {
        var (root, mount) = CreateOverlayRootWithRmdirFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x1E000u);
        env.WriteCString(0x1E000u, path);

        Assert.Equal(-(int)Errno.ENOTDIR, await env.Call("SysRmdir", 0x1E000u));
    }

    [Fact]
    public async Task Rmdir_AfterMkdirOverUnlinkedFile_Succeeds()
    {
        var (root, mount) = CreateOverlayRootWithRmdirFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x1F000u);
        env.WriteCString(0x1F000u, "/nonempty/a");

        // Unlink the file first
        Assert.Equal(0, await env.Call("SysUnlink", 0x1F000u));
        
        // Mkdir over the unlinked file (whiteout)
        Assert.Equal(0, await env.Call("SysMkdir", 0x1F000u, 0x1ED));
        
        // Rmdir the newly created directory
        Assert.Equal(0, await env.Call("SysRmdir", 0x1F000u));
        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysRmdir", 0x1F000u));
    }

    [Fact]
    public async Task Rmdir_OpaqueDirectory_Succeeds()
    {
        var (root, mount) = CreateOverlayRootWithRmdirFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x20000u);
        env.MapUserPage(0x21000u);
        env.WriteCString(0x20000u, "/empty");
        env.WriteCString(0x21000u, "/empty/newfile");

        // Rmdir the empty lower directory
        Assert.Equal(0, await env.Call("SysRmdir", 0x20000u));
        
        // Recreate it (it's now opaque upper-only)
        Assert.Equal(0, await env.Call("SysMkdir", 0x20000u, 0x1ED));
        
        // Populate it
        var fd = await env.Call("SysOpen", 0x21000u, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT));
        Assert.True(fd >= 0);
        await env.Call("SysClose", (uint)fd);
        
        // Try rmdir (should fail because not empty)
        Assert.Equal(-(int)Errno.ENOTEMPTY, await env.Call("SysRmdir", 0x20000u));
        
        // Unlink child and rmdir
        Assert.Equal(0, await env.Call("SysUnlink", 0x21000u));
        Assert.Equal(0, await env.Call("SysRmdir", 0x20000u));
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly TestRuntimeFactory _runtime = new();

        public TestEnv((Dentry Root, Mount Mount)? rootOverride = null)
        {
            Engine = _runtime.CreateEngine();
            Vma = _runtime.CreateAddressSpace();
            Engine.PageFaultResolver = (addr, isWrite) => Vma.HandleFault(addr, isWrite, Engine);
            SyscallManager = new SyscallManager(Engine, Vma, 0);

            if (rootOverride is { } root)
            {
                SyscallManager.InitializeRoot(root.Root, root.Mount);
            }
            else
            {
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var sb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "pathwalk-rmdir-tmpfs", null);
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

    private static (Dentry Root, Mount Mount) CreateOverlayRootWithRmdirFixtures()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "rmdir-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "rmdir-upper", null);

        var lowerRoot = lowerSb.Root;

        var file = new Dentry(FsName.FromString("file"), null, lowerRoot, lowerSb);
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

        var empty = new Dentry(FsName.FromString("empty"), null, lowerRoot, lowerSb);
        lowerRoot.Inode.Mkdir(empty, 0x1ED, 0, 0);

        var nonEmpty = new Dentry(FsName.FromString("nonempty"), null, lowerRoot, lowerSb);
        lowerRoot.Inode.Mkdir(nonEmpty, 0x1ED, 0, 0);
        var child = new Dentry(FsName.FromString("a"), null, nonEmpty, lowerSb);
        nonEmpty.Inode!.Create(child, 0x1A4, 0, 0);
        var childWriter = new LinuxFile(child, FileFlags.O_WRONLY, null!);
        try
        {
            Assert.Equal(1, child.Inode!.WriteFromHost(null, childWriter, "a"u8.ToArray(), 0));
        }
        finally
        {
            childWriter.Close();
        }

        var pop = new Dentry(FsName.FromString("pop"), null, nonEmpty, lowerSb);
        nonEmpty.Inode.Mkdir(pop, 0x1ED, 0, 0);
        var popChild = new Dentry(FsName.FromString("b"), null, pop, lowerSb);
        pop.Inode!.Create(popChild, 0x1A4, 0, 0);
        var popWriter = new LinuxFile(popChild, FileFlags.O_WRONLY, null!);
        try
        {
            Assert.Equal(1, popChild.Inode!.WriteFromHost(null, popWriter, "b"u8.ToArray(), 0));
        }
        finally
        {
            popWriter.Close();
        }

        var link = new Dentry(FsName.FromString("link"), null, lowerRoot, lowerSb);
        lowerRoot.Inode.Symlink(link, "file"u8.ToArray(), 0, 0);
        var indirect = new Dentry(FsName.FromString("indirect"), null, lowerRoot, lowerSb);
        lowerRoot.Inode.Symlink(indirect, "link"u8.ToArray(), 0, 0);
        var broken = new Dentry(FsName.FromString("broken"), null, lowerRoot, lowerSb);
        lowerRoot.Inode.Symlink(broken, "/missing-target"u8.ToArray(), 0, 0);
        var dirLink = new Dentry(FsName.FromString("dir-link"), null, lowerRoot, lowerSb);
        lowerRoot.Inode.Symlink(dirLink, "nonempty"u8.ToArray(), 0, 0);
        var dirChain = new Dentry(FsName.FromString("dir-chain"), null, lowerRoot, lowerSb);
        lowerRoot.Inode.Symlink(dirChain, "dir-link"u8.ToArray(), 0, 0);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "rmdir-overlay",
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
