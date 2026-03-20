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

        var childTid = await CallSys("SysClone", env.Engine.State, flags, 0, 0, tlsPtr, ctidPtr);
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

        var rc = await CallSys("SysClone", env.Engine.State, LinuxConstants.CLONE_THREAD);
        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task SysClone_ThreadWithoutSighand_ReturnsEinval()
    {
        using var env = new TestEnv(111, 111);

        var flags = LinuxConstants.CLONE_VM | LinuxConstants.CLONE_THREAD;
        var rc = await CallSys("SysClone", env.Engine.State, flags);
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

        var rc = await CallSys("SysClone", env.Engine.State, flags, 0, 0, invalidTlsPtr);
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

        var rc = await CallSys("SysClone", env.Engine.State, flags, 0, invalidPtidPtr);
        Assert.Equal(-(int)Errno.EFAULT, rc);
        Assert.Empty(env.Process.Children);
        Assert.Single(env.Process.Threads);
        Assert.Same(env.Task, env.Process.Threads[0]);
        Assert.Empty(env.Scheduler.GetProcessesSnapshot());
    }

    [Fact]
    public async Task SysExit_ClearsChildTidAndWakesFutexWaiters()
    {
        using var env = new TestEnv(200, 201);
        var sibling = new FiberTask(202, env.Process, new Engine(), env.Scheduler);
        env.SyscallManager.RegisterEngine(sibling.CPU);

        const uint clearTidPtr = 0x00600000;
        env.MapUserPage(clearTidPtr);
        Assert.True(env.Engine.CopyToUser(clearTidPtr, BitConverter.GetBytes(123u)));
        env.Task.ChildClearTidPtr = clearTidPtr;

        var hostPtr = env.Engine.GetPhysicalAddressSafe(clearTidPtr, false);
        Assert.NotEqual(IntPtr.Zero, hostPtr);

        var privateWaiter = env.SyscallManager.Futex.PrepareWait(clearTidPtr);
        var sharedWaiter = env.SyscallManager.Futex.PrepareWaitShared(hostPtr);

        var rc = await CallSys("SysExit", env.Engine.State);
        Assert.Equal(0, rc);

        var valueBuf = new byte[4];
        Assert.True(env.Engine.CopyFromUser(clearTidPtr, valueBuf));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(valueBuf));
        Assert.True(privateWaiter.Tcs.Task.IsCompleted);
        Assert.True(sharedWaiter.Tcs.Task.IsCompleted);
        GC.KeepAlive(sibling);
    }

    [Fact]
    public async Task SysExit_LeaderWithAliveThreads_MustNotFinalizeProcessOrCloseFds()
    {
        using var env = new TestEnv(240, 240);
        var peer = await env.Task.Clone((int)(LinuxConstants.CLONE_VM | LinuxConstants.CLONE_THREAD), 0, 0, 0, 0);

        var eventFd = new EventFdInode(77, env.SyscallManager.MemfdSuperBlock, 0, FileFlags.O_RDWR);
        var file = new LinuxFile(new Dentry("leader-exit-fd", eventFd, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        var fd = env.SyscallManager.AllocFD(file);

        var rc = await CallSys("SysExit", env.Engine.State);
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
        env.MapUserPage(nodeAddr);

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

        var hostPtr = env.Engine.GetPhysicalAddressSafe(futexAddr, false);
        Assert.NotEqual(IntPtr.Zero, hostPtr);
        var waiter = env.SyscallManager.Futex.PrepareWaitShared(hostPtr);

        env.Task.ExitRobustList();

        Assert.True(waiter.Tcs.Task.IsCompleted);

        var valueBuf = new byte[4];
        Assert.True(env.Engine.CopyFromUser(futexAddr, valueBuf));
        var updated = BinaryPrimitives.ReadUInt32LittleEndian(valueBuf);
        Assert.True((updated & LinuxConstants.FUTEX_OWNER_DIED) != 0);
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

        var rc = await CallSys("SysMunmap", peer.CPU.State, addr, LinuxConstants.PageSize);
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

        var rc = await CallSys("SysMmap2", env.Engine.State, addr, LinuxConstants.PageSize,
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

        var rc = await CallSys("SysMremap", peer.CPU.State, addr, twoPages, LinuxConstants.PageSize);
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

        Assert.True(peer.CPU.CopyFromUser(addr, valueBuf));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(valueBuf));

        GC.KeepAlive(peer);
    }

    private static async ValueTask<int> CallSys(string methodName, IntPtr state, uint a1 = 0, uint a2 = 0, uint a3 = 0,
        uint a4 = 0, uint a5 = 0, uint a6 = 0)
    {
        var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = (ValueTask<int>)method!.Invoke(null, [state, a1, a2, a3, a4, a5, a6])!;
        return await task;
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv(int tgid, int tid)
        {
            Engine = new Engine();
            Vma = new VMAManager();
            SyscallManager = new SyscallManager(Engine, Vma, 0);
            Process = new Process(tgid, Vma, SyscallManager);
            Scheduler = new KernelScheduler();
            
            Task = new FiberTask(tid, Process, Engine, Scheduler);
            Engine.Owner = Task;
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public SyscallManager SyscallManager { get; }
        public Process Process { get; }
        public KernelScheduler Scheduler { get; }
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
    }
}