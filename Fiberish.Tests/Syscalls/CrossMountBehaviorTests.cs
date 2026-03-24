using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class CrossMountBehaviorTests
{
    [Fact]
    public async Task Rename_AcrossMounts_ReturnsExdev_AndPreservesSource()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x10000);
        env.MapUserPage(0x11000);
        env.WriteCString(0x10000, "/mnt/source.txt");
        env.WriteCString(0x11000, "/target.txt");

        env.CreateFile("/mnt/source.txt");

        var rc = await env.Call("SysRename", 0x10000, 0x11000);
        Assert.Equal(-(int)Errno.EXDEV, rc);

        Assert.True(env.SyscallManager.PathWalkWithFlags("/mnt/source.txt", LookupFlags.None).IsValid);
        Assert.False(env.SyscallManager.PathWalkWithFlags("/target.txt", LookupFlags.None).IsValid);
    }

    [Fact]
    public async Task Link_AcrossMounts_ReturnsExdev_AndDoesNotCreateTarget()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x12000);
        env.MapUserPage(0x13000);
        env.WriteCString(0x12000, "/mnt/source.txt");
        env.WriteCString(0x13000, "/linked.txt");

        env.CreateFile("/mnt/source.txt");

        var rc = await env.Call("SysLink", 0x12000, 0x13000);
        Assert.Equal(-(int)Errno.EXDEV, rc);

        Assert.True(env.SyscallManager.PathWalkWithFlags("/mnt/source.txt", LookupFlags.None).IsValid);
        Assert.False(env.SyscallManager.PathWalkWithFlags("/linked.txt", LookupFlags.None).IsValid);
    }

    [Fact]
    public async Task RenameAt_AcrossMountsWithDirFds_ReturnsExdev_AndPreservesSource()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x14000);
        env.MapUserPage(0x15000);
        env.WriteCString(0x14000, "source.txt");
        env.WriteCString(0x15000, "renamed.txt");

        env.CreateFile("/mnt/source.txt");
        var oldDirFd = await env.OpenDirectory("/mnt");
        var newDirFd = await env.OpenDirectory("/");

        var rc = await env.Call("SysRenameAt", (uint)oldDirFd, 0x14000, (uint)newDirFd, 0x15000);
        Assert.Equal(-(int)Errno.EXDEV, rc);

        Assert.True(env.SyscallManager.PathWalkWithFlags("/mnt/source.txt", LookupFlags.None).IsValid);
        Assert.False(env.SyscallManager.PathWalkWithFlags("/renamed.txt", LookupFlags.None).IsValid);
    }

    [Fact]
    public async Task LinkAt_AcrossMountsWithDirFds_ReturnsExdev_AndDoesNotCreateTarget()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x16000);
        env.MapUserPage(0x17000);
        env.WriteCString(0x16000, "source.txt");
        env.WriteCString(0x17000, "linked.txt");

        env.CreateFile("/mnt/source.txt");
        var oldDirFd = await env.OpenDirectory("/mnt");
        var newDirFd = await env.OpenDirectory("/");

        var rc = await env.Call("SysLinkat", (uint)oldDirFd, 0x16000, (uint)newDirFd, 0x17000);
        Assert.Equal(-(int)Errno.EXDEV, rc);

        Assert.True(env.SyscallManager.PathWalkWithFlags("/mnt/source.txt", LookupFlags.None).IsValid);
        Assert.False(env.SyscallManager.PathWalkWithFlags("/linked.txt", LookupFlags.None).IsValid);
    }

    [Fact]
    public async Task LinkAt_UnsupportedFlags_ReturnsEinval()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x1B000);
        env.MapUserPage(0x1C000);
        env.WriteCString(0x1B000, "/mnt/source.txt");
        env.WriteCString(0x1C000, "/linked.txt");

        env.CreateFile("/mnt/source.txt");

        var rc = await env.Call("SysLinkat", LinuxConstants.AT_FDCWD, 0x1B000, LinuxConstants.AT_FDCWD, 0x1C000, 0x1);
        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task RenameAt2_UnsupportedFlags_ReturnsEinval()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x1D000);
        env.MapUserPage(0x1E000);
        env.WriteCString(0x1D000, "/mnt/source.txt");
        env.WriteCString(0x1E000, "/mnt/target.txt");

        env.CreateFile("/mnt/source.txt");

        var rc = await env.Call("SysRenameAt2", LinuxConstants.AT_FDCWD, 0x1D000, LinuxConstants.AT_FDCWD, 0x1E000,
            LinuxConstants.RENAME_WHITEOUT);
        Assert.Equal(-(int)Errno.EINVAL, rc);
        Assert.True(env.SyscallManager.PathWalkWithFlags("/mnt/source.txt", LookupFlags.None).IsValid);
        Assert.False(env.SyscallManager.PathWalkWithFlags("/mnt/target.txt", LookupFlags.None).IsValid);
    }

    [Fact]
    public async Task RenameAt2_Exchange_SwapsEntriesWithinDirectory()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x1F000);
        env.MapUserPage(0x20000);
        env.WriteCString(0x1F000, "/mnt/left.txt");
        env.WriteCString(0x20000, "/mnt/right.txt");

        env.CreateFile("/mnt/left.txt", "L");
        env.CreateFile("/mnt/right.txt", "R");

        var rc = await env.Call("SysRenameAt2", LinuxConstants.AT_FDCWD, 0x1F000, LinuxConstants.AT_FDCWD, 0x20000,
            LinuxConstants.RENAME_EXCHANGE);
        Assert.Equal(0, rc);
        Assert.Equal("R", env.ReadFile("/mnt/left.txt"));
        Assert.Equal("L", env.ReadFile("/mnt/right.txt"));
    }

    [Fact]
    public async Task RenameAt2_Exchange_SwapsEntriesAcrossParents()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x30000);
        env.MapUserPage(0x31000);
        env.WriteCString(0x30000, "/mnt/a/left.txt");
        env.WriteCString(0x31000, "/mnt/b/right.txt");

        env.CreateDirectory("/mnt/a");
        env.CreateDirectory("/mnt/b");
        env.CreateFile("/mnt/a/left.txt", "L");
        env.CreateFile("/mnt/b/right.txt", "R");

        var rc = await env.Call("SysRenameAt2", LinuxConstants.AT_FDCWD, 0x30000, LinuxConstants.AT_FDCWD, 0x31000,
            LinuxConstants.RENAME_EXCHANGE);
        Assert.Equal(0, rc);
        Assert.Equal("R", env.ReadFile("/mnt/a/left.txt"));
        Assert.Equal("L", env.ReadFile("/mnt/b/right.txt"));
    }

    [Fact]
    public async Task RenameAt2_Exchange_SamePath_IsNoOp()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x34000);
        env.WriteCString(0x34000, "/mnt/same.txt");

        env.CreateFile("/mnt/same.txt", "S");

        var rc = await env.Call("SysRenameAt2", LinuxConstants.AT_FDCWD, 0x34000, LinuxConstants.AT_FDCWD, 0x34000,
            LinuxConstants.RENAME_EXCHANGE);
        Assert.Equal(0, rc);
        Assert.Equal("S", env.ReadFile("/mnt/same.txt"));
    }

    [Fact]
    public async Task RenameAt2_Exchange_FileAndSymlink_SwapsEntries()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x35000);
        env.MapUserPage(0x36000);
        env.WriteCString(0x35000, "/mnt/file.txt");
        env.WriteCString(0x36000, "/mnt/link.txt");

        env.CreateFile("/mnt/file.txt", "F");
        env.CreateSymlink("/mnt/link.txt", "file.txt");

        var rc = await env.Call("SysRenameAt2", LinuxConstants.AT_FDCWD, 0x35000, LinuxConstants.AT_FDCWD, 0x36000,
            LinuxConstants.RENAME_EXCHANGE);
        Assert.Equal(0, rc);

        var fileLoc = env.SyscallManager.PathWalkWithFlags("/mnt/file.txt", LookupFlags.None);
        var linkLoc = env.SyscallManager.PathWalkWithFlags("/mnt/link.txt", LookupFlags.None);
        Assert.True(fileLoc.IsValid);
        Assert.True(linkLoc.IsValid);
        Assert.Equal(InodeType.Symlink, fileLoc.Dentry!.Inode!.Type);
        Assert.Equal("file.txt", fileLoc.Dentry.Inode.Readlink());
        Assert.Equal(InodeType.File, linkLoc.Dentry!.Inode!.Type);
        Assert.Equal("F", env.ReadFile("/mnt/link.txt"));
    }

    [Fact]
    public async Task RenameAt2_Exchange_NonEmptyDirectoryAndSymlink_SwapsEntries()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x37000);
        env.MapUserPage(0x38000);
        env.WriteCString(0x37000, "/mnt/dir");
        env.WriteCString(0x38000, "/mnt/link.txt");

        env.CreateDirectory("/mnt/dir");
        env.CreateFile("/mnt/dir/child.txt", "D");
        env.CreateSymlink("/mnt/link.txt", "dir");

        var rc = await env.Call("SysRenameAt2", LinuxConstants.AT_FDCWD, 0x37000, LinuxConstants.AT_FDCWD, 0x38000,
            LinuxConstants.RENAME_EXCHANGE);
        Assert.Equal(0, rc);

        var dirLoc = env.SyscallManager.PathWalkWithFlags("/mnt/dir", LookupFlags.None);
        var linkLoc = env.SyscallManager.PathWalkWithFlags("/mnt/link.txt", LookupFlags.None);
        Assert.True(dirLoc.IsValid);
        Assert.True(linkLoc.IsValid);
        Assert.Equal(InodeType.Symlink, dirLoc.Dentry!.Inode!.Type);
        Assert.Equal("dir", dirLoc.Dentry.Inode.Readlink());
        Assert.Equal(InodeType.Directory, linkLoc.Dentry!.Inode!.Type);
        Assert.True(env.SyscallManager.PathWalkWithFlags("/mnt/link.txt/child.txt", LookupFlags.FollowSymlink).IsValid);
        Assert.Equal("D", env.ReadFile("/mnt/link.txt/child.txt"));
    }

    [Fact]
    public async Task RenameAt2_Exchange_TargetMissing_ReturnsEnoent()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x32000);
        env.MapUserPage(0x33000);
        env.WriteCString(0x32000, "/mnt/source.txt");
        env.WriteCString(0x33000, "/mnt/missing.txt");

        env.CreateFile("/mnt/source.txt");

        var rc = await env.Call("SysRenameAt2", LinuxConstants.AT_FDCWD, 0x32000, LinuxConstants.AT_FDCWD, 0x33000,
            LinuxConstants.RENAME_EXCHANGE);
        Assert.Equal(-(int)Errno.ENOENT, rc);
        Assert.True(env.SyscallManager.PathWalkWithFlags("/mnt/source.txt", LookupFlags.None).IsValid);
    }

    [Fact]
    public async Task RenameAt2_Noreplace_ReturnsEexist_WhenTargetExists()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x21000);
        env.MapUserPage(0x22000);
        env.WriteCString(0x21000, "/mnt/source.txt");
        env.WriteCString(0x22000, "/mnt/target.txt");

        env.CreateFile("/mnt/source.txt");
        env.CreateFile("/mnt/target.txt");

        var rc = await env.Call("SysRenameAt2", LinuxConstants.AT_FDCWD, 0x21000, LinuxConstants.AT_FDCWD, 0x22000,
            LinuxConstants.RENAME_NOREPLACE);
        Assert.Equal(-(int)Errno.EEXIST, rc);
        Assert.True(env.SyscallManager.PathWalkWithFlags("/mnt/source.txt", LookupFlags.None).IsValid);
        Assert.True(env.SyscallManager.PathWalkWithFlags("/mnt/target.txt", LookupFlags.None).IsValid);
    }

    [Fact]
    public async Task RenameAt2_Noreplace_Succeeds_WhenTargetMissing()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x23000);
        env.MapUserPage(0x24000);
        env.WriteCString(0x23000, "/mnt/source.txt");
        env.WriteCString(0x24000, "/mnt/target.txt");

        env.CreateFile("/mnt/source.txt");

        var rc = await env.Call("SysRenameAt2", LinuxConstants.AT_FDCWD, 0x23000, LinuxConstants.AT_FDCWD, 0x24000,
            LinuxConstants.RENAME_NOREPLACE);
        Assert.Equal(0, rc);
        Assert.False(env.SyscallManager.PathWalkWithFlags("/mnt/source.txt", LookupFlags.None).IsValid);
        Assert.True(env.SyscallManager.PathWalkWithFlags("/mnt/target.txt", LookupFlags.None).IsValid);
    }

    [Fact]
    public void PathWalk_DotDotFromMountRoot_AscendsToParentMountPoint()
    {
        using var env = new TestEnv();

        var loc = env.SyscallManager.PathWalkWithFlags("/mnt/..", LookupFlags.FollowSymlink);

        Assert.True(loc.IsValid);
        Assert.Same(env.SyscallManager.RootMount, loc.Mount);
        Assert.Same(env.SyscallManager.Root.Dentry, loc.Dentry);
    }

    [Fact]
    public async Task Unlink_MountPoint_ReturnsEisdir_AndPreservesMount()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x18000);
        env.WriteCString(0x18000, "/mnt");

        var rc = await env.Call("SysUnlink", 0x18000);
        Assert.Equal(-(int)Errno.EISDIR, rc);

        var loc = env.SyscallManager.PathWalkWithFlags("/mnt", LookupFlags.None);
        Assert.True(loc.IsValid);
        Assert.NotSame(env.SyscallManager.RootMount, loc.Mount);
    }

    [Fact]
    public async Task Rmdir_MountPoint_ReturnsEbusy_AndPreservesMount()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x19000);
        env.WriteCString(0x19000, "/mnt");

        var rc = await env.Call("SysRmdir", 0x19000);
        Assert.Equal(-(int)Errno.EBUSY, rc);

        var loc = env.SyscallManager.PathWalkWithFlags("/mnt", LookupFlags.None);
        Assert.True(loc.IsValid);
        Assert.NotSame(env.SyscallManager.RootMount, loc.Mount);
    }

    [Fact]
    public async Task Rename_MountPoint_ReturnsEbusy_AndPreservesMount()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x29000);
        env.MapUserPage(0x2A000);
        env.WriteCString(0x29000, "/mnt");
        env.WriteCString(0x2A000, "/renamed");

        var rc = await env.Call("SysRename", 0x29000, 0x2A000);
        Assert.Equal(-(int)Errno.EBUSY, rc);

        var loc = env.SyscallManager.PathWalkWithFlags("/mnt", LookupFlags.None);
        Assert.True(loc.IsValid);
        Assert.NotSame(env.SyscallManager.RootMount, loc.Mount);
        Assert.False(env.SyscallManager.PathWalkWithFlags("/renamed", LookupFlags.None).IsValid);
    }

    [Fact]
    public async Task UnlinkAt_RemovedirOnMountPoint_ReturnsEbusy_AndPreservesMount()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x1A000);
        env.WriteCString(0x1A000, "/mnt");

        const uint AT_REMOVEDIR = 0x200;
        var rc = await env.Call("SysUnlinkAt", LinuxConstants.AT_FDCWD, 0x1A000, AT_REMOVEDIR);
        Assert.Equal(-(int)Errno.EBUSY, rc);

        var loc = env.SyscallManager.PathWalkWithFlags("/mnt", LookupFlags.None);
        Assert.True(loc.IsValid);
        Assert.NotSame(env.SyscallManager.RootMount, loc.Mount);
    }

    [Fact]
    public void PathWalk_NoXdevRejectsCrossingIntoMountedTree()
    {
        using var env = new TestEnv();

        var nd = env.SyscallManager.PathWalkWithData("/mnt", LookupFlags.NoXdev);

        Assert.True(nd.HasError);
        Assert.Equal(-(int)Errno.EXDEV, nd.ErrorCode);
    }

    [Fact]
    public void PathWalk_NoXdevRejectsDotDotLeavingMountedTree()
    {
        using var env = new TestEnv();

        var mountLoc = env.SyscallManager.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
        Assert.True(mountLoc.IsValid);
        Assert.NotSame(env.SyscallManager.RootMount, mountLoc.Mount);

        var nd = env.SyscallManager.PathWalker.PathWalkWithData("..", mountLoc, LookupFlags.NoXdev);

        Assert.True(nd.HasError);
        Assert.Equal(-(int)Errno.EXDEV, nd.ErrorCode);
    }

    private sealed class TestEnv : IDisposable
    {
        private uint _nextUserPageAddr = 0x40000;

        public TestEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
            SyscallManager = new SyscallManager(Engine, Vma, 0);

            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "cross-mount-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root)
            {
                Source = "tmpfs-root",
                FsType = "tmpfs",
                Options = "rw"
            };
            SyscallManager.InitializeRoot(rootSb.Root, rootMount);

            var root = SyscallManager.Root.Dentry!;
            var mountPoint = new Dentry("mnt", null, root, root.SuperBlock);
            root.Inode!.Mkdir(mountPoint, 0x1FF, 0, 0);
            root.Children["mnt"] = mountPoint;

            var mountedSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "cross-mount-mounted", null);
            var mountedMount = new Mount(mountedSb, mountedSb.Root)
            {
                Source = "tmpfs-mounted",
                FsType = "tmpfs",
                Options = "rw"
            };
            SyscallManager.RegisterMount(mountedMount, SyscallManager.RootMount, mountPoint);
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public SyscallManager SyscallManager { get; }

        public void Dispose()
        {
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

        public void CreateFile(string path, string contents = "")
        {
            var loc = SyscallManager.PathWalkWithFlags(path, LookupFlags.None);
            Assert.False(loc.IsValid);

            var (parentLoc, name, err) = SyscallManager.PathWalkForCreate(path);
            Assert.Equal(0, err);
            var dentry = new Dentry(name, null, parentLoc.Dentry, parentLoc.Dentry!.SuperBlock);
            var created = parentLoc.Dentry.Inode!.Create(dentry, 0x1A4, 0, 0);
            if (!string.IsNullOrEmpty(contents))
            {
                var file = new LinuxFile(created, FileFlags.O_RDWR, parentLoc.Mount!);
                var bytes = Encoding.UTF8.GetBytes(contents);
                Assert.True(created.Inode!.Write(file, bytes, 0) >= 0);
            }
        }

        public void CreateDirectory(string path)
        {
            var loc = SyscallManager.PathWalkWithFlags(path, LookupFlags.None);
            Assert.False(loc.IsValid);

            var (parentLoc, name, err) = SyscallManager.PathWalkForCreate(path);
            Assert.Equal(0, err);
            var dentry = new Dentry(name, null, parentLoc.Dentry, parentLoc.Dentry!.SuperBlock);
            parentLoc.Dentry.Inode!.Mkdir(dentry, 0x1ED, 0, 0);
        }

        public void CreateSymlink(string path, string target)
        {
            var loc = SyscallManager.PathWalkWithFlags(path, LookupFlags.None);
            Assert.False(loc.IsValid);

            var (parentLoc, name, err) = SyscallManager.PathWalkForCreate(path);
            Assert.Equal(0, err);
            var dentry = new Dentry(name, null, parentLoc.Dentry, parentLoc.Dentry!.SuperBlock);
            parentLoc.Dentry.Inode!.Symlink(dentry, target, 0, 0);
        }

        public string ReadFile(string path)
        {
            var loc = SyscallManager.PathWalkWithFlags(path, LookupFlags.None);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount!);
            var buffer = new byte[64];
            var read = loc.Dentry!.Inode!.Read(file, buffer, 0);
            Assert.True(read >= 0);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }

        public async ValueTask<int> Call(string methodName, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0,
            uint a5 = 0, uint a6 = 0)
        {
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var task = (ValueTask<int>)method!.Invoke(SyscallManager, [Engine, a1, a2, a3, a4, a5, a6])!;
            return await task;
        }

        public async ValueTask<int> OpenDirectory(string path)
        {
            var addr = AllocateMappedUserPage();
            WriteCString(addr, path);
            var fd = await Call("SysOpen", addr);
            Assert.True(fd >= 0);
            return fd;
        }

        private uint AllocateMappedUserPage()
        {
            var addr = _nextUserPageAddr;
            _nextUserPageAddr += LinuxConstants.PageSize;
            MapUserPage(addr);
            return addr;
        }
    }
}