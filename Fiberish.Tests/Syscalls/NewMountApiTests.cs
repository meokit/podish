using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class NewMountApiTests
{
    private const uint FSCONFIG_SET_FLAG = 0;
    private const uint FSCONFIG_SET_STRING = 1;
    private const uint FSCONFIG_CMD_CREATE = 6;

    [Fact]
    public async Task Fsopen_Fsconfig_Fsmount_MoveMount_Works()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x10000);
        env.MapUserPage(0x11000);
        env.MapUserPage(0x12000);
        env.MapUserPage(0x13000);

        env.WriteCString(0x10000, "tmpfs");
        env.WriteCString(0x11000, "source");
        env.WriteCString(0x12000, "tmpfs-test");
        env.WriteCString(0x13000, "/mnt");

        var fsfd = await env.Call("SysFsopen", 0x10000);
        Assert.True(fsfd >= 0);
        Assert.IsType<FsContextFile>(env.SyscallManager.GetFD(fsfd)!);

        Assert.Equal(0, await env.Call("SysFsconfig", (uint)fsfd, FSCONFIG_SET_STRING, 0x11000, 0x12000));
        Assert.Equal(0,
            await env.Call("SysFsconfig", (uint)fsfd, FSCONFIG_SET_FLAG, 0x12000)); // key=tmpfs-test (arbitrary flag)
        Assert.Equal(0, await env.Call("SysFsconfig", (uint)fsfd, FSCONFIG_CMD_CREATE));
        Assert.Equal(-(int)Errno.EBUSY,
            await env.Call("SysFsconfig", (uint)fsfd, FSCONFIG_SET_STRING, 0x11000, 0x12000));

        var mntfd = await env.Call("SysFsmount", (uint)fsfd);
        Assert.True(mntfd >= 0);
        var mountFile = Assert.IsType<MountFile>(env.SyscallManager.GetFD(mntfd)!);
        Assert.False(mountFile.Mount.IsAttached);

        // Ensure /mnt exists as mount target
        var root = env.SyscallManager.Root.Dentry!;
        if (root.Inode!.Lookup("mnt") == null)
        {
            var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
            root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
            root.Children["mnt"] = mntDentry;
        }

        Assert.Equal(0, await env.Call("SysMoveMount", (uint)mntfd, 0, LinuxConstants.AT_FDCWD, 0x13000));

        var loc = env.SyscallManager.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
        Assert.True(loc.IsValid);
        Assert.Equal("tmpfs", loc.Mount!.FsType);
        Assert.True(loc.Mount.IsAttached);
    }

    [Fact]
    public async Task Fsopen_UnknownFs_Returns_ENODEV()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x20000);
        env.WriteCString(0x20000, "no-such-fs");
        var rc = await env.Call("SysFsopen", 0x20000);
        Assert.Equal(-(int)Errno.ENODEV, rc);
    }

    [Fact]
    public async Task Fsmount_DetachedMount_IsReleased_WhenFdClosed()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x30000);
        env.WriteCString(0x30000, "tmpfs");

        var fsfd = await env.Call("SysFsopen", 0x30000);
        Assert.True(fsfd >= 0);
        Assert.Equal(0, await env.Call("SysFsconfig", (uint)fsfd, FSCONFIG_CMD_CREATE));

        var mntfd = await env.Call("SysFsmount", (uint)fsfd);
        var mountFile = Assert.IsType<MountFile>(env.SyscallManager.GetFD(mntfd)!);
        var mount = mountFile.Mount;
        Assert.NotNull(mount);
        Assert.Equal(1, mount.RefCount); // fd owner
        Assert.False(mount.IsAttached);

        env.SyscallManager.FreeFD(mntfd);
        Assert.Equal(0, mount.RefCount);
    }

    [Fact]
    public async Task ContainerPin_KeepsMountAlive_UntilReleased()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x31000);
        env.WriteCString(0x31000, "tmpfs");

        var fsfd = await env.Call("SysFsopen", 0x31000);
        Assert.Equal(0, await env.Call("SysFsconfig", (uint)fsfd, FSCONFIG_CMD_CREATE));
        var mntfd = await env.Call("SysFsmount", (uint)fsfd);
        var mount = Assert.IsType<MountFile>(env.SyscallManager.GetFD(mntfd)!).Mount;

        env.SyscallManager.PinContainerMount(mount);
        env.SyscallManager.FreeFD(mntfd);
        Assert.Equal(1, mount.RefCount); // container pin owner

        env.SyscallManager.ReleaseContainerPins();
        Assert.Equal(0, mount.RefCount);
    }

    [Fact]
    public async Task MountSyscall_RegularTmpfs_UsesUnifiedDetachedAttachFlow()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x40000);
        env.MapUserPage(0x41000);
        env.MapUserPage(0x42000);
        env.MapUserPage(0x43000);

        env.WriteCString(0x40000, "tmpfs-src");
        env.WriteCString(0x41000, "/mnt");
        env.WriteCString(0x42000, "tmpfs");
        env.WriteCString(0x43000, "nosuid,nodev,size=64k");

        var root = env.SyscallManager.Root.Dentry!;
        if (root.Inode!.Lookup("mnt") == null)
        {
            var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
            root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
            root.Children["mnt"] = mntDentry;
        }

        var rc = await env.Call("SysMount", 0x40000, 0x41000, 0x42000, 0, 0x43000);
        Assert.Equal(0, rc);

        var loc = env.SyscallManager.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
        Assert.True(loc.IsValid);
        Assert.Equal("tmpfs", loc.Mount!.FsType);
        Assert.Contains("nosuid", loc.Mount.Options);
        Assert.Contains("nodev", loc.Mount.Options);
        Assert.Contains("size=64k", loc.Mount.Options);
    }

    [Fact]
    public async Task MountSyscall_Bind_UsesAttachHelper_AndAppliesFlags()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x44000);
        env.MapUserPage(0x45000);

        var root = env.SyscallManager.Root.Dentry!;
        if (root.Inode!.Lookup("src") == null)
        {
            var srcDentry = new Dentry("src", null, root, root.SuperBlock);
            root.Inode.Mkdir(srcDentry, 0x1FF, 0, 0);
            root.Children["src"] = srcDentry;
        }

        if (root.Inode.Lookup("dst") == null)
        {
            var dstDentry = new Dentry("dst", null, root, root.SuperBlock);
            root.Inode.Mkdir(dstDentry, 0x1FF, 0, 0);
            root.Children["dst"] = dstDentry;
        }

        env.WriteCString(0x44000, "/src");
        env.WriteCString(0x45000, "/dst");

        var rc = await env.Call("SysMount", 0x44000, 0x45000, 0, LinuxConstants.MS_BIND | LinuxConstants.MS_RDONLY);
        Assert.Equal(0, rc);

        var loc = env.SyscallManager.PathWalkWithFlags("/dst", LookupFlags.FollowSymlink);
        Assert.True(loc.IsValid);
        Assert.Equal("none", loc.Mount!.FsType);
        Assert.True(loc.Mount.IsReadOnly);
    }

    [Fact]
    public async Task MountSyscall_Overlay_ParsesDataString_AndAttaches()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x46000);
        env.MapUserPage(0x47000);
        env.MapUserPage(0x48000);
        env.MapUserPage(0x49000);

        var root = env.SyscallManager.Root.Dentry!;
        foreach (var name in new[] { "lower", "upper", "work", "merged" })
        {
            if (root.Inode!.Lookup(name) != null) continue;
            var dir = new Dentry(name, null, root, root.SuperBlock);
            root.Inode.Mkdir(dir, 0x1FF, 0, 0);
            root.Children[name] = dir;
        }

        env.WriteCString(0x46000, "overlay");
        env.WriteCString(0x47000, "/merged");
        env.WriteCString(0x48000, "overlay");
        env.WriteCString(0x49000, "lowerdir=/lower,upperdir=/upper,workdir=/work");

        var rc = await env.Call("SysMount", 0x46000, 0x47000, 0x48000, 0, 0x49000);
        Assert.Equal(0, rc);

        var loc = env.SyscallManager.PathWalkWithFlags("/merged", LookupFlags.FollowSymlink);
        Assert.True(loc.IsValid);
        Assert.Equal("overlay", loc.Mount!.FsType);
    }

    [Fact]
    public async Task MountSyscall_Remount_UsesSharedFlagUpdateLogic()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x4a000);
        env.MapUserPage(0x4b000);
        env.MapUserPage(0x4c000);
        env.MapUserPage(0x4d000);

        var root = env.SyscallManager.Root.Dentry!;
        if (root.Inode!.Lookup("mnt") == null)
        {
            var mnt = new Dentry("mnt", null, root, root.SuperBlock);
            root.Inode.Mkdir(mnt, 0x1FF, 0, 0);
            root.Children["mnt"] = mnt;
        }

        env.WriteCString(0x4a000, "tmpfs-src");
        env.WriteCString(0x4b000, "/mnt");
        env.WriteCString(0x4c000, "tmpfs");
        env.WriteCString(0x4d000, "size=64k");

        Assert.Equal(0, await env.Call("SysMount", 0x4a000, 0x4b000, 0x4c000, 0, 0x4d000));
        var loc1 = env.SyscallManager.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
        Assert.True(loc1.IsValid);
        Assert.False(loc1.Mount!.IsReadOnly);

        var remountFlags = LinuxConstants.MS_REMOUNT | LinuxConstants.MS_RDONLY | LinuxConstants.MS_NOSUID;
        Assert.Equal(0, await env.Call("SysMount", 0, 0x4b000, 0, remountFlags));

        var loc2 = env.SyscallManager.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
        Assert.True(loc2.IsValid);
        Assert.True(loc2.Mount!.IsReadOnly);
        Assert.Contains("nosuid", loc2.Mount.Options);
    }

    [Fact]
    public async Task MountSetattr_UsesSharedFlagUpdateLogic()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x4e000);
        env.MapUserPage(0x4f000);
        env.MapUserPage(0x50000);
        env.MapUserPage(0x51000);
        env.MapUserPage(0x52000);

        var root = env.SyscallManager.Root.Dentry!;
        if (root.Inode!.Lookup("mnt") == null)
        {
            var mnt = new Dentry("mnt", null, root, root.SuperBlock);
            root.Inode.Mkdir(mnt, 0x1FF, 0, 0);
            root.Children["mnt"] = mnt;
        }

        env.WriteCString(0x4e000, "tmpfs-src");
        env.WriteCString(0x4f000, "/mnt");
        env.WriteCString(0x50000, "tmpfs");
        env.WriteCString(0x51000, "nosuid");
        Assert.Equal(0, await env.Call("SysMount", 0x4e000, 0x4f000, 0x50000, LinuxConstants.MS_NOSUID, 0x51000));

        // struct mount_attr { u64 attr_set, attr_clr, propagation, userns_fd }
        var attrSet = BitConverter.GetBytes((ulong)(1 | 4)); // RDONLY | NODEV
        var attrClr = BitConverter.GetBytes((ulong)2); // NOSUID
        var raw = new byte[32];
        Array.Copy(attrSet, 0, raw, 0, 8);
        Array.Copy(attrClr, 0, raw, 8, 8);
        Assert.True(env.Engine.CopyToUser(0x52000, raw));

        Assert.Equal(0,
            await env.Call("SysMountSetattr", unchecked(LinuxConstants.AT_FDCWD), 0x4f000, 0, 0x52000, 32));

        var loc = env.SyscallManager.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
        Assert.True(loc.IsValid);
        Assert.True(loc.Mount!.IsReadOnly);
        Assert.Contains("nodev", loc.Mount.Options);
        Assert.DoesNotContain("nosuid", loc.Mount.Options);
    }

    [Fact]
    public async Task Umount_Hostfs_WithOpenFile_IsBusy_UntilClose()
    {
        var hostDir = Path.Combine(Path.GetTempPath(), $"hostfs-umount-busy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(hostDir);
        File.WriteAllText(Path.Combine(hostDir, "x.txt"), "x");

        using var env = new TestEnv();
        env.MapUserPage(0x60000);
        env.MapUserPage(0x61000);
        env.MapUserPage(0x62000);
        env.MapUserPage(0x63000);
        env.MapUserPage(0x64000);

        try
        {
            var root = env.SyscallManager.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mnt = new Dentry("mnt", null, root, root.SuperBlock);
                root.Inode.Mkdir(mnt, 0x1FF, 0, 0);
                root.Children["mnt"] = mnt;
            }

            env.WriteCString(0x60000, hostDir);
            env.WriteCString(0x61000, "/mnt");
            env.WriteCString(0x62000, "hostfs");
            env.WriteCString(0x63000, "rw");
            Assert.Equal(0, await env.Call("SysMount", 0x60000, 0x61000, 0x62000, 0, 0x63000));

            env.WriteCString(0x64000, "/mnt/x.txt");
            var fd = await env.Call("SysOpen", 0x64000);
            Assert.True(fd >= 0);

            Assert.Equal(-(int)Errno.EBUSY, await env.Call("SysUmount", 0x61000));
            Assert.Equal(0, await env.Call("SysClose", (uint)fd));
            Assert.Equal(0, await env.Call("SysUmount", 0x61000));
        }
        finally
        {
            env.SyscallManager.Close();
            if (Directory.Exists(hostDir)) Directory.Delete(hostDir, true);
        }
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
            SyscallManager = new SyscallManager(Engine, Vma, 0);

            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var sb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
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
}