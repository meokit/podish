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

public class OverlayImpermissibleSyscallTests
{
    [Theory]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC), "shark", "shark")]
    [InlineData((uint)FileFlags.O_WRONLY, "shark", "sharkyyy:zzz")]
    [InlineData((uint)(FileFlags.O_WRONLY | FileFlags.O_APPEND), "shark", ":xxx:yyy:zzzshark")]
    public async Task Open_WriteLikeModes_DenyUnprivilegedButAllowRoot(uint flags, string writePayload, string expectedAfterRoot)
    {
        using var env = new TestEnv();
        env.MapUserPage(0x10000u);
        env.MapUserPage(0x11000u);
        env.WriteCString(0x10000u, "/rootfile");
        env.WriteBytes(0x11000u, Encoding.ASCII.GetBytes(writePayload));

        Assert.Equal(-(int)Errno.EACCES, await env.Call("SysOpen", 0x10000u, flags));
        await AssertFileContent(env, "/rootfile", ":xxx:yyy:zzz");

        env.BecomeRoot();
        var fd = await env.Call("SysOpen", 0x10000u, flags);
        Assert.True(fd >= 0);
        Assert.Equal(writePayload.Length, await env.Call("SysWrite", (uint)fd, 0x11000u, (uint)writePayload.Length));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        await AssertFileContent(env, "/rootfile", expectedAfterRoot);
    }

    [Fact]
    public async Task Truncate_DeniesUnprivilegedButAllowsRoot()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x12000u);
        env.WriteCString(0x12000u, "/rootfile");

        Assert.Equal(-(int)Errno.EACCES, await env.Call("SysTruncate", 0x12000u, 4));
        await AssertFileContent(env, "/rootfile", ":xxx:yyy:zzz");

        env.BecomeRoot();
        Assert.Equal(0, await env.Call("SysTruncate", 0x12000u, 4));
        await AssertFileContent(env, "/rootfile", ":xxx");
    }

    [Fact]
    public async Task UtimensAt_DeniesUnprivilegedButAllowsRoot()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x13000u);
        env.MapUserPage(0x14000u);
        env.WriteCString(0x13000u, "/rootfile");

        var inode = env.Lookup("/rootfile")!.Inode!;
        var originalAtime = inode.ATime;
        var originalMtime = inode.MTime;

        var times = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(0, 4), 1_700_001_000);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(4, 4), 100);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(8, 4), 1_700_001_100);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(12, 4), 200);
        env.WriteBytes(0x14000u, times);

        Assert.Equal(-(int)Errno.EACCES, await env.Call("SysUtimensAt", unchecked((uint)LinuxConstants.AT_FDCWD), 0x13000u, 0x14000u, 0));
        Assert.Equal(originalAtime, inode.ATime);
        Assert.Equal(originalMtime, inode.MTime);

        env.BecomeRoot();
        Assert.Equal(0, await env.Call("SysUtimensAt", unchecked((uint)LinuxConstants.AT_FDCWD), 0x13000u, 0x14000u, 0));
        Assert.NotEqual(originalAtime, inode.ATime);
        Assert.NotEqual(originalMtime, inode.MTime);
    }

    private static async Task AssertFileContent(TestEnv env, string path, string expected)
    {
        env.MapUserPage(0x15000u);
        env.MapUserPage(0x16000u);
        env.WriteCString(0x15000u, path);
        var fd = await env.Call("SysOpen", 0x15000u, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        var rc = await env.Call("SysRead", (uint)fd, 0x16000u, 64);
        Assert.Equal(expected.Length, rc);
        Assert.Equal(expected, Encoding.ASCII.GetString(env.ReadBytes(0x16000u, rc)));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly HashSet<uint> _mappedPages = [];

        public TestEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
            Process = new Process(100, Vma, null!)
            {
                UID = 1000,
                GID = 1000,
                EUID = 1000,
                EGID = 1000,
                FSUID = 1000,
                FSGID = 1000
            };
            Scheduler = new KernelScheduler();
            Task = new FiberTask(100, Process, Engine, Scheduler);
            Engine.Owner = Task;
            Engine.PageFaultResolver = (addr, isWrite) => Vma.HandleFault(addr, isWrite, Engine);
            SyscallManager = new SyscallManager(Engine, Vma, 0);

            var (root, mount) = CreateOverlayRootWithRootOwnedFile();
            RootMount = mount;
            SyscallManager.InitializeRoot(root, mount);
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public Process Process { get; }
        public KernelScheduler Scheduler { get; }
        public FiberTask Task { get; }
        public SyscallManager SyscallManager { get; }
        public Mount RootMount { get; }

        public void Dispose()
        {
            SyscallManager.Close();
        }

        public void BecomeRoot()
        {
            Process.UID = Process.EUID = Process.FSUID = 0;
            Process.GID = Process.EGID = Process.FSGID = 0;
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
            Assert.True(Engine.CopyToUser(addr, Encoding.UTF8.GetBytes(value + "\0")));
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

        public Dentry? Lookup(string path) => SyscallManager.PathWalkWithFlags(path, LookupFlags.FollowSymlink).Dentry;

        public async ValueTask<int> Call(string methodName, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0,
            uint a5 = 0, uint a6 = 0)
        {
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var task = (ValueTask<int>)method!.Invoke(SyscallManager, [Engine, a1, a2, a3, a4, a5, a6])!;
            return await task;
        }
    }

    private static (Dentry Root, Mount Mount) CreateOverlayRootWithRootOwnedFile()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "impermissible-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "impermissible-upper", null);

        var lowerRoot = lowerSb.Root;
        var rootfile = new Dentry("rootfile", null, lowerRoot, lowerSb);
        lowerRoot.Inode!.Create(rootfile, 0x1A4, 0, 0);
        rootfile.Inode!.Uid = 0;
        rootfile.Inode.Gid = 0;
        var writer = new LinuxFile(rootfile, FileFlags.O_WRONLY, null!);
        try
        {
            Assert.Equal(12, rootfile.Inode.WriteFromHost(null, writer, Encoding.ASCII.GetBytes(":xxx:yyy:zzz"), 0));
        }
        finally
        {
            writer.Close();
        }

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "impermissible-overlay",
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
