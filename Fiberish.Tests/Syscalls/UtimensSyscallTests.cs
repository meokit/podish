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

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
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
