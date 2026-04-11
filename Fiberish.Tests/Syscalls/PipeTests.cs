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
    private static readonly FieldInfo AsyncWaitQueuePoolField =
        typeof(KernelScheduler).GetField("_asyncWaitQueuePool", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo ReadHandleField =
        typeof(PipeInode).GetField("_readHandle", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo DrainEventsMethod =
        typeof(KernelScheduler).GetMethod("DrainEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static ValueTask<int> CallSysRead(TestEnv env, uint fd, uint bufAddr, uint count)
    {
        var method = typeof(SyscallManager).GetMethod("SysRead", BindingFlags.NonPublic | BindingFlags.Instance);
        return (ValueTask<int>)method!.Invoke(env.SyscallManager, [env.Engine, fd, bufAddr, count, 0u, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysWrite(TestEnv env, uint fd, uint bufAddr, uint count)
    {
        var method = typeof(SyscallManager).GetMethod("SysWrite", BindingFlags.NonPublic | BindingFlags.Instance);
        return (ValueTask<int>)method!.Invoke(env.SyscallManager, [env.Engine, fd, bufAddr, count, 0u, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysClose(TestEnv env, uint fd)
    {
        var method = typeof(SyscallManager).GetMethod("SysClose", BindingFlags.NonPublic | BindingFlags.Instance);
        return (ValueTask<int>)method!.Invoke(env.SyscallManager, [env.Engine, fd, 0u, 0u, 0u, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysLseek(TestEnv env, uint fd, uint offset, uint whence)
    {
        var method = typeof(SyscallManager).GetMethod("SysLseek", BindingFlags.NonPublic | BindingFlags.Instance);
        return (ValueTask<int>)method!.Invoke(env.SyscallManager, [env.Engine, fd, offset, whence, 0u, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysSplice(TestEnv env, uint fdIn, uint offInPtr, uint fdOut, uint offOutPtr,
        uint len, uint flags)
    {
        var method = typeof(SyscallManager).GetMethod("SysSplice", BindingFlags.NonPublic | BindingFlags.Instance);
        var previous = env.Engine.CurrentSyscallManager;
        env.Engine.CurrentSyscallManager = env.SyscallManager;
        try
        {
            return (ValueTask<int>)method!.Invoke(env.SyscallManager,
                [env.Engine, fdIn, offInPtr, fdOut, offOutPtr, len, flags])!;
        }
        finally
        {
            env.Engine.CurrentSyscallManager = previous;
        }
    }

    private static ValueTask<int> CallSysPipe(TestEnv env, uint fdsAddr)
    {
        var method = typeof(SyscallManager).GetMethod("SysPipe", BindingFlags.NonPublic | BindingFlags.Instance);
        return (ValueTask<int>)method!.Invoke(env.SyscallManager, [env.Engine, fdsAddr, 0u, 0u, 0u, 0u, 0u])!;
    }

    private static ValueTask<int> CallSysPipe2(TestEnv env, uint fdsAddr, uint flags)
    {
        var method = typeof(SyscallManager).GetMethod("SysPipe2", BindingFlags.NonPublic | BindingFlags.Instance);
        return (ValueTask<int>)method!.Invoke(env.SyscallManager, [env.Engine, fdsAddr, flags, 0u, 0u, 0u, 0u])!;
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
        using var env = new TestEnv();
        const uint invalidFdsAddr = 0xDEAD0000;
        var before = env.SyscallManager.FDs.Count;

        var rc = await CallSysPipe2(env, invalidFdsAddr, 0);
        Assert.Equal(-(int)Errno.EFAULT, rc);
        Assert.Equal(before, env.SyscallManager.FDs.Count);
    }

    [Fact(Timeout = 1000)]
    public async Task Pipe_ReadOnWriteEnd_ReturnsEbadf()
    {
        using var env = new TestEnv();
        const uint fdsAddr = 0x13000;
        const uint bufAddr = 0x14000;
        env.MapUserPage(fdsAddr);
        env.MapUserPage(bufAddr);

        Assert.Equal(0, await CallSysPipe(env, fdsAddr));
        var (_, wfd) = env.ReadPipeFds(fdsAddr);

        var rc = await CallSysRead(env, (uint)wfd, bufAddr, 1);
        Assert.Equal(-(int)Errno.EBADF, rc);
    }

    [Fact(Timeout = 1000)]
    public async Task Pipe_Efault_RollsBackAllocatedFds()
    {
        using var env = new TestEnv();
        const uint invalidFdsAddr = 0xDEAD0000;
        var before = env.SyscallManager.FDs.Count;

        var rc = await CallSysPipe(env, invalidFdsAddr);
        Assert.Equal(-(int)Errno.EFAULT, rc);
        Assert.Equal(before, env.SyscallManager.FDs.Count);
    }

    [Fact(Timeout = 1000)]
    public async Task Pipe_Lseek_ReturnsEspipe()
    {
        using var env = new TestEnv();
        const uint fdsAddr = 0x15000;
        env.MapUserPage(fdsAddr);

        Assert.Equal(0, await CallSysPipe(env, fdsAddr));
        var (rfd, _) = env.ReadPipeFds(fdsAddr);

        var rc = await CallSysLseek(env, (uint)rfd, 0, 0);
        Assert.Equal(-(int)Errno.ESPIPE, rc);
    }

    [Fact(Timeout = 1000)]
    public async Task Pipe_LastClose_FinalizesInodeAndReturnsBuffer()
    {
        using var env = new TestEnv();
        const uint fdsAddr = 0x151000;
        env.MapUserPage(fdsAddr);

        Assert.Equal(0, await CallSysPipe(env, fdsAddr));
        var (rfd, wfd) = env.ReadPipeFds(fdsAddr);

        var rFile = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rfd));
        var wFile = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(wfd));
        var inode = Assert.IsType<PipeInode>(rFile.Dentry.Inode);
        Assert.Same(inode, wFile.Dentry.Inode);

        Assert.Equal(0, await CallSysClose(env, (uint)rfd));
        Assert.False(inode.IsFinalized);

        Assert.Equal(0, await CallSysClose(env, (uint)wfd));
        Assert.True(inode.IsFinalized);
        Assert.True(inode.IsCacheEvicted);
        Assert.Equal(0, inode.RefCount);
        Assert.Empty(inode.Dentries);
        Assert.Null(rFile.Dentry.Inode);
        Assert.Null(wFile.Dentry.Inode);
        Assert.False(rFile.Dentry.IsTrackedBySuperBlock);
        Assert.False(wFile.Dentry.IsTrackedBySuperBlock);

        var buffer = (byte[])typeof(PipeInode).GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(inode)!;
        Assert.Empty(buffer);
    }

    [Fact(Timeout = 1000)]
    public async Task Pipe_LastClose_ReturnsWaitQueuesToSchedulerPool()
    {
        using var env = new TestEnv();
        const uint fdsAddr = 0x152000;
        env.MapUserPage(fdsAddr);

        var pool = Assert.IsType<Stack<AsyncWaitQueue>>(AsyncWaitQueuePoolField.GetValue(env.Scheduler));
        var before = pool.Count;

        Assert.Equal(0, await CallSysPipe(env, fdsAddr));
        var (rfd, wfd) = env.ReadPipeFds(fdsAddr);

        Assert.Equal(before, pool.Count);

        Assert.Equal(0, await CallSysClose(env, (uint)rfd));
        Assert.Equal(before, pool.Count);

        Assert.Equal(0, await CallSysClose(env, (uint)wfd));
        Assert.Equal(before + 2, pool.Count);
    }

    [Fact(Timeout = 1000)]
    public async Task Pipe_Poll_ReadEndWithBufferedDataAndClosedWriter_HasPollhup()
    {
        using var env = new TestEnv();
        const uint fdsAddr = 0x16000;
        const uint dataAddr = 0x17000;
        env.MapUserPage(fdsAddr);
        env.MapUserPage(dataAddr);

        Assert.Equal(0, await CallSysPipe(env, fdsAddr));
        var (rfd, wfd) = env.ReadPipeFds(fdsAddr);
        env.WriteBytes(dataAddr, [0x2A]);
        Assert.Equal(1, await CallSysWrite(env, (uint)wfd, dataAddr, 1));
        Assert.Equal(0, await CallSysClose(env, (uint)wfd));

        var rFile = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rfd));
        var revents = rFile.Dentry.Inode!.Poll(rFile, PollEvents.POLLIN);
        Assert.True((revents & PollEvents.POLLIN) != 0);
        Assert.True((revents & PollEvents.POLLHUP) != 0);
    }

    [Fact(Timeout = 1000)]
    public async Task Pipe_RegisterWaitHandle_StaleReadableSignal_DoesNotSpuriouslyFire()
    {
        using var env = new TestEnv();
        const uint fdsAddr = 0x161000;
        env.MapUserPage(fdsAddr);

        Assert.Equal(0, await CallSysPipe(env, fdsAddr));
        var (rfd, _) = env.ReadPipeFds(fdsAddr);

        var rFile = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rfd));
        var inode = Assert.IsType<PipeInode>(rFile.Dentry.Inode);
        var readHandle = Assert.IsType<AsyncWaitQueue>(ReadHandleField.GetValue(inode));

        readHandle.Signal();
        Assert.True(readHandle.IsSignaled);

        var fired = 0;
        using var reg = inode.RegisterWaitHandle(rFile, env.Task!, () => fired++, LinuxConstants.POLLIN);
        Assert.NotNull(reg);

        for (var i = 0; i < 3; i++)
        {
            env.DrainEvents();
            await Task.Delay(1);
        }

        Assert.False(readHandle.IsSignaled);
        Assert.Equal(0, fired);
    }

    [Fact(Timeout = 1000)]
    public async Task Pipe_RegisterWaitHandle_UnreadablePipe_DoesNotSpuriouslyFire()
    {
        using var env = new TestEnv();
        const uint fdsAddr = 0x161000;
        env.MapUserPage(fdsAddr);

        Assert.Equal(0, await CallSysPipe(env, fdsAddr));
        var (rfd, wfd) = env.ReadPipeFds(fdsAddr);

        var rFile = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rfd));
        var wFile = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(wfd));
        var inode = Assert.IsType<PipeInode>(rFile.Dentry.Inode);

        var fired = 0;
        using var reg = inode.RegisterWaitHandle(rFile, env.Task!, () => fired++, LinuxConstants.POLLIN);
        Assert.NotNull(reg);

        for (var i = 0; i < 3; i++)
        {
            env.DrainEvents();
            await Task.Delay(1);
        }

        Assert.Equal(0, fired);

        Assert.Equal(1, inode.WriteFromHost(env.Task, wFile, [0x2A]));

        for (var i = 0; i < 5 && fired == 0; i++)
        {
            env.DrainEvents();
            await Task.Delay(1);
        }

        Assert.Equal(1, fired);
    }

    [Fact(Timeout = 1000)]
    public async Task Pipe_WaitForWrite_SmallAtomicWrite_WaitsForFullPipeBufSpace()
    {
        using var env = new TestEnv();
        const uint fdsAddr = 0x162000;
        env.MapUserPage(fdsAddr);

        Assert.Equal(0, await CallSysPipe(env, fdsAddr));
        var (rfd, wfd) = env.ReadPipeFds(fdsAddr);

        var rFile = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rfd));
        var wFile = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(wfd));
        var inode = Assert.IsType<PipeInode>(wFile.Dentry.Inode);

        Assert.Equal(65535, inode.WriteFromHost(env.Task, wFile, new byte[65535]));

        var pending = inode.WaitForWrite(wFile, env.Task!, PipeInode.PipeBuf).AsTask();
        Assert.False(pending.IsCompleted);

        Assert.Equal(1, inode.ReadToHost(env.Task, rFile, new byte[1]));
        for (var i = 0; i < 3; i++)
        {
            env.DrainEvents();
            await Task.Delay(1);
        }

        Assert.False(pending.IsCompleted);

        Assert.Equal(PipeInode.PipeBuf - 1, inode.ReadToHost(env.Task, rFile, new byte[PipeInode.PipeBuf - 1]));
        for (var i = 0; i < 5 && !pending.IsCompleted; i++)
        {
            env.DrainEvents();
            await Task.Delay(1);
        }

        Assert.True(pending.IsCompleted);
        Assert.Equal(AwaitResult.Completed, await pending);
    }

    [Fact(Timeout = 1000)]
    public async Task Splice_PipeWithNonNullOffsets_ReturnsEspipe()
    {
        using var env = new TestEnv();
        const uint inPipeAddr = 0x18000;
        const uint outPipeAddr = 0x19000;
        const uint offPtr = 0x1A000;
        const uint dataAddr = 0x1B000;
        env.MapUserPage(inPipeAddr);
        env.MapUserPage(outPipeAddr);
        env.MapUserPage(offPtr);
        env.MapUserPage(dataAddr);
        env.WriteBytes(offPtr, new byte[8]);
        env.WriteBytes(dataAddr, [0x7F]);

        Assert.Equal(0, await CallSysPipe(env, inPipeAddr));
        Assert.Equal(0, await CallSysPipe(env, outPipeAddr));
        var (rfd, wfdIn) = env.ReadPipeFds(inPipeAddr);
        var (_, wfd) = env.ReadPipeFds(outPipeAddr);
        Assert.Equal(1, await CallSysWrite(env, (uint)wfdIn, dataAddr, 1));

        var rcOffIn = await CallSysSplice(env, (uint)rfd, offPtr, (uint)wfd, 0, 1, 0);
        Assert.Equal(-(int)Errno.ESPIPE, rcOffIn);

        var rcOffOut = await CallSysSplice(env, (uint)rfd, 0, (uint)wfd, offPtr, 1, 0);
        Assert.Equal(-(int)Errno.ESPIPE, rcOffOut);
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly TestRuntimeFactory _runtime = new();

        public TestEnv(bool attachTaskOwner = true)
        {
            Engine = _runtime.CreateEngine();
            Vma = _runtime.CreateAddressSpace();
            if (attachTaskOwner)
            {
                Process = new Process(100, Vma, null!);
                Scheduler = new KernelScheduler();
                Task = new FiberTask(100, Process, Engine, Scheduler);
                Engine.Owner = Task;
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
            if (Scheduler != null)
                GC.KeepAlive(Task);
        }

        public void DrainEvents()
        {
            Assert.NotNull(Scheduler);
            _ = (bool)DrainEventsMethod.Invoke(Scheduler, null)!;
        }


        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]",
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

        public void WriteBytes(uint addr, byte[] data)
        {
            Assert.True(Engine.CopyToUser(addr, data));
        }
    }
}
