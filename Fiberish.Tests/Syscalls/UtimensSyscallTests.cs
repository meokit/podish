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

public class UtimensSyscallTests
{
    private const int UtimeNow = 0x3fffffff;
    private const int UtimeOmit = 0x3ffffffe;

    [Fact]
    public async Task UtimensAt_TmpfsFile_AppliesRequestedAtimeAndMtime()
    {
        using var env = new TestEnv();
        const uint timesPtr = 0x10000;
        env.MapUserPage(timesPtr);

        var root = env.SyscallManager.Root.Dentry!;
        var file = new Dentry(FsName.FromString("stamp.txt"), null, root, root.SuperBlock);
        root.Inode!.Create(file, 0x1A4, 0, 0);

        var times = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(0, 4), 1_700_000_000);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(4, 4), 123_456_700);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(8, 4), 1_700_000_100);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(12, 4), 765_432_100);
        env.Write(timesPtr, times);

        var rc = await env.Invoke("SysUtimensAt", unchecked((uint)-100), env.WriteString("/stamp.txt"), timesPtr, 0, 0,
            0);
        Assert.Equal(0, rc);

        Assert.Equal(1_700_000_000L, new DateTimeOffset(file.Inode!.ATime).ToUnixTimeSeconds());
        Assert.Equal(1_700_000_100L, new DateTimeOffset(file.Inode.MTime).ToUnixTimeSeconds());
    }

    [Fact]
    public async Task UtimensAt_AtEmptyPath_UsesFdTarget()
    {
        using var env = new TestEnv();
        const uint timesPtr = 0x12000;
        const uint emptyPathPtr = 0x13000;
        env.MapUserPage(timesPtr);
        env.MapUserPage(emptyPathPtr);
        env.Write(emptyPathPtr, [0]);

        var root = env.SyscallManager.Root.Dentry!;
        var file = new Dentry(FsName.FromString("fd-stamp.txt"), null, root, root.SuperBlock);
        root.Inode!.Create(file, 0x1A4, 0, 0);
        var fd = env.SyscallManager.AllocFD(new LinuxFile(file, FileFlags.O_RDONLY, env.RootMount));

        var times = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(0, 4), 1_700_000_300);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(4, 4), 111_111_100);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(8, 4), 1_700_000_400);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(12, 4), 222_222_200);
        env.Write(timesPtr, times);

        var rc = await env.Invoke("SysUtimensAt", (uint)fd, emptyPathPtr, timesPtr, LinuxConstants.AT_EMPTY_PATH, 0, 0);
        Assert.Equal(0, rc);

        Assert.Equal(1_700_000_300L, new DateTimeOffset(file.Inode!.ATime).ToUnixTimeSeconds());
        Assert.Equal(1_700_000_400L, new DateTimeOffset(file.Inode.MTime).ToUnixTimeSeconds());
    }

    [Fact]
    public async Task UtimensAt_NullPath_UsesFdTarget()
    {
        using var env = new TestEnv();
        const uint timesPtr = 0x16000;
        env.MapUserPage(timesPtr);

        var root = env.SyscallManager.Root.Dentry!;
        var file = new Dentry(FsName.FromString("null-path.txt"), null, root, root.SuperBlock);
        root.Inode!.Create(file, 0x1A4, 0, 0);
        var fd = env.SyscallManager.AllocFD(new LinuxFile(file, FileFlags.O_RDONLY, env.RootMount));

        var times = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(0, 4), 1_700_000_600);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(4, 4), 333_333_300);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(8, 4), 1_700_000_700);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(12, 4), 444_444_400);
        env.Write(timesPtr, times);

        var rc = await env.Invoke("SysUtimensAt", (uint)fd, 0, timesPtr, 0, 0, 0);
        Assert.Equal(0, rc);

        Assert.Equal(1_700_000_600L, new DateTimeOffset(file.Inode!.ATime).ToUnixTimeSeconds());
        Assert.Equal(1_700_000_700L, new DateTimeOffset(file.Inode.MTime).ToUnixTimeSeconds());
    }

    [Fact]
    public async Task UtimensAt_NullPathWithAtFdcwd_ReturnsEfault()
    {
        using var env = new TestEnv();

        var rc = await env.Invoke("SysUtimensAt", unchecked((uint)-100), 0, 0, 0, 0, 0);

        Assert.Equal(-(int)Errno.EFAULT, rc);
    }

    [Fact]
    public async Task UtimensAt_NullPathWithSymlinkNofollow_ReturnsEinval()
    {
        using var env = new TestEnv();

        var root = env.SyscallManager.Root.Dentry!;
        var file = new Dentry(FsName.FromString("nofollow.txt"), null, root, root.SuperBlock);
        root.Inode!.Create(file, 0x1A4, 0, 0);
        var fd = env.SyscallManager.AllocFD(new LinuxFile(file, FileFlags.O_RDONLY, env.RootMount));

        var rc = await env.Invoke("SysUtimensAt", (uint)fd, 0, 0, LinuxConstants.AT_SYMLINK_NOFOLLOW, 0, 0);

        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task UtimensAt_AtEmptyPathWithAtFdcwd_UsesCurrentWorkingDirectory()
    {
        using var env = new TestEnv();
        const uint emptyPathPtr = 0x17000;
        env.MapUserPage(emptyPathPtr);
        env.Write(emptyPathPtr, [0]);

        var rootInode = env.SyscallManager.Root.Dentry!.Inode!;
        rootInode.UpdateTimes(
            DateTimeOffset.FromUnixTimeSeconds(100).UtcDateTime,
            DateTimeOffset.FromUnixTimeSeconds(200).UtcDateTime,
            DateTimeOffset.FromUnixTimeSeconds(300).UtcDateTime);
        var beforeAtime = rootInode.ATime;
        var beforeMtime = rootInode.MTime;

        var rc = await env.Invoke(
            "SysUtimensAt",
            unchecked((uint)-100),
            emptyPathPtr,
            0,
            LinuxConstants.AT_EMPTY_PATH,
            0,
            0);

        Assert.Equal(0, rc);
        Assert.NotEqual(beforeAtime, rootInode.ATime);
        Assert.NotEqual(beforeMtime, rootInode.MTime);
    }

    [Fact]
    public async Task UtimensAt_TimesNull_UsesCurrentTime()
    {
        using var env = new TestEnv();

        var root = env.SyscallManager.Root.Dentry!;
        var file = new Dentry(FsName.FromString("times-null.txt"), null, root, root.SuperBlock);
        root.Inode!.Create(file, 0x1A4, 0, 0);
        file.Inode!.UpdateTimes(
            DateTimeOffset.FromUnixTimeSeconds(400).UtcDateTime,
            DateTimeOffset.FromUnixTimeSeconds(500).UtcDateTime,
            DateTimeOffset.FromUnixTimeSeconds(600).UtcDateTime);
        var beforeAtime = file.Inode!.ATime;
        var beforeMtime = file.Inode.MTime;

        var rc = await env.Invoke("SysUtimensAt", unchecked((uint)-100), env.WriteString("/times-null.txt"), 0, 0, 0, 0);

        Assert.Equal(0, rc);
        Assert.NotEqual(beforeAtime, file.Inode.ATime);
        Assert.NotEqual(beforeMtime, file.Inode.MTime);
    }

    [Fact]
    public async Task UtimensAt_NonOwnerWithWriteAccess_CanUseUtimeNow()
    {
        using var env = new TestEnv();
        const uint timesPtr = 0x14000;
        env.MapUserPage(timesPtr);
        env.SetCreds(uid: 1234, gid: 1234);

        var root = env.SyscallManager.Root.Dentry!;
        var file = new Dentry(FsName.FromString("now.txt"), null, root, root.SuperBlock);
        root.Inode!.Create(file, 0x1B6, 0, 0);

        var times = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(4, 4), UtimeNow);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(12, 4), UtimeNow);
        env.Write(timesPtr, times);

        var rc = await env.Invoke("SysUtimensAt", unchecked((uint)-100), env.WriteString("/now.txt"), timesPtr, 0, 0, 0);

        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task UtimensAt_NonOwnerWithWriteAccess_CannotSetExplicitTimes()
    {
        using var env = new TestEnv();
        const uint timesPtr = 0x15000;
        env.MapUserPage(timesPtr);
        env.SetCreds(uid: 1234, gid: 1234);

        var root = env.SyscallManager.Root.Dentry!;
        var file = new Dentry(FsName.FromString("explicit.txt"), null, root, root.SuperBlock);
        root.Inode!.Create(file, 0x1B6, 0, 0);

        var times = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(4, 4), UtimeOmit);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(8, 4), 1_700_000_500);
        BinaryPrimitives.WriteInt32LittleEndian(times.AsSpan(12, 4), 0);
        env.Write(timesPtr, times);

        var rc = await env.Invoke(
            "SysUtimensAt",
            unchecked((uint)-100),
            env.WriteString("/explicit.txt"),
            timesPtr,
            0,
            0,
            0);

        Assert.Equal(-(int)Errno.EPERM, rc);
    }

    [Fact]
    public async Task UtimensAtTime64_NullPath_UsesFdTarget()
    {
        using var env = new TestEnv();
        const uint timesPtr = 0x18000;
        env.MapUserPage(timesPtr);

        var root = env.SyscallManager.Root.Dentry!;
        var file = new Dentry(FsName.FromString("null-path-time64.txt"), null, root, root.SuperBlock);
        root.Inode!.Create(file, 0x1A4, 0, 0);
        var fd = env.SyscallManager.AllocFD(new LinuxFile(file, FileFlags.O_RDONLY, env.RootMount));

        var times = new byte[32];
        BinaryPrimitives.WriteInt64LittleEndian(times.AsSpan(0, 8), 1_700_000_800);
        BinaryPrimitives.WriteInt64LittleEndian(times.AsSpan(8, 8), 555_555_500);
        BinaryPrimitives.WriteInt64LittleEndian(times.AsSpan(16, 8), 1_700_000_900);
        BinaryPrimitives.WriteInt64LittleEndian(times.AsSpan(24, 8), 666_666_600);
        env.Write(timesPtr, times);

        var rc = await env.Invoke("SysUtimensAtTime64", (uint)fd, 0, timesPtr, 0, 0, 0);
        Assert.Equal(0, rc);

        Assert.Equal(1_700_000_800L, new DateTimeOffset(file.Inode!.ATime).ToUnixTimeSeconds());
        Assert.Equal(1_700_000_900L, new DateTimeOffset(file.Inode.MTime).ToUnixTimeSeconds());
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly TestRuntimeFactory _runtime = new();

        public TestEnv()
        {
            Engine = _runtime.CreateEngine();
            Vma = _runtime.CreateAddressSpace();
            Process = new Process(100, Vma, null!);
            Scheduler = new KernelScheduler();
            Task = new FiberTask(100, Process, Engine, Scheduler);
            Engine.Owner = Task;
            SyscallManager = new SyscallManager(Engine, Vma, 0);

            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "utimens-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root)
            {
                Source = "tmpfs",
                FsType = "tmpfs",
                Options = "rw"
            };
            RootMount = rootMount;
            SyscallManager.InitializeRoot(rootSb.Root, rootMount);
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public Process Process { get; }
        public KernelScheduler Scheduler { get; }
        public FiberTask Task { get; }
        public SyscallManager SyscallManager { get; }
        public Mount RootMount { get; }

        public void SetCreds(int uid, int gid, params int[] groups)
        {
            Process.UID = uid;
            Process.EUID = uid;
            Process.SUID = uid;
            Process.FSUID = uid;
            Process.GID = gid;
            Process.EGID = gid;
            Process.SGID = gid;
            Process.FSGID = gid;
            Process.SupplementaryGroups.Clear();
            Process.SupplementaryGroups.AddRange(groups);
        }

        public void Dispose()
        {
            SyscallManager.Close();
        }

        public ValueTask<int> Invoke(string methodName, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
        {
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            return (ValueTask<int>)method!.Invoke(SyscallManager, [Engine, a1, a2, a3, a4, a5, a6])!;
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void Write(uint addr, ReadOnlySpan<byte> data)
        {
            Assert.True(Engine.CopyToUser(addr, data));
        }

        public uint WriteString(string value)
        {
            const uint addr = 0x11000;
            MapUserPage(addr);
            Write(addr, Encoding.UTF8.GetBytes(value + "\0"));
            return addr;
        }
    }
}
