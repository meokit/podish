using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class MknodAndXattrSyscallTests
{
    [Fact]
    public async Task Mknodat_Creates_Char_Device_Node()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x10000);
        env.WriteCString(0x10000, "/devnode");

        const uint mode = 0x2000 | 0x180; // S_IFCHR | 0600
        const uint rdev = (5u << 8) | 1u;
        var rc = await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x10000, mode, rdev);
        Assert.Equal(0, rc);

        var loc = env.SyscallManager.PathWalkWithFlags("/devnode", LookupFlags.None);
        Assert.True(loc.IsValid);
        var inode = Assert.IsAssignableFrom<Inode>(loc.Dentry!.Inode);
        Assert.Equal(InodeType.CharDev, inode.Type);
        Assert.Equal(rdev, inode.Rdev);
    }

    [Fact]
    public async Task Xattr_Path_And_Fd_Variants_Work()
    {
        using var env = new TestEnv();

        env.MapUserPage(0x10000);
        env.MapUserPage(0x11000);
        env.MapUserPage(0x12000);
        env.MapUserPage(0x13000);
        env.MapUserPage(0x14000);
        env.MapUserPage(0x15000);
        env.MapUserPage(0x16000);
        env.MapUserPage(0x17000);

        env.WriteCString(0x10000, "/file");
        env.WriteCString(0x11000, "user.demo");
        env.WriteBytes(0x12000, Encoding.UTF8.GetBytes("abc"));
        env.WriteCString(0x15000, "user.fd");
        env.WriteBytes(0x16000, Encoding.UTF8.GetBytes("xyz"));

        var mkRc = await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x10000, 0x8000 | 0x1A4); // S_IFREG|0644
        Assert.Equal(0, mkRc);

        var setRc = await env.Call("SysSetXAttr", 0x10000, 0x11000, 0x12000, 3);
        Assert.Equal(0, setRc);

        var lenRc = await env.Call("SysGetXAttr", 0x10000, 0x11000);
        Assert.Equal(3, lenRc);

        var getRc = await env.Call("SysGetXAttr", 0x10000, 0x11000, 0x13000, 16);
        Assert.Equal(3, getRc);
        Assert.Equal("abc", Encoding.UTF8.GetString(env.ReadBytes(0x13000, 3)));

        var fd = await env.Call("SysOpen", 0x10000);
        Assert.True(fd >= 0);

        var fsetRc = await env.Call("SysFSetXAttr", (uint)fd, 0x15000, 0x16000, 3);
        Assert.Equal(0, fsetRc);

        var fgetRc = await env.Call("SysFGetXAttr", (uint)fd, 0x15000, 0x17000, 16);
        Assert.Equal(3, fgetRc);
        Assert.Equal("xyz", Encoding.UTF8.GetString(env.ReadBytes(0x17000, 3)));
    }

    [Fact]
    public async Task List_And_Remove_Xattr_Work()
    {
        using var env = new TestEnv();

        env.MapUserPage(0x10000);
        env.MapUserPage(0x11000);
        env.MapUserPage(0x12000);
        env.MapUserPage(0x13000);
        env.MapUserPage(0x14000);
        env.MapUserPage(0x15000);
        env.MapUserPage(0x16000);

        env.WriteCString(0x10000, "/listfile");
        env.WriteCString(0x11000, "user.a");
        env.WriteCString(0x12000, "user.b");
        env.WriteBytes(0x13000, Encoding.UTF8.GetBytes("1"));
        env.WriteBytes(0x14000, Encoding.UTF8.GetBytes("2"));

        var mkRc = await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x10000, 0x8000 | 0x180); // S_IFREG|0600
        Assert.Equal(0, mkRc);

        Assert.Equal(0, await env.Call("SysSetXAttr", 0x10000, 0x11000, 0x13000, 1));
        Assert.Equal(0, await env.Call("SysSetXAttr", 0x10000, 0x12000, 0x14000, 1));

        var needed = await env.Call("SysListXAttr", 0x10000);
        Assert.True(needed > 0);

        var listRc = await env.Call("SysListXAttr", 0x10000, 0x15000, (uint)needed);
        Assert.Equal(needed, listRc);
        var listBytes = env.ReadBytes(0x15000, listRc);
        var names = Encoding.UTF8.GetString(listBytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("user.a", names);
        Assert.Contains("user.b", names);

        var rmRc = await env.Call("SysRemoveXAttr", 0x10000, 0x11000);
        Assert.Equal(0, rmRc);
        var getRemovedRc = await env.Call("SysGetXAttr", 0x10000, 0x11000, 0x16000, 8);
        Assert.Equal(-(int)Errno.ENODATA, getRemovedRc);
    }

    [Fact]
    public async Task Mknodat_On_OverlayRoot_Creates_Node()
    {
        using var env = new TestEnv(true);
        env.MapUserPage(0x18000);
        env.WriteCString(0x18000, "/tmp/devzero");

        var rc = await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x18000, 0x2000 | 0x180, (1u << 8) | 5u);
        Assert.Equal(0, rc);

        var loc = env.SyscallManager.PathWalkWithFlags("/tmp/devzero", LookupFlags.None);
        Assert.True(loc.IsValid);
        Assert.Equal(InodeType.CharDev, loc.Dentry!.Inode!.Type);
        Assert.Equal((1u << 8) | 5u, loc.Dentry.Inode.Rdev);
    }

    [Fact]
    public void MountDetachedTmpfsFile_MountsFileAtGuestPath()
    {
        using var env = new TestEnv();
        env.SyscallManager.MountDetachedTmpfsFile(
            "/etc/resolv.conf",
            "resolv.conf",
            Encoding.UTF8.GetBytes("nameserver 8.8.8.8\n"));

        var loc = env.SyscallManager.PathWalkWithFlags("/etc/resolv.conf", LookupFlags.FollowSymlink);
        Assert.True(loc.IsValid);
        Assert.Equal("tmpfs", loc.Mount!.FsType);
        Assert.Equal(InodeType.File, loc.Dentry!.Inode!.Type);

        var f = new LinuxFile(loc.Dentry, FileFlags.O_RDONLY, loc.Mount);
        var buf = new byte[64];
        var n = loc.Dentry.Inode.ReadToHost(null, f, buf, 0);
        Assert.True(n > 0);
        var text = Encoding.UTF8.GetString(buf, 0, n);
        Assert.Contains("nameserver 8.8.8.8", text);
    }

    [Fact]
    public async Task BindMountSubtree_FileMount_IsWritableAndUsesFileMountRoot()
    {
        using var env = new TestEnv(true);
        var sourceHandle = CreateBoundGeneratedFile(env);
        try
        {
            var loc = env.SyscallManager.PathWalkWithFlags("/etc/resolv.conf", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            Assert.Equal("none", loc.Mount!.FsType);
            Assert.Equal(InodeType.File, loc.Dentry!.Inode!.Type);
            Assert.Same(loc.Dentry, loc.Mount.Root);
            Assert.NotSame(loc.Mount.SB.Root, loc.Mount.Root);

            env.MapUserPage(0x19000);
            env.WriteCString(0x19000, "/etc/resolv.conf");
            env.MapUserPage(0x1A000);
            var payload = Encoding.UTF8.GetBytes("nameserver 9.9.9.9\n");
            env.WriteBytes(0x1A000, payload);

            var fd = await env.Call("SysOpen", 0x19000, (uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC));
            Assert.True(fd >= 0);
            Assert.Equal(payload.Length, await env.Call("SysWrite", (uint)fd, 0x1A000, (uint)payload.Length));
            Assert.Equal(0, await env.Call("SysClose", (uint)fd));

            loc = env.SyscallManager.PathWalkWithFlags("/etc/resolv.conf", LookupFlags.FollowSymlink);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount!);
            try
            {
                var buf = new byte[64];
                var n = loc.Dentry!.Inode!.ReadToHost(null, file, buf, 0);
                Assert.True(n > 0);
                Assert.Equal("nameserver 9.9.9.9\n", Encoding.UTF8.GetString(buf, 0, n));
            }
            finally
            {
                file.Close();
            }
        }
        finally
        {
            sourceHandle.Close();
        }
    }

    [Fact]
    public async Task BindMountSubtree_FileMount_UnlinkReturnsEbusyAndPreservesMount()
    {
        using var env = new TestEnv(true);
        var sourceHandle = CreateBoundGeneratedFile(env);
        try
        {
            env.MapUserPage(0x1B000);
            env.WriteCString(0x1B000, "/etc/resolv.conf");

            Assert.Equal(-(int)Errno.EBUSY, await env.Call("SysUnlink", 0x1B000));

            var loc = env.SyscallManager.PathWalkWithFlags("/etc/resolv.conf", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            Assert.NotSame(env.SyscallManager.RootMount, loc.Mount);
        }
        finally
        {
            sourceHandle.Close();
        }
    }

    [Fact]
    public async Task BindMountSubtree_FileMount_RenameReturnsEbusyAndPreservesMount()
    {
        using var env = new TestEnv(true);
        var sourceHandle = CreateBoundGeneratedFile(env);
        try
        {
            env.MapUserPage(0x1C000);
            env.MapUserPage(0x1D000);
            env.WriteCString(0x1C000, "/etc/resolv.conf");
            env.WriteCString(0x1D000, "/etc/resolv-next.conf");

            Assert.Equal(-(int)Errno.EBUSY, await env.Call("SysRename", 0x1C000, 0x1D000));

            var loc = env.SyscallManager.PathWalkWithFlags("/etc/resolv.conf", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            Assert.False(env.SyscallManager.PathWalkWithFlags("/etc/resolv-next.conf", LookupFlags.FollowSymlink)
                .IsValid);
        }
        finally
        {
            sourceHandle.Close();
        }
    }

    private static MountFile CreateBoundGeneratedFile(TestEnv env)
    {
        var handle = new MountFile(env.SyscallManager.CreateDetachedTmpfsMount("podish-config"));
        env.SyscallManager.WriteFileInDetachedMount(
            handle.Mount,
            "resolv.conf",
            Encoding.UTF8.GetBytes("nameserver 1.1.1.1\n"));
        env.SyscallManager.BindMountSubtree(handle.Mount, "resolv.conf", "/etc/resolv.conf");
        return handle;
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly string? _tempLowerDir;

        public TestEnv(bool useOverlayRoot = false)
        {
            Engine = new Engine();
            Vma = new VMAManager();
            SyscallManager = new SyscallManager(Engine, Vma, 0);

            if (useOverlayRoot)
            {
                _tempLowerDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                Directory.CreateDirectory(_tempLowerDir);
                Directory.CreateDirectory(Path.Combine(_tempLowerDir, "tmp"));

                var hostType = new FileSystemType { Name = "hostfs" };
                var hostOpts = HostfsMountOptions.Parse("rw");
                var lowerSb = new HostSuperBlock(hostType, _tempLowerDir, hostOpts);
                lowerSb.Root = lowerSb.GetDentry(_tempLowerDir, "/", null)!;

                var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
                var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "test-overlay-upper", null);

                var overlayFs = new OverlayFileSystem();
                var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                    new FileSystemType { Name = "overlay" },
                    0,
                    "overlay",
                    new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

                var mount = new Mount(overlaySb, overlaySb.Root)
                {
                    Source = "overlay",
                    FsType = "overlay",
                    Options = "rw"
                };
                SyscallManager.InitializeRoot(overlaySb.Root, mount);
            }
            else
            {
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var sb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "test-tmpfs", null);
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
            if (!string.IsNullOrEmpty(_tempLowerDir) && Directory.Exists(_tempLowerDir))
                Directory.Delete(_tempLowerDir, true);
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
}
