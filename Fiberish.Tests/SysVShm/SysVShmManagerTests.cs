using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.SysVShm;

public class SysVShmManagerTests
{
    [Fact]
    public void ShmGet_SameKeyWithoutExcl_ReturnsExistingShmid()
    {
        using var ctx = new TestContext();

        var shmid1 = ctx.Manager.ShmGet(0x1234, 4096, LinuxConstants.IPC_CREAT | 0x1FF, 1000, 1000, 2000);
        var shmid2 = ctx.Manager.ShmGet(0x1234, 4096, LinuxConstants.IPC_CREAT | 0x1FF, 1000, 1000, 2000);

        Assert.True(shmid1 > 0);
        Assert.Equal(shmid1, shmid2);
    }

    [Fact]
    public void ShmGet_ExclWithoutCreate_ShouldNotReturnEexist()
    {
        using var ctx = new TestContext();

        var shmid1 = ctx.Manager.ShmGet(0x1234, 4096, LinuxConstants.IPC_CREAT | 0x1FF, 1000, 1000, 2000);
        var shmid2 = ctx.Manager.ShmGet(0x1234, 4096, LinuxConstants.IPC_EXCL | 0x1FF, 1000, 1000, 2000);

        Assert.True(shmid1 > 0);
        Assert.Equal(shmid1, shmid2);
    }

    [Fact]
    public void ShmCtl_Ipc64Stat_ShouldBeAccepted()
    {
        using var ctx = new TestContext();

        var shmid = ctx.Manager.ShmGet(0x2234, 4096, LinuxConstants.IPC_CREAT | 0x1FF, 0, 0, 2000);
        Assert.True(shmid > 0);

        const uint userBuf = 0x40000000;
        ctx.MapUserPage(userBuf);

        var ret = ctx.Manager.ShmCtl(shmid, LinuxConstants.IPC_STAT | LinuxConstants.IPC_64, userBuf, ctx.Engine, 0,
            0, 2000);
        Assert.Equal(0, ret);
    }

    [Fact]
    public void ShmAt_WithoutRemap_MustRejectOverlap()
    {
        using var ctx = new TestContext();

        var shmid1 = ctx.Manager.ShmGet(LinuxConstants.IPC_PRIVATE, 4096, 0x1FF, 0, 0, 2000);
        var shmid2 = ctx.Manager.ShmGet(LinuxConstants.IPC_PRIVATE, 4096, 0x1FF, 0, 0, 2000);
        Assert.True(shmid1 > 0);
        Assert.True(shmid2 > 0);

        const uint addr = 0x20000000;
        var ret1 = ctx.Manager.ShmAt(shmid1, addr, 0, 2000, ctx.Vma, ctx.Engine);
        Assert.Equal(addr, ret1);

        var ret2 = ctx.Manager.ShmAt(shmid2, addr, 0, 2000, ctx.Vma, ctx.Engine);
        Assert.Equal(-(int)Errno.EINVAL, (int)ret2);
    }

    [Fact]
    public void ShmDt_ShouldWorkAcrossThreadsInSameProcess()
    {
        using var ctx = new TestContext();

        var shmid = ctx.Manager.ShmGet(LinuxConstants.IPC_PRIVATE, 4096, 0x1FF, 0, 0, 2000);
        Assert.True(shmid > 0);

        const uint addr = 0x21000000;
        var attachRet = ctx.Manager.ShmAt(shmid, addr, 0, 3001, ctx.Vma, ctx.Engine);
        Assert.Equal(addr, attachRet);

        // Same process, different TID, but the TGID (process ID) passed to IPC is the same.
        var detachRet = ctx.Manager.ShmDt(addr, 3001, ctx.Vma, ctx.Engine);
        Assert.Equal(0, detachRet);
    }

    [Fact]
    public void ShmDt_ShouldWorkAcrossProcessesSharingAddressSpace()
    {
        var manager = new SysVShmManager();
        var sharedVma = new VMAManager();
        sharedVma.AddSharedRef();

        using var p1 = new TestContext(manager, 3101, sharedVma);
        using var p2 = new TestContext(manager, 3102, sharedVma);

        var shmid = manager.ShmGet(LinuxConstants.IPC_PRIVATE, 4096, 0x1FF, 0, 0, 3101);
        Assert.True(shmid > 0);

        const uint addr = 0x21400000;
        var attachRet = manager.ShmAt(shmid, addr, 0, 3101, p1.Vma, p1.Engine, p1.Process);
        Assert.Equal(addr, attachRet);

        // Shared VM (CLONE_VM-style): peer process should be able to detach by address space.
        var detachRet = manager.ShmDt(addr, 3102, p2.Vma, p2.Engine, p2.Process);
        Assert.Equal(0, detachRet);
        Assert.Equal(-(int)Errno.EINVAL, manager.ShmDt(addr, 3101, p1.Vma, p1.Engine, p1.Process));
    }

    [Fact]
    public void OnProcessExit_ShouldDetachAndUnmapAllAttachments()
    {
        using var ctx = new TestContext();

        var shmid = ctx.Manager.ShmGet(LinuxConstants.IPC_PRIVATE, 4096, 0x1FF, 0, 0, 2000);
        Assert.True(shmid > 0);

        const uint addr = 0x22000000;
        var attachRet = ctx.Manager.ShmAt(shmid, addr, 0, 3001, ctx.Vma, ctx.Engine);
        Assert.Equal(addr, attachRet);
        Assert.NotEmpty(ctx.Vma.FindVMAsInRange(addr, addr + 4096));

        ctx.Manager.OnProcessExit(3001, ctx.Vma, ctx.Engine);

        Assert.Empty(ctx.Vma.FindVMAsInRange(addr, addr + 4096));
    }

    [Fact]
    public void OnProcessExit_WithSharedAddressSpace_MustNotDetach()
    {
        var manager = new SysVShmManager();
        var sharedVma = new VMAManager();
        sharedVma.AddSharedRef();

        using var p1 = new TestContext(manager, 3201, sharedVma);
        using var p2 = new TestContext(manager, 3202, sharedVma);

        var shmid = manager.ShmGet(LinuxConstants.IPC_PRIVATE, 4096, 0x1FF, 0, 0, 3201);
        Assert.True(shmid > 0);

        const uint addr = 0x22400000;
        var attachRet = manager.ShmAt(shmid, addr, 0, 3201, p1.Vma, p1.Engine, p1.Process);
        Assert.Equal(addr, attachRet);
        Assert.NotEmpty(sharedVma.FindVMAsInRange(addr, addr + 4096));

        manager.OnProcessExit(3201, p1.Vma, p1.Engine, p1.Process);

        // Address space is still shared, so attachment must remain live.
        Assert.NotEmpty(sharedVma.FindVMAsInRange(addr, addr + 4096));
        Assert.Equal(0, manager.ShmDt(addr, 3202, p2.Vma, p2.Engine, p2.Process));
    }

    [Fact]
    public void ShmDt_MustNotDetachAttachmentOfAnotherProcess()
    {
        var manager = new SysVShmManager();
        using var p1 = new TestContext(manager, 2001);
        using var p2 = new TestContext(manager, 2002);

        var shmid = manager.ShmGet(0x8899, 4096, LinuxConstants.IPC_CREAT | 0x1FF, 0, 0, 2001);
        Assert.True(shmid > 0);

        const uint addr = 0x23000000;
        Assert.Equal(addr, manager.ShmAt(shmid, addr, 0, 2001, p1.Vma, p1.Engine));
        Assert.Equal(addr, manager.ShmAt(shmid, addr, 0, 2002, p2.Vma, p2.Engine));

        // Detach from process 2 first. This must not remove process 1's attach record.
        Assert.Equal(0, manager.ShmDt(addr, 2002, p2.Vma, p2.Engine));
        Assert.Empty(p2.Vma.FindVMAsInRange(addr, addr + 4096));
        Assert.NotEmpty(p1.Vma.FindVMAsInRange(addr, addr + 4096));

        // Process 1 should still be able to detach successfully.
        Assert.Equal(0, manager.ShmDt(addr, 2001, p1.Vma, p1.Engine));
        Assert.Empty(p1.Vma.FindVMAsInRange(addr, addr + 4096));
    }

    [Fact]
    public void ShmAt_WithShmRemapAndNullAddress_ShouldReturnEinval()
    {
        using var ctx = new TestContext();
        var shmid = ctx.Manager.ShmGet(LinuxConstants.IPC_PRIVATE, 4096, 0x1FF, 0, 0, 2000);
        Assert.True(shmid > 0);

        var ret = ctx.Manager.ShmAt(shmid, 0, LinuxConstants.SHM_REMAP, 2000, ctx.Vma, ctx.Engine, ctx.Process);
        Assert.Equal(-(int)Errno.EINVAL, (int)ret);
    }

    [Fact]
    public void ShmAt_WithShmRemap_MustDropReplacedAttachmentRecord()
    {
        using var ctx = new TestContext();
        var shmid1 = ctx.Manager.ShmGet(LinuxConstants.IPC_PRIVATE, 4096, 0x1FF, 0, 0, 3001);
        var shmid2 = ctx.Manager.ShmGet(LinuxConstants.IPC_PRIVATE, 4096, 0x1FF, 0, 0, 3001);
        Assert.True(shmid1 > 0);
        Assert.True(shmid2 > 0);

        const uint addr = 0x22800000;
        Assert.Equal(addr, ctx.Manager.ShmAt(shmid1, addr, 0, 3001, ctx.Vma, ctx.Engine, ctx.Process));
        Assert.Equal(addr,
            ctx.Manager.ShmAt(shmid2, addr, LinuxConstants.SHM_REMAP, 3001, ctx.Vma, ctx.Engine, ctx.Process));

        // Only the new mapping should remain attached.
        Assert.Equal(0, ctx.Manager.ShmDt(addr, 3001, ctx.Vma, ctx.Engine, ctx.Process));
        Assert.Equal(-(int)Errno.EINVAL, ctx.Manager.ShmDt(addr, 3001, ctx.Vma, ctx.Engine, ctx.Process));
    }

    [Fact]
    public void ShmAt_WithAddressRangeOverflow_ShouldReturnEinval()
    {
        using var ctx = new TestContext();
        var shmid = ctx.Manager.ShmGet(LinuxConstants.IPC_PRIVATE, LinuxConstants.PageSize * 2, 0x1FF, 0, 0,
            3001);
        Assert.True(shmid > 0);

        var addr = LinuxConstants.TaskSize32 - LinuxConstants.PageSize;
        var ret = ctx.Manager.ShmAt(shmid, addr, 0, 3001, ctx.Vma, ctx.Engine, ctx.Process);
        Assert.Equal(-(int)Errno.EINVAL, (int)ret);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly FiberTask _task;

        public TestContext(SysVShmManager? manager = null, int processId = 2000, VMAManager? vma = null)
        {
            Engine = new Engine();
            Vma = vma ?? new VMAManager();
            Manager = manager ?? new SysVShmManager();

            var kernel = new KernelScheduler();
            Process = new Process(processId, Vma, null!)
            {
                EUID = 0,
                EGID = 0
            };
            _task = new FiberTask(processId, Process, Engine, kernel);
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public SysVShmManager Manager { get; }
        public Process Process { get; }

        public void Dispose()
        {
            GC.KeepAlive(_task);
            // Engine.Dispose() currently logs full stack traces, which makes test output unreadable.
            // Let process teardown reclaim the mock test engines.
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, LinuxConstants.PageSize, "[test]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }
    }
}