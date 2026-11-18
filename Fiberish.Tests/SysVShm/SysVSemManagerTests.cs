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
        ctx.DrainEvents();
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

    [Fact]
    public void SemOp_BlockingWait_StopThenSigcont_ReturnsEintr()
    {
        using var ctx = new TestContext();
        var method = typeof(FiberTask).GetMethod("IsSyscallInterruptedByStopSignal",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var stopInterrupted = (bool)method!.Invoke(null,
            [ctx.Task, (uint)X86SyscallNumbers.ipc, (uint)LinuxConstants.SEMOP])!;

        Assert.True(stopInterrupted);
    }

    [Fact]
    public void SemCtl_IpcStat_WritesCompleteMetadata()
    {
        using var ctx = new TestContext();
        var semid = ctx.Manager.SemGet(0x1234, 3, LinuxConstants.IPC_CREAT | 0x1ED, 1000, 2000, ctx.Process);
        Assert.True(semid >= 0);

        const uint bufPtr = 0x43000;
        ctx.MapUserPage(bufPtr);

        Assert.Equal(0, ctx.Manager.SemCtl(semid, 0, LinuxConstants.IPC_STAT, bufPtr, ctx.Engine, 1000, 2000, ctx.Process));

        var data = new byte[64];
        Assert.True(ctx.Engine.CopyFromUser(bufPtr, data));

        var key = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4));
        var uid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        var gid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
        var cuid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12, 4));
        var cgid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16, 4));
        var mode = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20, 4));
        var nsems = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(52, 4));

        Assert.Equal(0x1234, key);
        Assert.Equal(1000u, uid);
        Assert.Equal(2000u, gid);
        Assert.Equal(1000u, cuid);
        Assert.Equal(2000u, cgid);
        Assert.Equal(0x1EDu, mode);
        Assert.Equal(3u, nsems);
    }

    [Fact]
    public void SemCtl_GetAll_SetAll_EnforcesRange()
    {
        using var ctx = new TestContext();
        var semid = ctx.Manager.SemGet(LinuxConstants.IPC_PRIVATE, 2, 0x1FF, 0, 0, ctx.Process);
        Assert.True(semid >= 0);

        const uint bufPtr = 0x44000;
        ctx.MapUserPage(bufPtr);

        var setVals = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(setVals.AsSpan(0, 2), 10);
        BinaryPrimitives.WriteUInt16LittleEndian(setVals.AsSpan(2, 2), 20);
        Assert.True(ctx.Engine.CopyToUser(bufPtr, setVals));

        Assert.Equal(0, ctx.Manager.SemCtl(semid, 0, LinuxConstants.SETALL, bufPtr, ctx.Engine, 0, 0, ctx.Process));

        var getBuf = new byte[4];
        const uint getPtr = 0x45000;
        ctx.MapUserPage(getPtr);
        Assert.Equal(0, ctx.Manager.SemCtl(semid, 0, LinuxConstants.GETALL, getPtr, ctx.Engine, 0, 0, ctx.Process));
        Assert.True(ctx.Engine.CopyFromUser(getPtr, getBuf));
        Assert.Equal(10, BinaryPrimitives.ReadUInt16LittleEndian(getBuf.AsSpan(0, 2)));
        Assert.Equal(20, BinaryPrimitives.ReadUInt16LittleEndian(getBuf.AsSpan(2, 2)));

        // Range overflow > 32767 returns ERANGE
        BinaryPrimitives.WriteUInt16LittleEndian(setVals.AsSpan(0, 2), 32768);
        Assert.True(ctx.Engine.CopyToUser(bufPtr, setVals));
        Assert.Equal(-(int)Errno.ERANGE, ctx.Manager.SemCtl(semid, 0, LinuxConstants.SETALL, bufPtr, ctx.Engine, 0, 0, ctx.Process));
    }

    [Fact]
    public void SemCtl_UnprivilegedAccess_ReturnsEaccesOrEperm()
    {
        using var ctx = new TestContext();
        // Mode 0: no access for nobody
        var semid = ctx.Manager.SemGet(LinuxConstants.IPC_PRIVATE, 1, 0, 0, 0, ctx.Process);
        Assert.True(semid >= 0);

        const uint bufPtr = 0x46000;
        ctx.MapUserPage(bufPtr);

        // Nobody (uid 65534) trying IPC_STAT -> EACCES
        Assert.Equal(-(int)Errno.EACCES, ctx.Manager.SemCtl(semid, 0, LinuxConstants.IPC_STAT, bufPtr, ctx.Engine, 65534, 65534));

        // Nobody trying IPC_RMID -> EPERM
        Assert.Equal(-(int)Errno.EPERM, ctx.Manager.SemCtl(semid, 0, LinuxConstants.IPC_RMID, 0, ctx.Engine, 65534, 65534));
    }

    [Fact]
    public async Task SemOp_SemUndo_RestoresValuesOnProcessExit()
    {
        using var ctx = new TestContext();
        var semid = ctx.Manager.SemGet(LinuxConstants.IPC_PRIVATE, 1, 0x1FF, 0, 0, ctx.Process);
        Assert.True(semid >= 0);

        const uint sopsPtr = 0x47000;
        ctx.MapUserPage(sopsPtr);

        // op: Add 5 with SEM_UNDO
        var sops = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(sops.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteInt16LittleEndian(sops.AsSpan(2, 2), 5);
        BinaryPrimitives.WriteInt16LittleEndian(sops.AsSpan(4, 2), (short)LinuxConstants.SEM_UNDO);
        Assert.True(ctx.Engine.CopyToUser(sopsPtr, sops));

        Assert.Equal(0, await ctx.Manager.SemOp(semid, sopsPtr, 1, ctx.Engine));
        Assert.Equal(5, ctx.Manager.SemCtl(semid, 0, LinuxConstants.GETVAL, 0, ctx.Engine, 0, 0));

        // On process exit, undo should subtract 5
        ctx.Manager.OnProcessExit(ctx.Process);
        Assert.Equal(0, ctx.Manager.SemCtl(semid, 0, LinuxConstants.GETVAL, 0, ctx.Engine, 0, 0));
    }

    private sealed class TestContext : IDisposable
    {
        private static readonly MethodInfo DrainEventsMethod =
            typeof(KernelScheduler).GetMethod("DrainEvents",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

        private readonly KernelScheduler _kernel;
        private readonly TestRuntimeFactory _runtime = new();

        public TestContext(SysVSemManager? manager = null, int processId = 2000)
        {
            Engine = _runtime.CreateEngine();
            Vma = _runtime.CreateAddressSpace();
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
        public Process Process => Task.Process;

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