using System.Buffers.Binary;
using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class CloneThreadLifecycleTests
{
    private const int AllocationWarmupIterations = 16;
    private const int AllocationMeasureIterations = 256;

    private delegate ValueTask<int> SyscallInvoker(SyscallManager manager, Engine engine, uint a1, uint a2, uint a3,
        uint a4, uint a5, uint a6);

    private delegate bool ResolveFutexKeyInvoker(SyscallManager manager, Engine engine, uint uaddr, bool fshared,
        out FutexKey key, out int error);

    private static readonly SyscallInvoker SysFutexMethod =
        (SyscallInvoker)typeof(SyscallManager)
            .GetMethod("SysFutex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate(typeof(SyscallInvoker));

    private static readonly ResolveFutexKeyInvoker ResolveFutexKeyMethod =
        (ResolveFutexKeyInvoker)typeof(SyscallManager)
            .GetMethod("TryResolveFutexKey",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                [typeof(Engine), typeof(uint), typeof(bool), typeof(FutexKey).MakeByRefType(),
                    typeof(int).MakeByRefType()],
                null)!
            .CreateDelegate(typeof(ResolveFutexKeyInvoker));

    [Fact]
    public async Task SysClone_UsesX86RawOrder_TlsFromA4_CtidFromA5()
    {
        using var env = new TestEnv(100, 100);

        const uint ctidPtr = 0x00400000;
        const uint tlsPtr = 0x00500000;
        const uint tlsBase = 0x12345000;
        env.MapUserPage(ctidPtr);
        env.MapUserPage(tlsPtr);

        var tlsDesc = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(tlsDesc.AsSpan(4, 4), tlsBase);
        Assert.True(env.Engine.CopyToUser(tlsPtr, tlsDesc));

        var flags = LinuxConstants.CLONE_VM |
                    LinuxConstants.CLONE_SIGHAND |
                    LinuxConstants.CLONE_THREAD |
                    LinuxConstants.CLONE_SETTLS |
                    LinuxConstants.CLONE_CHILD_SETTID |
                    LinuxConstants.CLONE_CHILD_CLEARTID;

        var childTid = await CallSys(env.SyscallManager, env.Engine, "SysClone", flags, 0, 0, tlsPtr, ctidPtr);
        Assert.True(childTid > 0);

        var child = env.Scheduler.GetTask(childTid);
        Assert.NotNull(child);
        Assert.Equal(ctidPtr, child!.ChildClearTidPtr);
        Assert.Equal(tlsBase, child.CPU.GetSegBase(Seg.GS));

        var tidBuf = new byte[4];
        Assert.True(env.Engine.CopyFromUser(ctidPtr, tidBuf));
        Assert.Equal(childTid, BinaryPrimitives.ReadInt32LittleEndian(tidBuf));
    }

    [Fact]
    public async Task SysClone_ThreadWithoutVm_ReturnsEinval()
    {
        using var env = new TestEnv(110, 110);

        var rc = await CallSys(env.SyscallManager, env.Engine, "SysClone", LinuxConstants.CLONE_THREAD);
        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task SysClone_ThreadWithoutSighand_ReturnsEinval()
    {
        using var env = new TestEnv(111, 111);

        var flags = LinuxConstants.CLONE_VM | LinuxConstants.CLONE_THREAD;
        var rc = await CallSys(env.SyscallManager, env.Engine, "SysClone", flags);
        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task SysClone_SetTlsReadFailure_RollsBackThreadChild()
    {
        using var env = new TestEnv(112, 112);
        const uint invalidTlsPtr = 0x00D00000;
        var flags = LinuxConstants.CLONE_VM |
                    LinuxConstants.CLONE_SIGHAND |
                    LinuxConstants.CLONE_THREAD |
                    LinuxConstants.CLONE_SETTLS;

        var rc = await CallSys(env.SyscallManager, env.Engine, "SysClone", flags, 0, 0, invalidTlsPtr);
        Assert.Equal(-(int)Errno.EFAULT, rc);
        Assert.Single(env.Process.Threads);
        Assert.Same(env.Task, env.Process.Threads[0]);
    }

    [Fact]
    public async Task SysClone_ParentSetTidWriteFailure_RollsBackProcessChild()
    {
        using var env = new TestEnv(113, 113);
        const uint invalidPtidPtr = 0x00E00000;
        var flags = LinuxConstants.CLONE_PARENT_SETTID;

        var rc = await CallSys(env.SyscallManager, env.Engine, "SysClone", flags, 0, invalidPtidPtr);
        Assert.Equal(-(int)Errno.EFAULT, rc);
        Assert.Empty(env.Process.Children);
        Assert.Single(env.Process.Threads);
        Assert.Same(env.Task, env.Process.Threads[0]);
        Assert.Empty(env.Scheduler.GetProcessesSnapshot());
    }

    [Fact]
    public async Task SysClone_NewNsWithoutNamespaceSupport_ReturnsEinval()
    {
        using var env = new TestEnv(114, 114);

        var rc = await CallSys(env.SyscallManager, env.Engine, "SysClone", LinuxConstants.CLONE_NEWNS);

        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task SysClone_PtraceRelatedHints_AreAcceptedAsNoOps()
    {
        using var env = new TestEnv(115, 115);

        var flags = LinuxConstants.CLONE_PTRACE | LinuxConstants.CLONE_UNTRACED | LinuxConstants.CLONE_DETACHED;
        var childTid = await CallSys(env.SyscallManager, env.Engine, "SysClone", flags);

        Assert.True(childTid > 0);
        Assert.NotNull(env.Scheduler.GetTask(childTid));
        Assert.Contains(env.Process.Children, pid => pid == childTid);
    }

    [Fact]
    public async Task SysClone_ThreadWithSysvsem_IsAccepted()
    {
        using var env = new TestEnv(116, 116);

        var flags = LinuxConstants.CLONE_VM |
                    LinuxConstants.CLONE_SIGHAND |
                    LinuxConstants.CLONE_THREAD |
                    LinuxConstants.CLONE_SYSVSEM;
        var childTid = await CallSys(env.SyscallManager, env.Engine, "SysClone", flags);

        Assert.True(childTid > 0);
        var child = env.Scheduler.GetTask(childTid);
        Assert.NotNull(child);
        Assert.Same(env.Process, child!.Process);
        Assert.Equal(2, env.Process.Threads.Count);
    }

    [Fact]
    public async Task SysExit_ClearsChildTidAndWakesFutexWaiters()
    {
        using var env = new TestEnv(200, 201);
        var siblingRuntime = new TestRuntimeFactory();
        var sibling = new FiberTask(202, env.Process, siblingRuntime.CreateEngine(), env.Scheduler);
        env.SyscallManager.RegisterEngine(sibling.CPU);

        const uint clearTidPtr = 0x00600000;
        env.MapUserPage(clearTidPtr);
        Assert.True(env.Engine.CopyToUser(clearTidPtr, BitConverter.GetBytes(123u)));
        env.Task.ChildClearTidPtr = clearTidPtr;

        var privateWaiter = env.SyscallManager.Futex.PrepareWait(ResolvePrivateKey(env.Vma, clearTidPtr));

        var rc = await CallSys(env.SyscallManager, env.Engine, "SysExit");
        Assert.Equal(0, rc);

        var valueBuf = new byte[4];
        Assert.True(env.Engine.CopyFromUser(clearTidPtr, valueBuf));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(valueBuf));
        Assert.True(privateWaiter.IsCompleted);
        GC.KeepAlive(sibling);
    }

    [Fact]
    public void FutexWaitRegistration_Cancel_RemovesPrivateWaiterFromQueue()
    {
        using var env = new TestEnv(205, 206);
        const uint futexAddr = 0x00602000;
        env.MapUserPage(futexAddr);

        var key = ResolvePrivateKey(env.Vma, futexAddr);
        var waiter = env.SyscallManager.Futex.PrepareWait(key);
        using var registration = env.SyscallManager.Futex.CreateWaitRegistration(key, waiter);

        Assert.Equal(1, env.SyscallManager.Futex.GetWaiterCount(key));

        registration.Cancel();

        Assert.Equal(0, env.SyscallManager.Futex.GetWaiterCount(key));
        Assert.True(waiter.IsCompleted);
        Assert.False(waiter.Result);
    }

    [Fact]
    public async Task SysExit_LeaderWithAliveThreads_MustNotFinalizeProcessOrCloseFds()
    {
        using var env = new TestEnv(240, 240);
        var peer = await env.Task.Clone((int)(LinuxConstants.CLONE_VM | LinuxConstants.CLONE_THREAD), 0, 0, 0, 0);

        var eventFd = new EventFdInode(77, env.SyscallManager.MemfdSuperBlock, env.Task.CommonKernel, 0,
            FileFlags.O_RDWR);
        var file = new LinuxFile(new Dentry(FsName.FromString("leader-exit-fd"), eventFd, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        var fd = env.SyscallManager.AllocFD(file);

        var rc = await CallSys(env.SyscallManager, env.Engine, "SysExit");
        Assert.Equal(0, rc);

        Assert.Null(env.Scheduler.GetTask(env.Task.TID));
        Assert.NotNull(env.Scheduler.GetTask(peer.TID));
        Assert.NotNull(env.SyscallManager.GetFD(fd));
        Assert.Equal(ProcessState.Running, env.Process.State);
    }

    [Fact]
    public void ExitRobustList_SharedWaiter_MustBeWokenOnOwnerDeath()
    {
        using var env = new TestEnv(250, 251);
        const uint headAddr = 0x00610000;
        const uint nodeAddr = 0x00611000;
        const int futexOffset = 4;
        var futexAddr = nodeAddr + futexOffset;

        env.MapUserPage(headAddr);
        env.MapSharedAnonymousPage(nodeAddr);

        var headBuf = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(headBuf.AsSpan(0, 4), nodeAddr);
        BinaryPrimitives.WriteInt32LittleEndian(headBuf.AsSpan(4, 4), futexOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(headBuf.AsSpan(8, 4), 0);
        Assert.True(env.Engine.CopyToUser(headAddr, headBuf));

        Assert.True(env.Engine.CopyToUser(nodeAddr, BitConverter.GetBytes(headAddr)));

        var ownerWithWaiters = (uint)env.Task.TID | LinuxConstants.FUTEX_WAITERS;
        Assert.True(env.Engine.CopyToUser(futexAddr, BitConverter.GetBytes(ownerWithWaiters)));

        env.Task.RobustListHead = headAddr;
        env.Task.RobustListSize = 12;

        var waiter = env.SyscallManager.Futex.PrepareWait(ResolveSharedKey(env.Vma, futexAddr));

        env.Task.ExitRobustList();

        Assert.True(waiter.IsCompleted);

        var valueBuf = new byte[4];
        Assert.True(env.Engine.CopyFromUser(futexAddr, valueBuf));
        var updated = BinaryPrimitives.ReadUInt32LittleEndian(valueBuf);
        Assert.True((updated & LinuxConstants.FUTEX_OWNER_DIED) != 0);
    }

    [Fact]
    public async Task PrivateFutex_SameVirtualAddressInDifferentProcesses_DoesNotCollide()
    {
        using var env = new TestEnv(260, 260);
        const uint futexAddr = 0x00620000;
        env.MapUserPage(futexAddr);
        Assert.True(env.Engine.CopyToUser(futexAddr, BitConverter.GetBytes(1u)));

        var child = await env.Task.Clone(0, 0, 0, 0, 0);
        Assert.NotSame(env.Process.Mem, child.Process.Mem);
        Assert.True(child.CPU.CopyToUser(futexAddr, BitConverter.GetBytes(1u)));

        var parentKey = ResolvePrivateKey(env.Vma, futexAddr);
        var childKey = ResolvePrivateKey(child.Process.Mem, futexAddr);

        var waiter = env.SyscallManager.Futex.PrepareWait(parentKey);
        Assert.Equal(0, env.SyscallManager.Futex.Wake(childKey, 1));
        Assert.False(waiter.IsCompleted);

        Assert.Equal(1, env.SyscallManager.Futex.Wake(parentKey, 1));
        Assert.True(waiter.IsCompleted);
    }

    [Fact]
    public async Task FutexRequeuePrivate_MovesWaiterToTargetQueue_AndCancelTracksNewKey()
    {
        using var env = new TestEnv(265, 265);
        const uint sourceAddr = 0x00624000;
        const uint targetAddr = 0x00625000;
        env.MapUserPage(sourceAddr);
        env.MapUserPage(targetAddr);

        var sourceKey = ResolvePrivateKey(env.Vma, sourceAddr);
        var targetKey = ResolvePrivateKey(env.Vma, targetAddr);
        var waiter = env.SyscallManager.Futex.PrepareWait(sourceKey);
        using var registration = env.SyscallManager.Futex.CreateWaitRegistration(sourceKey, waiter);

        var rc = await CallSys(env.SyscallManager, env.Engine, "SysFutex", sourceAddr,
            LinuxConstants.FUTEX_REQUEUE | LinuxConstants.FUTEX_PRIVATE_FLAG, 0, 1, targetAddr);

        Assert.Equal(1, rc);
        Assert.Equal(0, env.SyscallManager.Futex.GetWaiterCount(sourceKey));
        Assert.Equal(1, env.SyscallManager.Futex.GetWaiterCount(targetKey));
        Assert.False(waiter.IsCompleted);

        registration.Cancel();

        Assert.Equal(0, env.SyscallManager.Futex.GetWaiterCount(sourceKey));
        Assert.Equal(0, env.SyscallManager.Futex.GetWaiterCount(targetKey));
        Assert.True(waiter.IsCompleted);
        Assert.False(waiter.Result);
    }

    [Fact]
    public async Task FutexCmpRequeuePrivate_CompareMismatch_ReturnsEagain_WithoutMovingWaiters()
    {
        using var env = new TestEnv(266, 266);
        const uint sourceAddr = 0x00626000;
        const uint targetAddr = 0x00627000;
        env.MapUserPage(sourceAddr);
        env.MapUserPage(targetAddr);
        Assert.True(env.Engine.CopyToUser(sourceAddr, BitConverter.GetBytes(1u)));

        var sourceKey = ResolvePrivateKey(env.Vma, sourceAddr);
        var targetKey = ResolvePrivateKey(env.Vma, targetAddr);
        var waiter = env.SyscallManager.Futex.PrepareWait(sourceKey);

        var rc = await CallSys(env.SyscallManager, env.Engine, "SysFutex", sourceAddr,
            LinuxConstants.FUTEX_CMP_REQUEUE | LinuxConstants.FUTEX_PRIVATE_FLAG, 0, 1, targetAddr, 2);

        Assert.Equal(-(int)Errno.EAGAIN, rc);
        Assert.Equal(1, env.SyscallManager.Futex.GetWaiterCount(sourceKey));
        Assert.Equal(0, env.SyscallManager.Futex.GetWaiterCount(targetKey));
        Assert.False(waiter.IsCompleted);

        Assert.Equal(1, env.SyscallManager.Futex.Wake(sourceKey, 1));
        Assert.True(waiter.IsCompleted);
    }

    [Fact]
    public async Task FutexWaitBitset_ZeroMask_ReturnsEinval()
    {
        using var env = new TestEnv(267, 267);
        const uint futexAddr = 0x00628000;
        env.MapUserPage(futexAddr);
        Assert.True(env.Engine.CopyToUser(futexAddr, BitConverter.GetBytes(0u)));

        var rc = await CallSys(env.SyscallManager, env.Engine, "SysFutex", futexAddr,
            LinuxConstants.FUTEX_WAIT_BITSET | LinuxConstants.FUTEX_PRIVATE_FLAG);

        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task FutexWaitBitset_AbsolutePastDeadline_ReturnsEtimedout()
    {
        using var env = new TestEnv(268, 268);
        const uint futexAddr = 0x00629000;
        const uint timeoutPtr = 0x0062A000;
        env.MapUserPage(futexAddr);
        env.MapUserPage(timeoutPtr);
        Assert.True(env.Engine.CopyToUser(futexAddr, BitConverter.GetBytes(0u)));
        Assert.True(env.Engine.CopyToUser(timeoutPtr, new byte[8]));

        var rc = await CallSys(env.SyscallManager, env.Engine, "SysFutex", futexAddr,
            LinuxConstants.FUTEX_WAIT_BITSET | LinuxConstants.FUTEX_PRIVATE_FLAG, 0, timeoutPtr, 0,
            LinuxConstants.FUTEX_BITSET_MATCH_ANY);

        Assert.Equal(-(int)Errno.ETIMEDOUT, rc);
    }

    [Fact]
    public async Task FutexWakeBitset_WakesOnlyMatchingWaiters()
    {
        using var env = new TestEnv(269, 269);
        const uint futexAddr = 0x0062B000;
        env.MapUserPage(futexAddr);

        var key = ResolvePrivateKey(env.Vma, futexAddr);
        var waiterA = env.SyscallManager.Futex.PrepareWait(key, 0b0001);
        var waiterB = env.SyscallManager.Futex.PrepareWait(key, 0b0010);

        var rc = await CallSys(env.SyscallManager, env.Engine, "SysFutex", futexAddr,
            LinuxConstants.FUTEX_WAKE_BITSET | LinuxConstants.FUTEX_PRIVATE_FLAG, 2, 0, 0, 0b0001);

        Assert.Equal(1, rc);
        Assert.True(waiterA.IsCompleted);
        Assert.False(waiterB.IsCompleted);
        Assert.Equal(1, env.SyscallManager.Futex.GetWaiterCount(key));
    }

    [Fact]
    public void FutexWake_WakesWaitersInFifoOrder()
    {
        using var env = new TestEnv(26901, 26901);
        const uint futexAddr = 0x0064A000;
        env.MapUserPage(futexAddr);

        var key = ResolvePrivateKey(env.Vma, futexAddr);
        var waiterA = env.SyscallManager.Futex.PrepareWait(key);
        var waiterB = env.SyscallManager.Futex.PrepareWait(key);
        var waiterC = env.SyscallManager.Futex.PrepareWait(key);

        Assert.Equal(1, env.SyscallManager.Futex.Wake(key, 1));
        Assert.True(waiterA.IsCompleted);
        Assert.False(waiterB.IsCompleted);
        Assert.False(waiterC.IsCompleted);

        Assert.Equal(1, env.SyscallManager.Futex.Wake(key, 1));
        Assert.True(waiterB.IsCompleted);
        Assert.False(waiterC.IsCompleted);

        Assert.Equal(1, env.SyscallManager.Futex.Wake(key, 1));
        Assert.True(waiterC.IsCompleted);
    }

    [Fact]
    public void FutexWakeBitset_UnmatchedWaitersKeepRelativeOrder()
    {
        using var env = new TestEnv(26902, 26902);
        const uint futexAddr = 0x0064B000;
        env.MapUserPage(futexAddr);

        var key = ResolvePrivateKey(env.Vma, futexAddr);
        var waiterA = env.SyscallManager.Futex.PrepareWait(key, 0b0010);
        var waiterB = env.SyscallManager.Futex.PrepareWait(key, 0b0001);
        var waiterC = env.SyscallManager.Futex.PrepareWait(key, 0b0010);

        Assert.Equal(1, env.SyscallManager.Futex.Wake(key, 1, 0b0001));
        Assert.False(waiterA.IsCompleted);
        Assert.True(waiterB.IsCompleted);
        Assert.False(waiterC.IsCompleted);

        Assert.Equal(1, env.SyscallManager.Futex.Wake(key, 1, 0b0010));
        Assert.True(waiterA.IsCompleted);
        Assert.False(waiterC.IsCompleted);

        Assert.Equal(1, env.SyscallManager.Futex.Wake(key, 1, 0b0010));
        Assert.True(waiterC.IsCompleted);
    }

    [Fact]
    public async Task FutexWakeOp_UpdatesSecondWord_AndConditionallyWakesBothQueues()
    {
        using var env = new TestEnv(2691, 2691);
        const uint sourceAddr = 0x0062C000;
        const uint targetAddr = 0x0062D000;
        env.MapUserPage(sourceAddr);
        env.MapUserPage(targetAddr);
        Assert.True(env.Engine.CopyToUser(targetAddr, BitConverter.GetBytes(0u)));

        var sourceKey = ResolvePrivateKey(env.Vma, sourceAddr);
        var targetKey = ResolvePrivateKey(env.Vma, targetAddr);
        var sourceWaiter = env.SyscallManager.Futex.PrepareWait(sourceKey);
        var targetWaiter = env.SyscallManager.Futex.PrepareWait(targetKey);

        var rc = await CallSys(env.SyscallManager, env.Engine, "SysFutex", sourceAddr,
            LinuxConstants.FUTEX_WAKE_OP | LinuxConstants.FUTEX_PRIVATE_FLAG, 1, 1, targetAddr,
            EncodeWakeOp(LinuxConstants.FUTEX_OP_ADD, 1, LinuxConstants.FUTEX_OP_CMP_EQ, 0));

        Assert.Equal(2, rc);
        Assert.True(sourceWaiter.IsCompleted);
        Assert.True(targetWaiter.IsCompleted);

        var valueBuf = new byte[4];
        Assert.True(env.Engine.CopyFromUser(targetAddr, valueBuf));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(valueBuf));
    }

    [Fact]
    public async Task FutexWake_WithClockRealtimeFlag_ReturnsEnosys()
    {
        using var env = new TestEnv(2692, 2692);
        const uint futexAddr = 0x0062E000;
        env.MapUserPage(futexAddr);

        var rc = await CallSys(env.SyscallManager, env.Engine, "SysFutex", futexAddr,
            LinuxConstants.FUTEX_WAKE | LinuxConstants.FUTEX_PRIVATE_FLAG | LinuxConstants.FUTEX_CLOCK_REALTIME, 1);

        Assert.Equal(-(int)Errno.ENOSYS, rc);
    }

    [Fact]
    public async Task FutexMisalignedAddress_ReturnsEinval()
    {
        using var env = new TestEnv(2693, 2693);
        const uint alignedAddr = 0x0062F000;
        env.MapUserPage(alignedAddr);

        var rc = await CallSys(env.SyscallManager, env.Engine, "SysFutex", alignedAddr + 1,
            LinuxConstants.FUTEX_WAKE | LinuxConstants.FUTEX_PRIVATE_FLAG, 1);

        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public void FutexLockPi_WithQueuedWaiters_KeepsWaitersBitSet()
    {
        using var env = new TestEnv(2696, 2696);
        const uint futexAddr = 0x0064C000;
        env.MapUserPage(futexAddr);
        Assert.True(env.Engine.CopyToUser(futexAddr, BitConverter.GetBytes(0u)));

        var key = ResolvePrivateKey(env.Vma, futexAddr);
        var waiter = env.SyscallManager.Futex.PrepareWait(key);

        var lockRc = InvokeSysFutex(env, futexAddr, LinuxConstants.FUTEX_LOCK_PI | LinuxConstants.FUTEX_PRIVATE_FLAG, 0);
        Assert.Equal(0, lockRc);
        Assert.Equal((uint)env.Task.TID | LinuxConstants.FUTEX_WAITERS, ReadUserUInt32(env.Engine, futexAddr));

        var unlockRc = InvokeSysFutex(env, futexAddr,
            LinuxConstants.FUTEX_UNLOCK_PI | LinuxConstants.FUTEX_PRIVATE_FLAG, 0);
        Assert.Equal(0, unlockRc);
        Assert.Equal(LinuxConstants.FUTEX_WAITERS, ReadUserUInt32(env.Engine, futexAddr));
        Assert.True(waiter.IsCompleted);
    }

    [Fact]
    public async Task FutexWaitRequeuePi_SameSourceAndTarget_ReturnsEinval()
    {
        using var env = new TestEnv(2694, 2694);
        const uint futexAddr = 0x00631000;
        env.MapUserPage(futexAddr);
        Assert.True(env.Engine.CopyToUser(futexAddr, BitConverter.GetBytes(0u)));

        var rc = await CallSys(env.SyscallManager, env.Engine, "SysFutex", futexAddr,
            LinuxConstants.FUTEX_WAIT_REQUEUE_PI | LinuxConstants.FUTEX_PRIVATE_FLAG, 0, 0, futexAddr);

        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task FutexCmpRequeuePi_WakeCountMustBeOne()
    {
        using var env = new TestEnv(2695, 2695);
        const uint sourceAddr = 0x00632000;
        const uint targetAddr = 0x00633000;
        env.MapUserPage(sourceAddr);
        env.MapUserPage(targetAddr);
        Assert.True(env.Engine.CopyToUser(sourceAddr, BitConverter.GetBytes(0u)));

        var rc = await CallSys(env.SyscallManager, env.Engine, "SysFutex", sourceAddr,
            LinuxConstants.FUTEX_CMP_REQUEUE_PI | LinuxConstants.FUTEX_PRIVATE_FLAG, 2, 0, targetAddr);

        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task SharedFileFutex_DifferentMappingsAndFiles_HitSameKey()
    {
        using var env = new TestEnv(270, 270);
        var child = await env.Task.Clone(0, 0, 0, 0, 0);

        var file1 = env.CreateTmpfsFile("futex-shared");
        var file2 = new LinuxFile(file1.Dentry, FileFlags.O_RDWR, null!);
        Assert.Equal(file1.Dentry.Inode, file2.Dentry.Inode);
        Assert.Equal(4, file1.Dentry.Inode!.WriteFromHost(null, file1, BitConverter.GetBytes(2u), 0));

        const uint parentAddr = 0x00630000;
        const uint childAddr = 0x00634000;
        env.Vma.Mmap(parentAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Shared | MapFlags.Fixed, file1, 0, "futex-parent", env.Engine);
        child.Process.Mem.Mmap(childAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Shared | MapFlags.Fixed, file2, 0, "futex-child", child.CPU);
        Assert.True(env.Vma.HandleFault(parentAddr, true, env.Engine));
        Assert.True(child.Process.Mem.HandleFault(childAddr, true, child.CPU));
        Assert.True(env.Engine.CopyToUser(parentAddr, BitConverter.GetBytes(2u)));
        Assert.True(child.CPU.CopyToUser(childAddr, BitConverter.GetBytes(2u)));

        file1.Close();

        var parentKey = ResolveSharedKey(env.Vma, parentAddr);
        var childKey = ResolveSharedKey(child.Process.Mem, childAddr);
        Assert.Equal(parentKey, childKey);

        var waiter = env.SyscallManager.Futex.PrepareWait(parentKey);
        Assert.Equal(1, env.SyscallManager.Futex.Wake(childKey, 1));
        Assert.True(waiter.IsCompleted);

        file2.Close();
    }

    [Fact]
    public void FutexWithoutPrivateFlag_OnPrivateMapping_FallsBackToPrivateKey()
    {
        using var env = new TestEnv(275, 275);
        const uint futexAddr = 0x00638000;
        env.MapUserPage(futexAddr);
        Assert.True(env.Engine.CopyToUser(futexAddr, BitConverter.GetBytes(0u)));

        var resolved = ResolveKey(env.SyscallManager, env.Engine, futexAddr, true, out var error);

        Assert.Equal(0, error);
        Assert.Equal(ResolvePrivateKey(env.Vma, futexAddr), resolved);
    }

    [Fact]
    public async Task FutexWithoutPrivateFlag_OnSharedFileMapping_UsesSharedKey()
    {
        using var env = new TestEnv(276, 276);
        var child = await env.Task.Clone(0, 0, 0, 0, 0);

        var file1 = env.CreateTmpfsFile("futex-shared-nonprivate");
        var file2 = new LinuxFile(file1.Dentry, FileFlags.O_RDWR, null!);
        Assert.Equal(4, file1.Dentry.Inode!.WriteFromHost(null, file1, BitConverter.GetBytes(0u), 0));

        const uint parentAddr = 0x0063C000;
        const uint childAddr = 0x00640000;
        env.Vma.Mmap(parentAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Shared | MapFlags.Fixed, file1, 0, "futex-parent-nonprivate", env.Engine);
        child.Process.Mem.Mmap(childAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Shared | MapFlags.Fixed, file2, 0, "futex-child-nonprivate", child.CPU);
        Assert.True(env.Vma.HandleFault(parentAddr, true, env.Engine));
        Assert.True(child.Process.Mem.HandleFault(childAddr, true, child.CPU));
        Assert.True(env.Engine.CopyToUser(parentAddr, BitConverter.GetBytes(0u)));

        var resolved = ResolveKey(env.SyscallManager, env.Engine, parentAddr, true, out var error);

        Assert.Equal(0, error);
        Assert.Equal(ResolveSharedKey(env.Vma, parentAddr), resolved);

        file1.Close();
        file2.Close();
    }

    [Fact]
    public void FutexWithoutPrivateFlag_OnSharedAnonymousMapping_UsesSharedFileKey()
    {
        using var env = new TestEnv(277, 277);
        const uint futexAddr = 0x00642000;
        env.MapSharedAnonymousPage(futexAddr);
        Assert.True(env.Engine.CopyToUser(futexAddr, BitConverter.GetBytes(0u)));

        var resolved = ResolveKey(env.SyscallManager, env.Engine, futexAddr, true, out var error);

        Assert.Equal(0, error);
        Assert.Equal(ResolveSharedKey(env.Vma, futexAddr), resolved);
    }

    [Fact]
    public void FutexManagerWake_HotPath_DoesNotAllocate()
    {
        using var env = new TestEnv(278, 278);
        const uint futexAddr = 0x00643000;
        env.MapUserPage(futexAddr);

        var key = ResolvePrivateKey(env.Vma, futexAddr);
        var total = AllocationWarmupIterations + AllocationMeasureIterations;
        var waiters = new Waiter[total];
        for (var i = 0; i < total; i++)
            waiters[i] = env.SyscallManager.Futex.PrepareWait(key);

        var index = 0;
        AssertNoAllocation(() =>
        {
            if (env.SyscallManager.Futex.Wake(key, 1) != 1)
                throw new InvalidOperationException();
            if (!waiters[index].IsCompleted)
                throw new InvalidOperationException();
            index++;
        });

        Assert.Equal(total, index);
        Assert.Equal(0, env.SyscallManager.Futex.GetWaiterCount(key));
    }

    [Fact]
    public void FutexManagerRequeue_HotPath_DoesNotAllocate()
    {
        using var env = new TestEnv(279, 279);
        const uint sourceAddr = 0x00644000;
        const uint targetAddr = 0x00645000;
        env.MapUserPage(sourceAddr);
        env.MapUserPage(targetAddr);

        var sourceKey = ResolvePrivateKey(env.Vma, sourceAddr);
        var targetKey = ResolvePrivateKey(env.Vma, targetAddr);
        var total = AllocationWarmupIterations + AllocationMeasureIterations;

        _ = env.SyscallManager.Futex.PrepareWait(targetKey);
        for (var i = 0; i < total; i++)
            _ = env.SyscallManager.Futex.PrepareWait(sourceKey);

        AssertNoAllocation(() =>
        {
            if (env.SyscallManager.Futex.Requeue(sourceKey, targetKey, 1) != 1)
                throw new InvalidOperationException();
        });

        Assert.Equal(0, env.SyscallManager.Futex.GetWaiterCount(sourceKey));
        Assert.Equal(total + 1, env.SyscallManager.Futex.GetWaiterCount(targetKey));
    }

    [Fact]
    public void FutexManagerCancelWait_HotPath_DoesNotAllocate()
    {
        using var env = new TestEnv(280, 280);
        const uint futexAddr = 0x00646000;
        env.MapUserPage(futexAddr);

        var key = ResolvePrivateKey(env.Vma, futexAddr);
        var total = AllocationWarmupIterations + AllocationMeasureIterations;
        var waiters = new Waiter[total];
        for (var i = 0; i < total; i++)
            waiters[i] = env.SyscallManager.Futex.PrepareWait(key);

        var index = 0;
        AssertNoAllocation(() =>
        {
            env.SyscallManager.Futex.CancelWait(key, waiters[index]);
            index++;
        });

        Assert.Equal(total, index);
        Assert.Equal(0, env.SyscallManager.Futex.GetWaiterCount(key));
        foreach (var waiter in waiters)
        {
            Assert.True(waiter.IsCompleted);
            Assert.False(waiter.Result);
        }
    }

    [Fact]
    public void SysFutexWait_EagainFastPath_DoesNotAllocate()
    {
        using var env = new TestEnv(281, 281);
        const uint futexAddr = 0x00647000;
        env.MapUserPage(futexAddr);
        Assert.True(env.Engine.CopyToUser(futexAddr, BitConverter.GetBytes(1u)));

        AssertNoAllocation(() =>
        {
            var rc = InvokeSysFutex(env, futexAddr, LinuxConstants.FUTEX_WAIT | LinuxConstants.FUTEX_PRIVATE_FLAG, 0);
            if (rc != -(int)Errno.EAGAIN)
                throw new InvalidOperationException();
        });
    }

    [Fact]
    public void SysFutexWake_HotPath_DoesNotAllocate()
    {
        using var env = new TestEnv(282, 282);
        const uint futexAddr = 0x00648000;
        env.MapUserPage(futexAddr);

        var key = ResolvePrivateKey(env.Vma, futexAddr);
        var total = AllocationWarmupIterations + AllocationMeasureIterations;
        var waiters = new Waiter[total];
        for (var i = 0; i < total; i++)
            waiters[i] = env.SyscallManager.Futex.PrepareWait(key);

        var index = 0;
        AssertNoAllocation(() =>
        {
            var rc = InvokeSysFutex(env, futexAddr, LinuxConstants.FUTEX_WAKE | LinuxConstants.FUTEX_PRIVATE_FLAG,
                1);
            if (rc != 1)
                throw new InvalidOperationException();
            if (!waiters[index].IsCompleted)
                throw new InvalidOperationException();
            index++;
        });

        Assert.Equal(total, index);
        Assert.Equal(0, env.SyscallManager.Futex.GetWaiterCount(key));
    }

    [Fact]
    public async Task SysMunmap_FromOneThread_UnmapsPeerEngineMappings()
    {
        using var env = new TestEnv(300, 301);
        const uint addr = 0x00700000;
        env.MapUserPage(addr);

        const uint oldValue = 0xA1B2C3D4;
        Assert.True(env.Engine.CopyToUser(addr, BitConverter.GetBytes(oldValue)));

        var peer = await env.Task.Clone((int)(LinuxConstants.CLONE_VM | LinuxConstants.CLONE_THREAD), 0, 0, 0, 0);
        var valueBuf = new byte[4];
        Assert.True(peer.CPU.CopyFromUser(addr, valueBuf));
        Assert.Equal(oldValue, BinaryPrimitives.ReadUInt32LittleEndian(valueBuf));

        var rc = await CallSys(env.SyscallManager, peer.CPU, "SysMunmap", addr, LinuxConstants.PageSize);
        Assert.Equal(0, rc);

        Assert.Equal(IntPtr.Zero, env.Engine.GetPhysicalAddressSafe(addr, false));
        Assert.Equal(IntPtr.Zero, peer.CPU.GetPhysicalAddressSafe(addr, false));

        env.Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test-remap]",
            env.Engine);
        Assert.True(env.Vma.HandleFault(addr, true, env.Engine));

        const uint newValue = 0x55667788;
        Assert.True(env.Engine.CopyToUser(addr, BitConverter.GetBytes(newValue)));
        Assert.True(peer.CPU.CopyFromUser(addr, valueBuf));
        Assert.Equal(newValue, BinaryPrimitives.ReadUInt32LittleEndian(valueBuf));

        GC.KeepAlive(peer);
    }

    [Fact]
    public async Task SysMmap2_MapFixed_ReplacesPeerThreadStaleMappings()
    {
        using var env = new TestEnv(310, 311);
        const uint addr = 0x00800000;
        env.MapUserPage(addr);

        const uint oldValue = 0x11223344;
        Assert.True(env.Engine.CopyToUser(addr, BitConverter.GetBytes(oldValue)));

        var peer = await env.Task.Clone((int)(LinuxConstants.CLONE_VM | LinuxConstants.CLONE_THREAD), 0, 0, 0, 0);
        var valueBuf = new byte[4];
        Assert.True(peer.CPU.CopyFromUser(addr, valueBuf));
        Assert.Equal(oldValue, BinaryPrimitives.ReadUInt32LittleEndian(valueBuf));

        var rc = await CallSys(env.SyscallManager, env.Engine, "SysMmap2", addr, LinuxConstants.PageSize,
            (uint)(Protection.Read | Protection.Write),
            (uint)(MapFlags.Private | MapFlags.Anonymous | MapFlags.Fixed));
        Assert.Equal((int)addr, rc);

        Assert.True(peer.CPU.CopyFromUser(addr, valueBuf));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(valueBuf));

        const uint newValue = 0x55667788;
        Assert.True(env.Engine.CopyToUser(addr, BitConverter.GetBytes(newValue)));
        Assert.True(peer.CPU.CopyFromUser(addr, valueBuf));
        Assert.Equal(newValue, BinaryPrimitives.ReadUInt32LittleEndian(valueBuf));

        GC.KeepAlive(peer);
    }

    [Fact]
    public async Task SysMremap_Shrink_UnmapsTailInPeerEngines()
    {
        using var env = new TestEnv(320, 321);
        const uint addr = 0x00900000;
        const uint twoPages = LinuxConstants.PageSize * 2;

        env.Vma.Mmap(addr, twoPages, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[mremap-test]", env.Engine);
        Assert.True(env.Vma.HandleFault(addr, true, env.Engine));
        Assert.True(env.Vma.HandleFault(addr + LinuxConstants.PageSize, true, env.Engine));
        Assert.True(env.Engine.CopyToUser(addr + LinuxConstants.PageSize, BitConverter.GetBytes(0xDEADBEEFu)));

        var peer = await env.Task.Clone((int)(LinuxConstants.CLONE_VM | LinuxConstants.CLONE_THREAD), 0, 0, 0, 0);
        var valueBuf = new byte[4];
        Assert.True(peer.CPU.CopyFromUser(addr + LinuxConstants.PageSize, valueBuf));
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(valueBuf));

        var rc = await CallSys(env.SyscallManager, peer.CPU, "SysMremap", addr, twoPages, LinuxConstants.PageSize);
        Assert.Equal((int)addr, rc);

        Assert.Equal(IntPtr.Zero, env.Engine.GetPhysicalAddressSafe(addr + LinuxConstants.PageSize, false));
        Assert.Equal(IntPtr.Zero, peer.CPU.GetPhysicalAddressSafe(addr + LinuxConstants.PageSize, false));

        GC.KeepAlive(peer);
    }

    [Fact]
    public async Task SysVShm_ShmatRemap_ReplacesPeerThreadStaleMappings()
    {
        using var env = new TestEnv(330, 331);
        const uint addr = 0x00A00000;
        env.MapUserPage(addr);

        const uint oldValue = 0xCAFEBABE;
        Assert.True(env.Engine.CopyToUser(addr, BitConverter.GetBytes(oldValue)));

        var peer = await env.Task.Clone((int)(LinuxConstants.CLONE_VM | LinuxConstants.CLONE_THREAD), 0, 0, 0, 0);
        var valueBuf = new byte[4];
        Assert.True(peer.CPU.CopyFromUser(addr, valueBuf));
        Assert.Equal(oldValue, BinaryPrimitives.ReadUInt32LittleEndian(valueBuf));

        var shmid = env.SyscallManager.SysVShm.ShmGet(LinuxConstants.IPC_PRIVATE, LinuxConstants.PageSize,
            LinuxConstants.IPC_CREAT | 0x1FF, 0, 0, env.Process.TGID);
        Assert.True(shmid > 0);

        var attachRc = env.SyscallManager.SysVShm.ShmAt(shmid, addr, LinuxConstants.SHM_REMAP, env.Process.TGID,
            env.Vma, env.Engine, env.Process);
        Assert.Equal(addr, attachRc);

        ProcessAddressSpaceSync.SyncEngineBeforeRun(env.Vma, peer.CPU, env.Process);
        Assert.True(peer.CPU.CopyFromUser(addr, valueBuf));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(valueBuf));

        GC.KeepAlive(peer);
    }

    private static async ValueTask<int> CallSys(SyscallManager sm, Engine engine, string methodName, uint a1 = 0,
        uint a2 = 0, uint a3 = 0, uint a4 = 0, uint a5 = 0, uint a6 = 0)
    {
        var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (ValueTask<int>)method!.Invoke(sm, [engine, a1, a2, a3, a4, a5, a6])!;
        return await task;
    }

    private static FutexKey ResolvePrivateKey(VMAManager mm, uint uaddr)
    {
        return FutexKey.Private(mm, uaddr & LinuxConstants.PageMask, (ushort)(uaddr & LinuxConstants.PageOffsetMask));
    }

    private static FutexKey ResolveSharedKey(VMAManager mm, uint uaddr)
    {
        var vma = mm.FindVmArea(uaddr);
        Assert.NotNull(vma);
        Assert.True((vma!.Flags & MapFlags.Shared) != 0);
        Assert.True(vma.IsFileBacked);
        var pageIndex = vma.GetPageIndex(uaddr & LinuxConstants.PageMask);
        var offset = (ushort)(uaddr & LinuxConstants.PageOffsetMask);
        return FutexKey.SharedFile(vma.File!.OpenedInode!, pageIndex, offset);
    }

    private static FutexKey ResolveKey(SyscallManager sm, Engine engine, uint uaddr, bool fshared, out int error)
    {
        var ok = ResolveFutexKeyMethod(sm, engine, uaddr, fshared, out var key, out error);
        Assert.True(ok);
        return key;
    }

    private static uint EncodeWakeOp(int op, int opArg, int cmp, int cmpArg)
    {
        return (uint)(((op & 0xF) << 28) | ((cmp & 0xF) << 24) | ((opArg & 0xFFF) << 12) | (cmpArg & 0xFFF));
    }

    private static int InvokeSysFutex(TestEnv env, uint a1, uint a2, uint a3 = 0, uint a4 = 0, uint a5 = 0,
        uint a6 = 0)
    {
        return SysFutexMethod(env.SyscallManager, env.Engine, a1, a2, a3, a4, a5, a6).GetAwaiter().GetResult();
    }

    private static uint ReadUserUInt32(Engine engine, uint addr)
    {
        var buf = new byte[4];
        Assert.True(engine.CopyFromUser(addr, buf));
        return BinaryPrimitives.ReadUInt32LittleEndian(buf);
    }

    private static void AssertNoAllocation(Action action)
    {
        for (var i = 0; i < AllocationWarmupIterations; i++)
            action();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < AllocationMeasureIterations; i++)
            action();
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly TestRuntimeFactory _runtime = new();

        public TestEnv(int tgid, int tid)
        {
            Engine = _runtime.CreateEngine();
            Vma = _runtime.CreateAddressSpace();
            SyscallManager = new SyscallManager(Engine, Vma, 0);
            Process = new Process(tgid, Vma, SyscallManager);
            Scheduler = new KernelScheduler();

            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            TmpfsSuper = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "test-tmpfs", null);

            Task = new FiberTask(tid, Process, Engine, Scheduler);
            Engine.Owner = Task;
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public SyscallManager SyscallManager { get; }
        public Process Process { get; }
        public KernelScheduler Scheduler { get; }
        public FiberTask Task { get; }
        public SuperBlock TmpfsSuper { get; }

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

        public void MapSharedAnonymousPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test-shared]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public LinuxFile CreateTmpfsFile(string name)
        {
            var dentry = new Dentry(FsName.FromString(name), null, TmpfsSuper.Root, TmpfsSuper);
            TmpfsSuper.Root.Inode!.Create(dentry, 0x1B6, 0, 0);
            return new LinuxFile(dentry, FileFlags.O_RDWR, null!);
        }
    }
}
