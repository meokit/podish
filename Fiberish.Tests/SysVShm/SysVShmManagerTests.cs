using System.Buffers.Binary;
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

        Assert.True((uint)shmid1 < 0xFFFFF000u);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShmCtl_Stat_UsesI386LayoutAndRequestedSize(bool ipc64)
    {
        using var ctx = new TestContext();

        const int key = 0x2234;
        const uint size = 4097;
        const int uid = 70000, gid = 90000, creatorPid = 1234, attachPid = 3456;
        const int mode = 0x180;
        var shmid = ctx.Manager.ShmGet(key, size, LinuxConstants.IPC_CREAT | mode, uid, gid, creatorPid);
        Assert.True(shmid > 0);
        Assert.Equal(-(int)Errno.EINVAL, ctx.Manager.ShmGet(key, size + 1, 0, uid, gid, creatorPid));

        const uint userBuf = 0x40000000;
        ctx.MapUserPage(userBuf);
        var buffer = Enumerable.Repeat((byte)0x7d, 85).ToArray();
        Assert.True(ctx.Engine.CopyToUser(userBuf, buffer));

        int command = LinuxConstants.IPC_STAT | (ipc64 ? LinuxConstants.IPC_64 : 0);
        byte[] Stat()
        {
            Assert.Equal(0, ctx.Manager.ShmCtl(shmid, command, userBuf, ctx.Engine, 0, 0, creatorPid));
            Assert.True(ctx.Engine.CopyFromUser(userBuf, buffer));
            return buffer;
        }

        var data = Stat();
        Assert.Equal(key, BinaryPrimitives.ReadInt32LittleEndian(data));
        if (ipc64)
        {
            Assert.Equal(uid, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)));
            Assert.Equal(gid, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8)));
            Assert.Equal(uid, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12)));
            Assert.Equal(gid, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16)));
            Assert.Equal(mode, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(20)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(56 + 4)));
            Assert.Equal(creatorPid, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(64)));
            Assert.Equal(0, BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(76)));
            Assert.Equal(0x7d, data[84]);
        }
        else
        {
            for (int offset = 4; offset <= 10; offset += 2)
                Assert.Equal(65534, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)));
            Assert.Equal(mode, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12)));
            Assert.Equal(creatorPid, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(32)));
            Assert.Equal(0, BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(40)));
            Assert.Equal(0x7d, data[48]);
        }

        Assert.Equal(size, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ipc64 ? 36 : 16)));
        Assert.Equal(0, ReadTime(data, ipc64 ? 40 : 20));
        Assert.Equal(0, ReadTime(data, ipc64 ? 48 : 24));
        Assert.True(ReadTime(data, ipc64 ? 56 : 28) > 0);
        Assert.Equal(0, ReadCounter(data, ipc64 ? 68 : 34));
        Assert.Equal(0, ReadCounter(data, ipc64 ? 72 : 36));

        const uint attachAddress = 0x21000000;
        Assert.Equal(attachAddress, ctx.Manager.ShmAt(shmid, attachAddress, 0, attachPid, ctx.Vma, ctx.Engine));
        Assert.Equal(attachAddress + 8192u, ctx.Vma.FindVmArea(attachAddress)!.End);
        data = Stat();
        Assert.True(ReadTime(data, ipc64 ? 40 : 20) > 0);
        Assert.Equal(0, ReadTime(data, ipc64 ? 48 : 24));
        Assert.Equal(attachPid, ReadCounter(data, ipc64 ? 68 : 34));
        Assert.Equal(1, ReadCounter(data, ipc64 ? 72 : 36));

        Assert.Equal(0, ctx.Manager.ShmDt(attachAddress, attachPid + 1, ctx.Vma, ctx.Engine));
        data = Stat();
        Assert.True(ReadTime(data, ipc64 ? 48 : 24) > 0);
        Assert.Equal(attachPid + 1, ReadCounter(data, ipc64 ? 68 : 34));
        Assert.Equal(0, ReadCounter(data, ipc64 ? 72 : 36));

        long ReadTime(byte[] value, int offset) => ipc64
            ? BinaryPrimitives.ReadInt64LittleEndian(value.AsSpan(offset))
            : BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(offset));

        int ReadCounter(byte[] value, int offset) => ipc64
            ? BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(offset))
            : BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(offset));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShmCtl_Set_ReadsI386OwnerAndModeOffsets(bool ipc64)
    {
        using var ctx = new TestContext();

        int shmid = ctx.Manager.ShmGet(LinuxConstants.IPC_PRIVATE, 10, 0x1ff, 42, 43, 2000);
        Assert.True(shmid > 0);

        const uint address = 0x40000000;
        ctx.MapUserPage(address);
        var data = new byte[ipc64 ? 84 : 48];
        int uid = ipc64 ? 64001 + 65536 : 64001;
        int gid = ipc64 ? 64002 + 65536 : 64002;
        const ushort mode = 0x181;
        if (ipc64)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), uid);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), gid);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), (ushort)(mode | 0x8000));
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)uid);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), (ushort)gid);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)(mode | 0x8000));
        }

        Assert.True(ctx.Engine.CopyToUser(address, data));
        int version = ipc64 ? LinuxConstants.IPC_64 : 0;
        Assert.Equal(0, ctx.Manager.ShmCtl(shmid, LinuxConstants.IPC_SET | version, address, ctx.Engine, 42, 43, 2000));
        Assert.Equal(0, ctx.Manager.ShmCtl(shmid, LinuxConstants.IPC_STAT | version, address, ctx.Engine, uid, gid, 2001));
        Assert.True(ctx.Engine.CopyFromUser(address, data));
        Assert.Equal(uid, ipc64 ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)));
        Assert.Equal(gid, ipc64 ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6)));
        Assert.Equal(mode, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ipc64 ? 20 : 12)));
        Assert.Equal(42, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ipc64 ? 12 : 8)));
        Assert.Equal(43, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ipc64 ? 16 : 10)));
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
        Assert.Equal(-(long)Errno.EINVAL, ret2);
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
        var runtime = new MemoryRuntimeContext();
        var manager = new SysVShmManager(runtime);
        var sharedVma = new VMAManager(runtime);
        sharedVma.AddSharedRef();

        using var p1 = new TestContext(manager, 3101, sharedVma, runtime);
        using var p2 = new TestContext(manager, 3102, sharedVma, runtime);

        var shmid = manager.ShmGet(LinuxConstants.IPC_PRIVATE, 4096, 0x1FF, 0, 0, 3101);
        Assert.True(shmid > 0);

        const uint addr = 0x21400000;
        var attachRet = manager.ShmAt(shmid, addr, 0, 3101, p1.Vma, p1.Engine, p1.Process);
        Assert.Equal(addr, attachRet);

        // Shared VM (CLONE_VM-style): peer process should be able to detach by address space.
        var detachRet = manager.ShmDt(addr, 3102, p2.Vma, p2.Engine, p2.Process);
        Assert.Equal(0, detachRet);
        Assert.Equal(-(long)Errno.EINVAL, manager.ShmDt(addr, 3101, p1.Vma, p1.Engine, p1.Process));
    }

    [Fact]
    public void ShmAt_UsesRuntimeShmMountFileBacking()
    {
        var runtime = new MemoryRuntimeContext();
        using var ctx = new TestContext(runtime: runtime);

        var shmid = ctx.Manager.ShmGet(LinuxConstants.IPC_PRIVATE, 4096, 0x1FF, 0, 0, 2000);
        Assert.True(shmid > 0);

        const uint addr = 0x21800000;
        var attachRet = ctx.Manager.ShmAt(shmid, addr, 0, 2000, ctx.Vma, ctx.Engine, ctx.Process);
        Assert.Equal(addr, attachRet);

        var vma = Assert.IsType<VmArea>(ctx.Vma.FindVmArea(addr));
        Assert.True(vma.IsFileBacked);
        Assert.NotNull(vma.File?.OpenedInode);
        Assert.Same(runtime.GetOrCreateShmSuperBlock(), vma.File!.OpenedInode!.SuperBlock);
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
        Assert.NotEmpty(ctx.Vma.FindVmAreasInRange(addr, addr + 4096));

        ctx.Manager.OnProcessExit(3001, ctx.Vma, ctx.Engine);

        Assert.Empty(ctx.Vma.FindVmAreasInRange(addr, addr + 4096));
    }

    [Fact]
    public void OnProcessExit_WithSharedAddressSpace_MustNotDetach()
    {
        var runtime = new MemoryRuntimeContext();
        var manager = new SysVShmManager(runtime);
        var sharedVma = new VMAManager(runtime);
        sharedVma.AddSharedRef();

        using var p1 = new TestContext(manager, 3201, sharedVma, runtime);
        using var p2 = new TestContext(manager, 3202, sharedVma, runtime);

        var shmid = manager.ShmGet(LinuxConstants.IPC_PRIVATE, 4096, 0x1FF, 0, 0, 3201);
        Assert.True(shmid > 0);

        const uint addr = 0x22400000;
        var attachRet = manager.ShmAt(shmid, addr, 0, 3201, p1.Vma, p1.Engine, p1.Process);
        Assert.Equal(addr, attachRet);
        Assert.NotEmpty(sharedVma.FindVmAreasInRange(addr, addr + 4096));

        manager.OnProcessExit(3201, p1.Vma, p1.Engine, p1.Process);

        // Address space is still shared, so attachment must remain live.
        Assert.NotEmpty(sharedVma.FindVmAreasInRange(addr, addr + 4096));
        Assert.Equal(0, manager.ShmDt(addr, 3202, p2.Vma, p2.Engine, p2.Process));
    }

    [Fact]
    public void ShmDt_MustNotDetachAttachmentOfAnotherProcess()
    {
        var runtime = new MemoryRuntimeContext();
        var manager = new SysVShmManager(runtime);
        using var p1 = new TestContext(manager, 2001, runtime: runtime);
        using var p2 = new TestContext(manager, 2002, runtime: runtime);

        var shmid = manager.ShmGet(0x8899, 4096, LinuxConstants.IPC_CREAT | 0x1FF, 0, 0, 2001);
        Assert.True(shmid > 0);

        const uint addr = 0x23000000;
        Assert.Equal(addr, manager.ShmAt(shmid, addr, 0, 2001, p1.Vma, p1.Engine));
        Assert.Equal(addr, manager.ShmAt(shmid, addr, 0, 2002, p2.Vma, p2.Engine));

        // Detach from process 2 first. This must not remove process 1's attach record.
        Assert.Equal(0, manager.ShmDt(addr, 2002, p2.Vma, p2.Engine));
        Assert.Empty(p2.Vma.FindVmAreasInRange(addr, addr + 4096));
        Assert.NotEmpty(p1.Vma.FindVmAreasInRange(addr, addr + 4096));

        // Process 1 should still be able to detach successfully.
        Assert.Equal(0, manager.ShmDt(addr, 2001, p1.Vma, p1.Engine));
        Assert.Empty(p1.Vma.FindVmAreasInRange(addr, addr + 4096));
    }

    [Fact]
    public void ShmAt_WithShmRemapAndNullAddress_ShouldReturnEinval()
    {
        using var ctx = new TestContext();
        var shmid = ctx.Manager.ShmGet(LinuxConstants.IPC_PRIVATE, 4096, 0x1FF, 0, 0, 2000);
        Assert.True(shmid > 0);

        var ret = ctx.Manager.ShmAt(shmid, 0, LinuxConstants.SHM_REMAP, 2000, ctx.Vma, ctx.Engine, ctx.Process);
        Assert.Equal(-(long)Errno.EINVAL, ret);
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
        Assert.Equal(-(long)Errno.EINVAL, ctx.Manager.ShmDt(addr, 3001, ctx.Vma, ctx.Engine, ctx.Process));
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

    [Fact]
    public void ShmAt_UsesLayoutTaskSizeUpperBound()
    {
        using var ctx = new TestContext();
        ctx.Vma.Layout = CreateLayoutWithTaskSize(0x20000000);

        var shmid = ctx.Manager.ShmGet(LinuxConstants.IPC_PRIVATE, LinuxConstants.PageSize * 2, 0x1FF, 0, 0, 3001);
        Assert.True(shmid > 0);

        var ret = ctx.Manager.ShmAt(shmid, 0x1ffff000, 0, 3001, ctx.Vma, ctx.Engine, ctx.Process);
        Assert.Equal(-(int)Errno.EINVAL, (int)ret);
    }

    private static GuestAddressSpaceLayout CreateLayoutWithTaskSize(uint taskSize)
    {
        var baseLayout = GuestAddressSpaceLayout.CreateCompat32(
            new ResourceLimit(LinuxConstants.RLIM64_INFINITY, LinuxConstants.RLIM64_INFINITY),
            Enumerable.Range(0, 32).Select(i => (byte)(i * 5 + 1)).ToArray());
        var stackTopMax = taskSize - LinuxConstants.PageSize * 2;
        var initialStackTop = stackTopMax - LinuxConstants.PageSize * 4;
        var mmapBase = taskSize - 0x02000000;
        return new GuestAddressSpaceLayout
        {
            TaskSize = taskSize,
            StackTopMax = stackTopMax,
            InitialStackTop = initialStackTop,
            StackLowerBound = initialStackTop - 0x00100000,
            MmapBase = mmapBase,
            LegacyMmapBase = Math.Min(baseLayout.LegacyMmapBase, mmapBase - LinuxConstants.PageSize),
            PieBase = Math.Min(baseLayout.PieBase, mmapBase - 0x00400000),
            InterpreterBaseHint = Math.Min(baseLayout.InterpreterBaseHint, mmapBase - 0x00200000),
            VdsoBaseHint = taskSize - LinuxConstants.PageSize,
            StackRandomOffset = 0,
            MmapRandomOffset = 0,
            StackGuardGap = baseLayout.StackGuardGap,
            AuxRandomBytes = baseLayout.AuxRandomBytes.ToArray()
        };
    }

    private sealed class TestContext : IDisposable
    {
        private readonly FiberTask _task;

        public TestContext(SysVShmManager? manager = null, int processId = 2000, VMAManager? vma = null,
            MemoryRuntimeContext? runtime = null)
        {
            var memoryContext = runtime ?? new MemoryRuntimeContext();
            Engine = new Engine(memoryContext);
            Vma = vma ?? new VMAManager(memoryContext);
            Manager = manager ?? new SysVShmManager(memoryContext);

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
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }
    }
}
