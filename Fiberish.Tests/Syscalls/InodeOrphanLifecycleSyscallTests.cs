using System.Reflection;
using System.Buffers.Binary;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class InodeOrphanLifecycleSyscallTests
{
    [Fact]
    public async Task Unlink_WithOpenFd_KeepsOrphanAliveUntilClose()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x10000);
        env.MapUserPage(0x11000);
        env.MapUserPage(0x12000);
        env.WriteCString(0x10000, "/orphan-open");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x10000, 0x8000 | 0x1A4, 0));
        var fd = await env.Call("SysOpen", 0x10000, (uint)FileFlags.O_RDWR, 0);
        Assert.True(fd >= 0);

        var inode = env.LookupInode("/orphan-open");
        var payload = Encoding.ASCII.GetBytes("hello-open");
        env.WriteBytes(0x11000, payload);
        Assert.Equal(payload.Length, await env.Call("SysWrite", (uint)fd, 0x11000, (uint)payload.Length));
        Assert.Equal(0, await env.Call("SysLseek", (uint)fd, 0, 0));

        Assert.Equal(0, await env.Call("SysUnlink", 0x10000));
        Assert.Equal(0, inode.LinkCount);
        Assert.Equal(1, inode.FileOpenRefCount);
        Assert.Equal(0, inode.FileMmapRefCount);
        Assert.False(inode.IsFinalized);

        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysOpen", 0x10000, (uint)FileFlags.O_RDONLY, 0));
        Assert.Equal(payload.Length, await env.Call("SysRead", (uint)fd, 0x12000, (uint)payload.Length));
        Assert.Equal(payload, env.ReadBytes(0x12000, payload.Length));

        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
        Assert.Equal(0, inode.FileOpenRefCount);
        Assert.Equal(0, inode.RefCount);
        Assert.True(inode.IsFinalized);
    }

    [Fact]
    public async Task Mmap_CloseFd_KeepsMappingReadable()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x20000);
        env.MapUserPage(0x21000);
        env.WriteCString(0x20000, "/mmap-close");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x20000, 0x8000 | 0x1A4, 0));
        var fd = await env.Call("SysOpen", 0x20000, (uint)FileFlags.O_RDWR, 0);
        Assert.True(fd >= 0);

        var inode = env.LookupInode("/mmap-close");
        var payload = Encoding.ASCII.GetBytes("hello-mmap");
        env.WriteBytes(0x21000, payload);
        Assert.Equal(payload.Length, await env.Call("SysWrite", (uint)fd, 0x21000, (uint)payload.Length));

        var mapped = await env.Call("SysMmap2", 0, LinuxConstants.PageSize, (uint)Protection.Read, (uint)MapFlags.Private,
            (uint)fd, 0);
        Assert.True(mapped > 0);

        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
        Assert.Equal(0, inode.FileOpenRefCount);
        Assert.Equal(1, inode.FileMmapRefCount);
        Assert.False(inode.IsFinalized);

        Assert.Equal(payload, env.ReadMappedBytes((uint)mapped, payload.Length));

        Assert.Equal(0, await env.Call("SysMunmap", (uint)mapped, LinuxConstants.PageSize));
        Assert.Equal(0, inode.FileMmapRefCount);
        Assert.False(inode.IsFinalized);
    }

    [Fact]
    public async Task Unlink_Mmap_CloseFd_KeepsOrphanAliveUntilMunmap()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x30000);
        env.MapUserPage(0x31000);
        env.WriteCString(0x30000, "/orphan-mmap");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x30000, 0x8000 | 0x1A4, 0));
        var fd = await env.Call("SysOpen", 0x30000, (uint)FileFlags.O_RDWR, 0);
        Assert.True(fd >= 0);

        var inode = env.LookupInode("/orphan-mmap");
        var payload = Encoding.ASCII.GetBytes("hello-orphan-mmap");
        env.WriteBytes(0x31000, payload);
        Assert.Equal(payload.Length, await env.Call("SysWrite", (uint)fd, 0x31000, (uint)payload.Length));

        var mapped = await env.Call("SysMmap2", 0, LinuxConstants.PageSize, (uint)Protection.Read, (uint)MapFlags.Private,
            (uint)fd, 0);
        Assert.True(mapped > 0);

        Assert.Equal(0, await env.Call("SysUnlink", 0x30000));
        Assert.Equal(0, inode.LinkCount);
        Assert.Equal(1, inode.FileOpenRefCount);
        Assert.Equal(1, inode.FileMmapRefCount);
        Assert.False(inode.IsFinalized);

        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
        Assert.Equal(0, inode.FileOpenRefCount);
        Assert.Equal(1, inode.FileMmapRefCount);
        Assert.False(inode.IsFinalized);

        Assert.Equal(payload, env.ReadMappedBytes((uint)mapped, payload.Length));

        Assert.Equal(0, await env.Call("SysMunmap", (uint)mapped, LinuxConstants.PageSize));
        Assert.Equal(0, inode.FileMmapRefCount);
        Assert.Equal(0, inode.RefCount);
        Assert.True(inode.IsFinalized);
    }

    [Fact]
    public async Task Mremap_Move_FileBackedMapping_PreservesMmapRefAndContent()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x50000);
        env.MapUserPage(0x51000);
        env.MapUserPage(0x52000);
        env.WriteCString(0x50000, "/mremap-file");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x50000, 0x8000 | 0x1A4, 0));
        var fd = await env.Call("SysOpen", 0x50000, (uint)FileFlags.O_RDWR, 0);
        Assert.True(fd >= 0);

        var inode = env.LookupInode("/mremap-file");
        var payload = Encoding.ASCII.GetBytes("mremap-keep-data");
        env.WriteBytes(0x51000, payload);
        Assert.Equal(payload.Length, await env.Call("SysWrite", (uint)fd, 0x51000, (uint)payload.Length));

        const uint oldMapAddr = 0x50000000;
        var mapped = await env.Call("SysMmap2", oldMapAddr, LinuxConstants.PageSize,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Fixed),
            (uint)fd, 0);
        Assert.Equal((int)oldMapAddr, mapped);

        // Block in-place growth so mremap must move.
        Assert.Equal((int)(oldMapAddr + LinuxConstants.PageSize),
            await env.Call("SysMmap2", oldMapAddr + LinuxConstants.PageSize, LinuxConstants.PageSize,
                (uint)(Protection.Read | Protection.Write),
                (uint)(MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous),
                0, 0));

        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
        Assert.Equal(1, inode.FileMmapRefCount);

        const uint MREMAP_MAYMOVE = 1;
        var newMap = await env.Call("SysMremap", oldMapAddr, LinuxConstants.PageSize, LinuxConstants.PageSize * 2,
            MREMAP_MAYMOVE, 0);
        Assert.True(newMap > 0, $"newMap={newMap}");
        Assert.NotEqual((int)oldMapAddr, newMap);
        Assert.Equal(1, inode.FileMmapRefCount);

        Assert.Equal(payload, env.ReadMappedBytes((uint)newMap, payload.Length));

        Assert.Equal(0, await env.Call("SysMunmap", (uint)newMap, LinuxConstants.PageSize * 2));
        Assert.Equal(0, inode.FileMmapRefCount);

        // Cleanup guard mapping.
        Assert.Equal(0, await env.Call("SysMunmap", oldMapAddr + LinuxConstants.PageSize, LinuxConstants.PageSize));
    }

    [Fact]
    public async Task Mremap_Move_FilePrivateCow_PreservesPrivateDirtyData()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x54000);
        env.MapUserPage(0x55000);
        env.MapUserPage(0x56000);
        env.WriteCString(0x54000, "/mremap-file-cow");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x54000, 0x8000 | 0x1A4, 0));
        var fd = await env.Call("SysOpen", 0x54000, (uint)FileFlags.O_RDWR, 0);
        Assert.True(fd >= 0);

        var inode = env.LookupInode("/mremap-file-cow");
        var filePayload = Encoding.ASCII.GetBytes("file-base-content");
        var privatePayload = Encoding.ASCII.GetBytes("private-cow-data!");
        Assert.Equal(filePayload.Length, privatePayload.Length);
        env.WriteBytes(0x55000, filePayload);
        Assert.Equal(filePayload.Length, await env.Call("SysWrite", (uint)fd, 0x55000, (uint)filePayload.Length));

        const uint oldMapAddr = 0x54000000;
        var mapped = await env.Call("SysMmap2", oldMapAddr, LinuxConstants.PageSize,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Fixed),
            (uint)fd, 0);
        Assert.Equal((int)oldMapAddr, mapped);

        // Write through MAP_PRIVATE mapping to create COW-private dirty page.
        Assert.True(env.Vma.HandleFault(oldMapAddr, true, env.Engine));
        env.WriteBytes(oldMapAddr, privatePayload);
        Assert.Equal(privatePayload, env.ReadMappedBytes(oldMapAddr, privatePayload.Length));

        // Backing file must remain unchanged.
        Assert.Equal(0, await env.Call("SysLseek", (uint)fd, 0, 0));
        Assert.Equal(filePayload.Length, await env.Call("SysRead", (uint)fd, 0x56000, (uint)filePayload.Length));
        Assert.Equal(filePayload, env.ReadBytes(0x56000, filePayload.Length));

        // Block in-place growth so mremap must move.
        Assert.Equal((int)(oldMapAddr + LinuxConstants.PageSize),
            await env.Call("SysMmap2", oldMapAddr + LinuxConstants.PageSize, LinuxConstants.PageSize,
                (uint)(Protection.Read | Protection.Write),
                (uint)(MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous),
                0, 0));

        const uint MREMAP_MAYMOVE = 1;
        var newMap = await env.Call("SysMremap", oldMapAddr, LinuxConstants.PageSize, LinuxConstants.PageSize * 2,
            MREMAP_MAYMOVE, 0);
        Assert.True(newMap > 0, $"newMap={newMap}");
        Assert.NotEqual((int)oldMapAddr, newMap);

        // Private dirty bytes must survive move.
        Assert.Equal(privatePayload, env.ReadMappedBytes((uint)newMap, privatePayload.Length));
        // Backing file must still remain unchanged.
        Assert.Equal(0, await env.Call("SysLseek", (uint)fd, 0, 0));
        Assert.Equal(filePayload.Length, await env.Call("SysRead", (uint)fd, 0x56000, (uint)filePayload.Length));
        Assert.Equal(filePayload, env.ReadBytes(0x56000, filePayload.Length));

        Assert.Equal(0, await env.Call("SysMunmap", (uint)newMap, LinuxConstants.PageSize * 2));
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
        Assert.Equal(0, inode.FileMmapRefCount);
        Assert.Equal(0, inode.FileOpenRefCount);

        // Cleanup guard mapping.
        Assert.Equal(0, await env.Call("SysMunmap", oldMapAddr + LinuxConstants.PageSize, LinuxConstants.PageSize));
    }

    [Fact]
    public async Task Mremap_Move_Anonymous_PreservesContent()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x58000);

        const uint oldMapAddr = 0x58000000;
        var mapped = await env.Call("SysMmap2", oldMapAddr, LinuxConstants.PageSize,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous),
            0, 0);
        Assert.Equal((int)oldMapAddr, mapped);

        var payload = Encoding.ASCII.GetBytes("anon-mremap-preserve");
        Assert.True(env.Vma.HandleFault(oldMapAddr, true, env.Engine));
        env.WriteBytes(oldMapAddr, payload);

        // Block in-place growth so mremap must move.
        Assert.Equal((int)(oldMapAddr + LinuxConstants.PageSize),
            await env.Call("SysMmap2", oldMapAddr + LinuxConstants.PageSize, LinuxConstants.PageSize,
                (uint)(Protection.Read | Protection.Write),
                (uint)(MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous),
                0, 0));

        const uint MREMAP_MAYMOVE = 1;
        var newMap = await env.Call("SysMremap", oldMapAddr, LinuxConstants.PageSize, LinuxConstants.PageSize * 2,
            MREMAP_MAYMOVE, 0);
        Assert.True(newMap > 0);
        Assert.NotEqual((int)oldMapAddr, newMap);
        Assert.Equal(payload, env.ReadMappedBytes((uint)newMap, payload.Length));

        Assert.Equal(0, await env.Call("SysMunmap", (uint)newMap, LinuxConstants.PageSize * 2));
        // Cleanup guard mapping.
        Assert.Equal(0, await env.Call("SysMunmap", oldMapAddr + LinuxConstants.PageSize, LinuxConstants.PageSize));
    }

    [Fact]
    public async Task Unlink_WithOpenFd_FstatAndFtruncate_StillWork()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x40000);
        env.MapUserPage(0x41000);
        env.MapUserPage(0x42000);
        env.MapUserPage(0x43000);
        env.WriteCString(0x40000, "/orphan-fops");

        Assert.Equal(0, await env.Call("SysMknodat", LinuxConstants.AT_FDCWD, 0x40000, 0x8000 | 0x1A4, 0));
        var fd = await env.Call("SysOpen", 0x40000, (uint)FileFlags.O_RDWR, 0);
        Assert.True(fd >= 0);

        var inode = env.LookupInode("/orphan-fops");
        var payload = Encoding.ASCII.GetBytes("abcdef");
        env.WriteBytes(0x41000, payload);
        Assert.Equal(payload.Length, await env.Call("SysWrite", (uint)fd, 0x41000, (uint)payload.Length));

        Assert.Equal(0, await env.Call("SysUnlink", 0x40000));
        Assert.Equal(0, inode.LinkCount);
        Assert.Equal(1, inode.FileOpenRefCount);

        Assert.Equal(0, await env.Call("SysFstat64", (uint)fd, 0x42000));
        var st = env.ReadBytes(0x42000, 96);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(st.AsSpan(20, 4)));
        Assert.Equal((ulong)payload.Length, BinaryPrimitives.ReadUInt64LittleEndian(st.AsSpan(44, 8)));

        Assert.Equal(0, await env.Call("SysFtruncate", (uint)fd, 2));
        Assert.Equal(2, await env.Call("SysLseek", (uint)fd, 0, 2));
        Assert.Equal(0, await env.Call("SysLseek", (uint)fd, 0, 0));
        Assert.Equal(2, await env.Call("SysRead", (uint)fd, 0x43000, 2));
        Assert.Equal("ab"u8.ToArray(), env.ReadBytes(0x43000, 2));

        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
        Assert.True(inode.IsFinalized);
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
            Engine.PageFaultResolver = (addr, isWrite) => Vma.HandleFault(addr, isWrite, Engine);
            SyscallManager = new SyscallManager(Engine, Vma, 0);

            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var sb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "orphan-tmpfs", null);
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
            SyscallManager.Close();
        }

        public Inode LookupInode(string path)
        {
            var loc = SyscallManager.PathWalk(path);
            Assert.True(loc.IsValid);
            Assert.NotNull(loc.Dentry?.Inode);
            return loc.Dentry!.Inode!;
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, LinuxConstants.PageSize, "[test]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void WriteCString(uint addr, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            Assert.True(Engine.CopyToUser(addr, bytes));
        }

        public void WriteBytes(uint addr, ReadOnlySpan<byte> bytes)
        {
            Assert.True(Engine.CopyToUser(addr, bytes.ToArray()));
        }

        public byte[] ReadBytes(uint addr, int len)
        {
            var buf = new byte[len];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return buf;
        }

        public byte[] ReadMappedBytes(uint addr, int len)
        {
            var start = addr & LinuxConstants.PageMask;
            var end = (addr + (uint)len + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
            for (var p = start; p < end; p += LinuxConstants.PageSize)
                Assert.True(Vma.HandleFault(p, false, Engine));
            return ReadBytes(addr, len);
        }

        public async ValueTask<int> Call(string methodName, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0,
            uint a5 = 0, uint a6 = 0)
        {
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var task = (ValueTask<int>)method!.Invoke(null, [Engine.State, a1, a2, a3, a4, a5, a6])!;
            return await task;
        }
    }
}
