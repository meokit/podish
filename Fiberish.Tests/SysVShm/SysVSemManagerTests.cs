using System.Buffers.Binary;
using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.SysVShm;

public class SysVSemManagerTests
{
    [Fact]
    public void SemGet_CreatesNewSet_AndReturnsValidId()
    {
        using var ctx = new TestContext();

        var semid = ctx.Manager.SemGet(0x1111, 2, LinuxConstants.IPC_CREAT | 0x1FF, 0, 0);
        Assert.True(semid >= 0);

        // Same key, should return same semid without IPC_CREAT if it already exists
        var semid2 = ctx.Manager.SemGet(0x1111, 2, 0, 0, 0);
        Assert.Equal(semid, semid2);
    }

    [Fact]
    public void SemGet_ExclFailsIfAlreadyExists()
    {
        using var ctx = new TestContext();
        var semid = ctx.Manager.SemGet(0x2222, 1, LinuxConstants.IPC_CREAT | 0x1FF, 0, 0);
        Assert.True(semid >= 0);

        var semid2 = ctx.Manager.SemGet(0x2222, 1, LinuxConstants.IPC_CREAT | LinuxConstants.IPC_EXCL | 0x1FF, 0, 0);
        Assert.Equal(-(int)Errno.EEXIST, semid2);
    }

    [Fact]
    public void SemCtl_SetVal_And_GetVal()
    {
        using var ctx = new TestContext();
        var semid = ctx.Manager.SemGet(LinuxConstants.IPC_PRIVATE, 3, 0x1FF, 0, 0);
        Assert.True(semid >= 0);

        // Set semaphore 1 to 42
        var ret = ctx.Manager.SemCtl(semid, 1, LinuxConstants.SETVAL, 42, ctx.Engine, 0, 0);
        Assert.Equal(0, ret);

        // Get semaphore 1
        var val = ctx.Manager.SemCtl(semid, 1, LinuxConstants.GETVAL, 0, ctx.Engine, 0, 0);
        Assert.Equal(42, val);
    }

    [Fact]
    public void SemCtl_RmId_DeletesSemaphoreSet()
    {
        using var ctx = new TestContext();
        var semid = ctx.Manager.SemGet(LinuxConstants.IPC_PRIVATE, 1, 0x1FF, 0, 0);
        Assert.True(semid >= 0);

        var ret = ctx.Manager.SemCtl(semid, 0, LinuxConstants.IPC_RMID, 0, ctx.Engine, 0, 0);
        Assert.Equal(0, ret);

        // Accessing it again should yield EINVAL
        var val = ctx.Manager.SemCtl(semid, 0, LinuxConstants.GETVAL, 0, ctx.Engine, 0, 0);
        Assert.Equal(-(int)Errno.EINVAL, val);
    }

    [Fact]
    public async Task SemOp_Increment_And_NowaitDecrement()
    {
        using var ctx = new TestContext();
        var semid = ctx.Manager.SemGet(LinuxConstants.IPC_PRIVATE, 1, 0x1FF, 0, 0);

        // semop buffers mapped in user memory
        uint sopsPtr = 0x40000;
        ctx.MapUserPage(sopsPtr);

        // op 1: Increment by 2 (sem_num=0, sem_op=2, sem_flg=0)
        var sops1 = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(sops1.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteInt16LittleEndian(sops1.AsSpan(2, 2), 2);
        BinaryPrimitives.WriteInt16LittleEndian(sops1.AsSpan(4, 2), 0);
        ctx.Engine.CopyToUser(sopsPtr, sops1);

        var ret = await ctx.Manager.SemOp(semid, sopsPtr, 1, ctx.Engine);
        Assert.Equal(0, ret);
        Assert.Equal(2, ctx.Manager.SemCtl(semid, 0, LinuxConstants.GETVAL, 0, ctx.Engine, 0, 0));

        // op 2: Decrement by 3 with IPC_NOWAIT (sem_num=0, sem_op=-3, sem_flg=IPC_NOWAIT)
        var sops2 = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(sops2.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteInt16LittleEndian(sops2.AsSpan(2, 2), -3);
        BinaryPrimitives.WriteInt16LittleEndian(sops2.AsSpan(4, 2),
            LinuxConstants.IPC_NOWAIT);
        ctx.Engine.CopyToUser(sopsPtr, sops2);

        // Should return EAGAIN immediately
        ret = await ctx.Manager.SemOp(semid, sopsPtr, 1, ctx.Engine);
        Assert.Equal(-(int)Errno.EAGAIN, ret);

        // Value remains unchanged
        Assert.Equal(2, ctx.Manager.SemCtl(semid, 0, LinuxConstants.GETVAL, 0, ctx.Engine, 0, 0));
    }

    [Fact]
    public async Task SemOp_BlockingWait_CompletesAfterSetVal()
    {
        using var ctx = new TestContext();
        var semid = ctx.Manager.SemGet(LinuxConstants.IPC_PRIVATE, 1, 0x1FF, 0, 0);

        const uint sopsPtr = 0x41000;
        ctx.MapUserPage(sopsPtr);

        var waitForOne = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(waitForOne.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteInt16LittleEndian(waitForOne.AsSpan(2, 2), -1);
        BinaryPrimitives.WriteInt16LittleEndian(waitForOne.AsSpan(4, 2), 0);
        ctx.Engine.CopyToUser(sopsPtr, waitForOne);

        var pending = ctx.Manager.SemOp(semid, sopsPtr, 1, ctx.Engine).AsTask();
        Assert.False(pending.IsCompleted);

        Assert.Equal(0, ctx.Manager.SemCtl(semid, 0, LinuxConstants.SETVAL, 1, ctx.Engine, 0, 0));
        var rc = await pending.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, rc);
        Assert.Equal(0, ctx.Manager.SemCtl(semid, 0, LinuxConstants.GETVAL, 0, ctx.Engine, 0, 0));
    }

    [Fact]
    public async Task SemOp_BlockingWait_InterruptedBySignal_ReturnsEintr()
    {
        using var ctx = new TestContext();
        var semid = ctx.Manager.SemGet(LinuxConstants.IPC_PRIVATE, 1, 0x1FF, 0, 0);

        const uint sopsPtr = 0x42000;
        ctx.MapUserPage(sopsPtr);

        var waitForOne = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(waitForOne.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteInt16LittleEndian(waitForOne.AsSpan(2, 2), -1);
        BinaryPrimitives.WriteInt16LittleEndian(waitForOne.AsSpan(4, 2), 0);
        ctx.Engine.CopyToUser(sopsPtr, waitForOne);

        var pending = ctx.Manager.SemOp(semid, sopsPtr, 1, ctx.Engine).AsTask();
        Assert.False(pending.IsCompleted);

        ctx.Task.PostSignal((int)Signal.SIGUSR1);
        ctx.DrainEvents();

        var rc = await pending.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(-(int)Errno.EINTR, rc);
    }

    private sealed class TestContext : IDisposable
    {
        private static readonly MethodInfo DrainEventsMethod =
            typeof(KernelScheduler).GetMethod("DrainEvents",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

        private readonly KernelScheduler _kernel;

        public TestContext(SysVSemManager? manager = null, int processId = 2000)
        {
            Engine = new Engine();
            Vma = new VMAManager();
            Manager = manager ?? new SysVSemManager();

            _kernel = new KernelScheduler();
            
            var process = new Process(processId, Vma, null!)
            {
                EUID = 0,
                EGID = 0
            };
            Task = new FiberTask(processId, process, Engine, _kernel);
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public SysVSemManager Manager { get; }
        public FiberTask Task { get; }

        public void Dispose()
        {
            
            GC.KeepAlive(Task);
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void DrainEvents()
        {
            _ = (bool)DrainEventsMethod.Invoke(_kernel, null)!;
        }
    }
}