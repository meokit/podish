using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class PathWalkTruncateSyscallTests
{
    [Fact]
    public async Task Truncate_ExtantFile_ResizesAcrossShrinkAndGrow()
    {
        const string Key = ":xxx:yyy:zzz";

        for (var size = 0; size <= 29; size++)
        {
            var (root, mount) = CreateOverlayRootWithTruncateFixtures();
            using var env = new TestEnv((root, mount));
            env.MapUserPage(0x10000u);
            env.MapUserPage(0x11000u);
            env.MapUserPage(0x12000u);
            env.MapUserPage(0x14000u);
            env.WriteCString(0x10000u, "/file");
            env.WriteCString(0x11000u, "/link");
            env.WriteCString(0x12000u, "/indirect");

            Assert.Equal(0, await env.Call("SysTruncate", 0x10000u, (uint)size));
            await AssertFileSizeAndPrefix(env, 0x10000u, size, Key);

            Assert.Equal(0, await env.Call("SysTruncate", 0x11000u, (uint)size));
            await AssertFileSizeAndPrefix(env, 0x10000u, size, Key);

            Assert.Equal(0, await env.Call("SysTruncate64", 0x12000u, (uint)size, 0));
            await AssertFileSizeAndPrefix(env, 0x10000u, size, Key);
        }
    }

    [Theory]
    [InlineData("/file/", -(int)Errno.ENOTDIR)]
    [InlineData("/link/", -(int)Errno.ENOTDIR)]
    [InlineData("/indirect/", -(int)Errno.ENOTDIR)]
    [InlineData("/broken/", -(int)Errno.ENOENT)]
    [InlineData("/file/sub", -(int)Errno.ENOTDIR)]
    [InlineData("/link/sub", -(int)Errno.ENOTDIR)]
    [InlineData("/indirect/sub", -(int)Errno.ENOTDIR)]
    [InlineData("/broken/sub", -(int)Errno.ENOENT)]
    [InlineData("/missing/sub", -(int)Errno.ENOENT)]
    public async Task Truncate_PathwalkErrors_ArePreserved(string path, int errno)
    {
        var (root, mount) = CreateOverlayRootWithTruncateFixtures();
        using var env = new TestEnv((root, mount));
        env.MapUserPage(0x13000u);
        env.WriteCString(0x13000u, path);

        Assert.Equal(errno, await env.Call("SysTruncate", 0x13000u, 5));
        Assert.Equal(errno, await env.Call("SysTruncate64", 0x13000u, 5, 0));
    }

    private static async Task AssertFileSizeAndPrefix(TestEnv env, uint pathAddr, int size, string seed)
    {
        var fd = await env.Call("SysOpen", pathAddr, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);

        var read = await env.Call("SysRead", (uint)fd, 0x14000u, 64);
        Assert.Equal(size, read);
        Assert.Equal(size, await env.Call("SysLseek", (uint)fd, 0, 2));

        var bytes = env.ReadBytes(0x14000u, Math.Max(size, 0));
        var expected = Encoding.ASCII.GetBytes(seed);

        for (var i = 0; i < size; i++)
        {
            var want = i < expected.Length ? expected[i] : (byte)0;
            Assert.Equal(want, bytes[i]);
        }

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
                var sb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "pathwalk-truncate-tmpfs",
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
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void WriteCString(uint addr, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            Assert.True(Engine.CopyToUser(addr, bytes));
        }

        public byte[] ReadBytes(uint addr, int length)
        {
            var bytes = new byte[length];
            Assert.True(Engine.CopyFromUser(addr, bytes));
            return bytes;
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

    private static (Dentry Root, Mount Mount) CreateOverlayRootWithTruncateFixtures()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "truncate-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "truncate-upper", null);

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

        lowerRoot.Inode.Symlink(new Dentry("link", null, lowerRoot, lowerSb), "file", 0, 0);
        lowerRoot.Inode.Symlink(new Dentry("indirect", null, lowerRoot, lowerSb), "link", 0, 0);
        lowerRoot.Inode.Symlink(new Dentry("broken", null, lowerRoot, lowerSb), "/missing-target", 0, 0);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "truncate-overlay",
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
