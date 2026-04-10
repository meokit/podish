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

public class RawPathnameSyscallTests
{
    [Fact]
    public async Task Tmpfs_Utf8Pathnames_RoundTripThroughSyscalls()
    {
        using var env = new TestEnv();

        var dirPath = Encoding.UTF8.GetBytes("/目录😀");
        var filePath = Encoding.UTF8.GetBytes("/目录😀/文件🙂.txt");
        var renamedPath = Encoding.UTF8.GetBytes("/目录😀/重命名🚀");
        var linkPath = Encoding.UTF8.GetBytes("/链接🙂");
        var xattrName = Encoding.UTF8.GetBytes("user.标签😀");
        var xattrValue = Encoding.UTF8.GetBytes("值🙂");

        Assert.Equal(0, await env.Call("SysMkdir", env.WriteCString(dirPath), 0x1FFu));

        var fd = await env.Call("SysOpen", env.WriteCString(filePath), (uint)(FileFlags.O_CREAT | FileFlags.O_RDWR), 0x1A4u);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        Assert.Equal(0, await env.Call("SysSetXAttr", env.WriteCString(filePath), env.WriteCString(xattrName),
            env.WriteBytes(xattrValue), (uint)xattrValue.Length, 0));

        var xattrBuf = env.AllocRegion(128);
        var getXAttrRc = await env.Call("SysGetXAttr", env.WriteCString(filePath), env.WriteCString(xattrName),
            xattrBuf, 128u);
        Assert.Equal(xattrValue.Length, getXAttrRc);
        Assert.Equal(xattrValue, env.ReadBytes(xattrBuf, getXAttrRc));

        var xattrListBuf = env.AllocRegion(128);
        var listXAttrRc = await env.Call("SysListXAttr", env.WriteCString(filePath), xattrListBuf, 128u);
        Assert.Contains(ParseNulList(env.ReadBytes(xattrListBuf, listXAttrRc)), item => item.SequenceEqual(xattrName));

        Assert.Equal(0, await env.Call("SysSymlink", env.WriteCString(filePath), env.WriteCString(linkPath)));

        var readlinkBuf = env.AllocRegion(256);
        var readlinkRc = await env.Call("SysReadlink", env.WriteCString(linkPath), readlinkBuf, 256u);
        Assert.Equal(filePath.Length, readlinkRc);
        Assert.Equal(filePath, env.ReadBytes(readlinkBuf, readlinkRc));

        Assert.Equal(0, await env.Call("SysRename", env.WriteCString(filePath), env.WriteCString(renamedPath)));
        Assert.Equal(-(int)Errno.ENOENT,
            await env.Call("SysOpen", env.WriteCString(filePath), (uint)FileFlags.O_RDONLY));

        fd = await env.Call("SysOpen", env.WriteCString(renamedPath), (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        Assert.Equal(0, await env.Call("SysChdir", env.WriteCString(dirPath)));
        var cwdBuf = env.AllocRegion(256);
        var getcwdRc = await env.Call("SysGetCwd", cwdBuf, 256u);
        Assert.Equal(dirPath, env.ReadBytes(cwdBuf, getcwdRc - 1));

        var dirFd = await env.Call("SysOpen", env.WriteCString(dirPath), (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY));
        Assert.True(dirFd >= 0);
        var dentsBuf = env.AllocRegion(512);
        var dentsRc = await env.Call("SysGetdents64", (uint)dirFd, dentsBuf, 512u);
        Assert.Contains(ParseGetdents64Names(env.ReadBytes(dentsBuf, dentsRc)), item => item.SequenceEqual("重命名🚀"u8.ToArray()));
        Assert.Equal(0, await env.Call("SysClose", (uint)dirFd));
    }

    [Fact]
    public async Task Tmpfs_InvalidUtf8Pathnames_RoundTripThroughSyscalls()
    {
        using var env = new TestEnv();

        var badPath = new byte[] { (byte)'/', (byte)'b', (byte)'a', 0xFF, (byte)'d' };
        var renamedPath = new byte[] { (byte)'/', (byte)'r', 0xC3, 0x28, (byte)'x' };
        var xattrName = new byte[] { (byte)'u', (byte)'s', (byte)'e', (byte)'r', (byte)'.', 0xFF, (byte)'t' };
        var xattrValue = new byte[] { 1, 2, 3, 4 };
        var symlinkPath = Encoding.UTF8.GetBytes("/lnk");

        var fd = await env.Call("SysOpen", env.WriteCString(badPath), (uint)(FileFlags.O_CREAT | FileFlags.O_RDWR), 0x1A4u);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        Assert.Equal(0, await env.Call("SysRename", env.WriteCString(badPath), env.WriteCString(renamedPath)));

        fd = await env.Call("SysOpen", env.WriteCString(renamedPath), (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        Assert.Equal(0, await env.Call("SysSetXAttr", env.WriteCString(renamedPath), env.WriteCString(xattrName),
            env.WriteBytes(xattrValue), (uint)xattrValue.Length, 0));

        var xattrBuf = env.AllocRegion(64);
        var getXAttrRc = await env.Call("SysGetXAttr", env.WriteCString(renamedPath), env.WriteCString(xattrName),
            xattrBuf, 64u);
        Assert.Equal(xattrValue.Length, getXAttrRc);
        Assert.Equal(xattrValue, env.ReadBytes(xattrBuf, getXAttrRc));

        var xattrListBuf = env.AllocRegion(64);
        var listXAttrRc = await env.Call("SysListXAttr", env.WriteCString(renamedPath), xattrListBuf, 64u);
        Assert.Contains(ParseNulList(env.ReadBytes(xattrListBuf, listXAttrRc)), item => item.SequenceEqual(xattrName));

        Assert.Equal(0, await env.Call("SysSymlink", env.WriteCString(renamedPath), env.WriteCString(symlinkPath)));

        var readlinkBuf = env.AllocRegion(64);
        var readlinkRc = await env.Call("SysReadlink", env.WriteCString(symlinkPath), readlinkBuf, 64u);
        Assert.Equal(renamedPath.Length, readlinkRc);
        Assert.Equal(renamedPath, env.ReadBytes(readlinkBuf, readlinkRc));

        var dirFd = await env.Call("SysOpen", env.WriteCString("/"u8), (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY));
        Assert.True(dirFd >= 0);
        var dentsBuf = env.AllocRegion(512);
        var dentsRc = await env.Call("SysGetdents64", (uint)dirFd, dentsBuf, 512u);
        Assert.Contains(ParseGetdents64Names(env.ReadBytes(dentsBuf, dentsRc)), item => item.SequenceEqual(renamedPath[1..]));
        Assert.Equal(0, await env.Call("SysClose", (uint)dirFd));
    }

    [Fact]
    public async Task Silkfs_InvalidUtf8Pathnames_RoundTripThroughSyscalls()
    {
        using var env = new TestEnv();
        env.MountSilkfsAt("/mnt");

        var badPath = new byte[] { (byte)'/', (byte)'m', (byte)'n', (byte)'t', (byte)'/', 0xF5, (byte)'x' };

        var fd = await env.Call("SysOpen", env.WriteCString(badPath), (uint)(FileFlags.O_CREAT | FileFlags.O_RDWR), 0x1A4u);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        fd = await env.Call("SysOpen", env.WriteCString(badPath), (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));

        var dirFd = await env.Call("SysOpen", env.WriteCString("/mnt"u8), (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY));
        Assert.True(dirFd >= 0);
        var dentsBuf = env.AllocRegion(512);
        var dentsRc = await env.Call("SysGetdents64", (uint)dirFd, dentsBuf, 512u);
        Assert.Contains(ParseGetdents64Names(env.ReadBytes(dentsBuf, dentsRc)), item => item.SequenceEqual(new byte[] { 0xF5, (byte)'x' }));
        Assert.Equal(0, await env.Call("SysClose", (uint)dirFd));
    }

    private static List<byte[]> ParseNulList(byte[] data)
    {
        var result = new List<byte[]>();
        var start = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != 0)
                continue;
            result.Add(data[start..i]);
            start = i + 1;
        }

        return result;
    }

    private static List<byte[]> ParseGetdents64Names(byte[] data)
    {
        var result = new List<byte[]>();
        var offset = 0;
        while (offset < data.Length)
        {
            var reclen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 16, 2));
            if (reclen == 0)
                break;
            var nameStart = offset + 19;
            var nameEnd = nameStart;
            while (nameEnd < offset + reclen && data[nameEnd] != 0)
                nameEnd++;
            result.Add(data[nameStart..nameEnd]);
            offset += reclen;
        }

        return result;
    }

    private sealed class TestEnv : IDisposable
    {
        private uint _nextAddr = 0x10000;
        private readonly List<string> _tempDirs = [];

        public TestEnv()
        {
            Engine = new Engine();
            Memory = new VMAManager();
            Engine.PageFaultResolver = (addr, isWrite) => Memory.HandleFault(addr, isWrite, Engine);
            Process = new Process(100, Memory, null!);
            Scheduler = new KernelScheduler();
            Task = new FiberTask(100, Process, Engine, Scheduler);
            Scheduler.CurrentTask = Task;
            Engine.Owner = Task;
            SyscallManager = new SyscallManager(Engine, Memory, 0);

            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var sb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "raw-path-tests", null);
            var mount = new Mount(sb, sb.Root)
            {
                Source = "tmpfs",
                FsType = "tmpfs",
                Options = "rw"
            };
            SyscallManager.InitializeRoot(sb.Root, mount);

            Process.UID = Process.EUID = Process.SUID = Process.FSUID = 0;
            Process.GID = Process.EGID = Process.SGID = Process.FSGID = 0;
        }

        public Engine Engine { get; }
        public VMAManager Memory { get; }
        public Process Process { get; }
        public KernelScheduler Scheduler { get; }
        public FiberTask Task { get; }
        public SyscallManager SyscallManager { get; }

        public void Dispose()
        {
            SyscallManager.Close();
            Scheduler.CurrentTask = null;
            foreach (var dir in _tempDirs)
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
        }

        public uint AllocRegion(int size)
        {
            var addr = _nextAddr;
            var pageAlignedSize = (size + LinuxConstants.PageSize - 1) & ~(LinuxConstants.PageSize - 1);
            for (var offset = 0; offset < pageAlignedSize; offset += LinuxConstants.PageSize)
                MapUserPage(addr + (uint)offset);
            _nextAddr += (uint)pageAlignedSize;
            return addr;
        }

        public uint WriteBytes(ReadOnlySpan<byte> data)
        {
            var addr = AllocRegion(data.Length);
            Assert.True(Engine.CopyToUser(addr, data));
            return addr;
        }

        public uint WriteCString(ReadOnlySpan<byte> data)
        {
            var buf = new byte[data.Length + 1];
            data.CopyTo(buf);
            return WriteBytes(buf);
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

        public void MountSilkfsAt(string guestPath)
        {
            EnsureDirectory(guestPath);
            var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-e2e-{Guid.NewGuid():N}");
            _tempDirs.Add(silkRoot);
            var fsCtx = SyscallManager.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, SyscallManager.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = SyscallManager.PathWalkWithFlags(guestPath, LookupFlags.FollowSymlink);
            Assert.True(target.IsValid);
            Assert.Equal(0, SyscallManager.AttachDetachedMount(mount!, target));
        }

        private void EnsureDirectory(string guestPath)
        {
            if (guestPath == "/")
                return;

            var parentPath = Path.GetDirectoryName(guestPath)!.Replace('\\', '/');
            if (string.IsNullOrEmpty(parentPath))
                parentPath = "/";
            var name = Path.GetFileName(guestPath);
            var parent = SyscallManager.PathWalkWithFlags(parentPath, LookupFlags.FollowSymlink);
            Assert.True(parent.IsValid);
            if (parent.Dentry!.TryGetCachedChild(name, out _))
                return;
            var dentry = new Dentry(name, null, parent.Dentry, parent.Dentry.SuperBlock);
            Assert.Equal(0, parent.Dentry.Inode!.Mkdir(dentry, 0x1FF, 0, 0));
            parent.Dentry.CacheChild(dentry, "test");
        }

        private void MapUserPage(uint addr)
        {
            Memory.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", Engine);
            Assert.True(Memory.HandleFault(addr, true, Engine));
        }
    }
}
