using System.Buffers.Binary;
using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class PipeTests
{
    private static ValueTask<int> CallSysPipe(TestEnv env, uint fdsAddr)
    {
        var method = typeof(SyscallManager).GetMethod("SysPipe", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fdsAddr, 0u, 0u, 0u, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysPipe2(TestEnv env, uint fdsAddr, uint flags)
    {
        var method = typeof(SyscallManager).GetMethod("SysPipe2", BindingFlags.NonPublic | BindingFlags.Static);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, fdsAddr, flags, 0u, 0u, 0u, 0u])!;
    }

    [Fact]
    public async Task Pipe_CreatesReadWriteEnds()
    {
        using var env = new TestEnv();
        const uint fdsAddr = 0x10000;
        env.MapUserPage(fdsAddr);

        var rc = await CallSysPipe(env, fdsAddr);
        Assert.Equal(0, rc);

        var (rfd, wfd) = env.ReadPipeFds(fdsAddr);
        Assert.NotEqual(rfd, wfd);

        var rFile = env.SyscallManager.GetFD(rfd);
        var wFile = env.SyscallManager.GetFD(wfd);
        Assert.NotNull(rFile);
        Assert.NotNull(wFile);

        Assert.Equal(FileFlags.O_RDONLY, rFile!.Flags);
        Assert.Equal(FileFlags.O_WRONLY, wFile!.Flags);
        Assert.Same(rFile.Dentry.Inode, wFile.Dentry.Inode);
    }

    [Fact]
    public async Task Pipe2_SetsCloexecAndNonblock_OnBothEnds()
    {
        using var env = new TestEnv();
        const uint fdsAddr = 0x11000;
        env.MapUserPage(fdsAddr);

        var flags = (uint)(FileFlags.O_CLOEXEC | FileFlags.O_NONBLOCK);
        var rc = await CallSysPipe2(env, fdsAddr, flags);
        Assert.Equal(0, rc);

        var (rfd, wfd) = env.ReadPipeFds(fdsAddr);
        var rFile = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rfd));
        var wFile = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(wfd));

        var expectedReader = FileFlags.O_RDONLY | FileFlags.O_CLOEXEC | FileFlags.O_NONBLOCK;
        var expectedWriter = FileFlags.O_WRONLY | FileFlags.O_CLOEXEC | FileFlags.O_NONBLOCK;
        Assert.Equal(expectedReader, rFile.Flags);
        Assert.Equal(expectedWriter, wFile.Flags);
    }

    [Fact]
    public async Task Pipe2_RejectsUnsupportedFlags()
    {
        using var env = new TestEnv();
        const uint fdsAddr = 0x12000;
        env.MapUserPage(fdsAddr);

        // O_DIRECT is intentionally unsupported in current implementation.
        const uint O_DIRECT = 0x4000;
        var rc = await CallSysPipe2(env, fdsAddr, O_DIRECT);
        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task Pipe2_Efault_RollsBackAllocatedFds()
    {
        using var env = new TestEnv();
        const uint invalidFdsAddr = 0xDEAD0000;
        var before = env.SyscallManager.FDs.Count;

        var rc = await CallSysPipe2(env, invalidFdsAddr, 0);
        Assert.Equal(-(int)Errno.EFAULT, rc);
        Assert.Equal(before, env.SyscallManager.FDs.Count);
    }

    [Fact]
    public async Task Pipe2_Efault_WithTaskOwner_DoesNotRecurseAndRollsBack()
    {
        using var env = new TestEnv(attachTaskOwner: true);
        const uint invalidFdsAddr = 0xDEAD0000;
        var before = env.SyscallManager.FDs.Count;

        var rc = await CallSysPipe2(env, invalidFdsAddr, 0);
        Assert.Equal(-(int)Errno.EFAULT, rc);
        Assert.Equal(before, env.SyscallManager.FDs.Count);
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv(bool attachTaskOwner = false)
        {
            Engine = new Engine();
            Vma = new VMAManager();
            if (attachTaskOwner)
            {
                Process = new Process(100, Vma, null!);
                Scheduler = new KernelScheduler();
                Task = new FiberTask(100, Process, Engine, Scheduler);
                Engine.Owner = Task;
                KernelScheduler.Current = Scheduler;
            }
            SyscallManager = new SyscallManager(Engine, Vma, 0);
            SyscallManager.MountRootHostfs(".");
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public Process? Process { get; }
        public FiberTask? Task { get; }
        public KernelScheduler? Scheduler { get; }
        public SyscallManager SyscallManager { get; }

        public void Dispose()
        {
            if (Scheduler != null) KernelScheduler.Current = null;
            GC.KeepAlive(Task);
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, LinuxConstants.PageSize, "[test]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public (int rfd, int wfd) ReadPipeFds(uint fdsAddr)
        {
            var buf = new byte[8];
            Assert.True(Engine.CopyFromUser(fdsAddr, buf));
            var rfd = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
            var wfd = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));
            return (rfd, wfd);
        }
    }
}
