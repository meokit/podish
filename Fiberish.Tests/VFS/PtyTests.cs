using Fiberish.Core;
using Fiberish.Core.VFS.TTY;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fiberish.Tests.VFS;

public class PtyTests
{
    private readonly ILogger _logger = NullLogger.Instance;
    private static KernelScheduler NewScheduler() => new();

    [Fact]
    public void PtyManager_EncodeRdev_CorrectEncoding()
    {
        // Test encoding
        var rdev = PtyManager.EncodeRdev(5, 2);
        Assert.Equal((uint)0x502, rdev);

        rdev = PtyManager.EncodeRdev(136, 0);
        Assert.Equal((uint)0x8800, rdev);

        rdev = PtyManager.EncodeRdev(136, 5);
        Assert.Equal((uint)0x8805, rdev);
    }

    [Fact]
    public void PtyManager_DecodeRdev_CorrectDecoding()
    {
        // Test decoding
        var (major, minor) = PtyManager.DecodeRdev(0x502);
        Assert.Equal(5u, major);
        Assert.Equal(2u, minor);

        (major, minor) = PtyManager.DecodeRdev(0x8800);
        Assert.Equal(136u, major);
        Assert.Equal(0u, minor);

        (major, minor) = PtyManager.DecodeRdev(0x8805);
        Assert.Equal(136u, major);
        Assert.Equal(5u, minor);
    }

    [Fact]
    public void PtyManager_GetPtmxRdev_ReturnsCorrectValue()
    {
        var rdev = PtyManager.GetPtmxRdev();
        Assert.Equal((uint)0x502, rdev); // Major 5, Minor 2

        var (major, minor) = PtyManager.DecodeRdev(rdev);
        Assert.Equal(PtyManager.PTMX_MAJOR, major);
        Assert.Equal(PtyManager.PTMX_MINOR, minor);
    }

    [Fact]
    public void PtyManager_GetPtsRdev_ReturnsCorrectValue()
    {
        var rdev = PtyManager.GetPtsRdev(0);
        Assert.Equal((uint)0x8800, rdev); // Major 136, Minor 0

        rdev = PtyManager.GetPtsRdev(5);
        Assert.Equal((uint)0x8805, rdev); // Major 136, Minor 5

        var (major, minor) = PtyManager.DecodeRdev(rdev);
        Assert.Equal(PtyManager.PTS_MAJOR, major);
        Assert.Equal(5u, minor);
    }

    [Fact]
    public void PtyManager_AllocatePty_ReturnsNonNull()
    {
        var manager = new PtyManager(_logger, NewScheduler());
        var pair = manager.AllocatePty();

        Assert.NotNull(pair);
        Assert.Equal(0, pair.Index);
        Assert.NotNull(pair.Master);
        Assert.NotNull(pair.Slave);
    }

    [Fact]
    public void PtyManager_AllocateMultiplePtys_IncrementsIndex()
    {
        var manager = new PtyManager(_logger, NewScheduler());

        var pair1 = manager.AllocatePty();
        var pair2 = manager.AllocatePty();
        var pair3 = manager.AllocatePty();

        Assert.NotNull(pair1);
        Assert.NotNull(pair2);
        Assert.NotNull(pair3);

        Assert.Equal(0, pair1.Index);
        Assert.Equal(1, pair2.Index);
        Assert.Equal(2, pair3.Index);
    }

    [Fact]
    public void PtyManager_GetPty_ReturnsCorrectPair()
    {
        var manager = new PtyManager(_logger, NewScheduler());
        var pair = manager.AllocatePty();

        var retrieved = manager.GetPty(0);
        Assert.Same(pair, retrieved);
    }

    [Fact]
    public void PtyManager_PtyExists_ReturnsTrueForAllocated()
    {
        var manager = new PtyManager(_logger, NewScheduler());

        Assert.False(manager.PtyExists(0));

        manager.AllocatePty();
        Assert.True(manager.PtyExists(0));
        Assert.False(manager.PtyExists(1));
    }

    [Fact]
    public void PtyBuffer_WriteAndRead_WorksCorrectly()
    {
        var buffer = new PtyBuffer(NewScheduler(), 1024);
        var data = new byte[] { 1, 2, 3, 4, 5 };

        var written = buffer.Write(data);
        Assert.Equal(5, written);
        Assert.True(buffer.HasData);
        Assert.Equal(5, buffer.Count);

        var readBuffer = new byte[10];
        var read = buffer.Read(readBuffer);
        Assert.Equal(5, read);
        Assert.Equal(1, readBuffer[0]);
        Assert.Equal(5, readBuffer[4]);
    }

    [Fact]
    public void PtyBuffer_WriteBeyondCapacity_Truncates()
    {
        var buffer = new PtyBuffer(NewScheduler(), 10);
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };

        var written = buffer.Write(data);
        Assert.Equal(10, written);

        // Buffer is full, next write should return 0
        written = buffer.Write(new byte[] { 13, 14 });
        Assert.Equal(0, written);
    }

    [Fact]
    public void PtyBuffer_ReadPartial_ReturnsAvailable()
    {
        var buffer = new PtyBuffer(NewScheduler(), 1024);
        buffer.Write(new byte[] { 1, 2, 3, 4, 5 });

        var readBuffer = new byte[3];
        var read = buffer.Read(readBuffer);
        Assert.Equal(3, read);
        Assert.Equal(1, readBuffer[0]);
        Assert.Equal(3, readBuffer[2]);

        // Read remaining
        read = buffer.Read(readBuffer);
        Assert.Equal(2, read);
        Assert.Equal(4, readBuffer[0]);
        Assert.Equal(5, readBuffer[1]);
    }

    [Fact]
    public void PtyBuffer_ReadDrain_ResetsDataAvailableSignal()
    {
        var scheduler = NewScheduler();
        var process = new Process(700, null!, null!);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(701, process, new Engine(), scheduler);
        var buffer = new PtyBuffer(scheduler, 1024);

        void Entry()
        {
            buffer.Write("abc"u8);
            Assert.True(buffer.DataAvailable.IsSignaled);

            Span<byte> readBuffer = stackalloc byte[8];
            var read = buffer.Read(readBuffer);
            Assert.Equal(3, read);
            Assert.False(buffer.HasData);
            Assert.False(buffer.DataAvailable.IsSignaled);

            task.Exited = true;
            task.Status = FiberTaskStatus.Terminated;
        }

        task.Continuation = Entry;
        scheduler.RegisterTask(task);
        scheduler.Run(100);
    }

    [Fact]
    public void PtyPair_Unlock_SetsIsLockedToFalse()
    {
        var manager = new PtyManager(_logger, NewScheduler());
        var pair = manager.AllocatePty();

        // Default is unlocked (modern behavior)
        Assert.False(pair!.IsLocked);

        // Test unlock is idempotent
        pair.Unlock();
        Assert.False(pair.IsLocked);
    }

    [Fact]
    public void PtyMaster_ReadWrite_WorksCorrectly()
    {
        var manager = new PtyManager(_logger, NewScheduler());
        var pair = manager.AllocatePty();

        // Write from master (goes to slave's input)
        var data = new[] { (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o' };
        var written = pair!.Master.Write(data);
        Assert.Equal(5, written);

        // Read from slave's input buffer
        var readBuffer = new byte[10];
        var read = pair.Master!.InputBuffer.Read(readBuffer);
        Assert.Equal(5, read);
    }

    [Fact]
    public void PtyMaster_HasDataAvailable_ReflectsBufferState()
    {
        var manager = new PtyManager(_logger, NewScheduler());
        var pair = manager.AllocatePty();

        Assert.False(pair!.Master.HasDataAvailable);

        // Write to slave output buffer (simulating slave output)
        pair.Master!.OutputBuffer.Write(new byte[] { 1, 2, 3 });

        Assert.True(pair.Master.HasDataAvailable);
    }

    [Fact]
    public void PtmxInode_Tiocswinsz_ForwardsToSlaveDiscipline()
    {
        using var ctx = new PtyTaskContext();
        var broadcaster = new MockSignalBroadcaster();
        var superBlock = new TmpfsSuperBlock(new FileSystemType { Name = "tmpfs" }, new DeviceNumberManager());
        var ptmxInode = new PtmxInode(superBlock, ctx.Manager, broadcaster, _logger);
        var linuxFile = new LinuxFile(new Dentry(FsName.FromString("ptmx"), ptmxInode, null, ptmxInode.SuperBlock), FileFlags.O_RDWR, null!);
        ptmxInode.Open(linuxFile);

        var pair = Assert.IsType<PtyPair>(linuxFile.PrivateData);
        var winsizePtr = 0x20000u;
        ctx.MapUserPage(winsizePtr);
        ctx.Engine.CopyToUser(winsizePtr, new byte[] { 40, 0, 100, 0, 0, 0, 0, 0 });

        var setRc = ptmxInode.Ioctl(linuxFile, ctx.Task, LinuxConstants.TIOCSWINSZ, winsizePtr);
        Assert.Equal(0, setRc);
        Assert.NotNull(pair.Slave.Discipline);

        var readBack = new byte[LinuxConstants.WINSIZE_SIZE];
        var getRc = pair.Slave.Discipline!.GetWindowSize(readBack);
        Assert.Equal(0, getRc);
        Assert.Equal(40, BitConverter.ToUInt16(readBack, 0));
        Assert.Equal(100, BitConverter.ToUInt16(readBack, 2));
    }

    [Fact]
    public void PtyMaster_Write_RoutesThroughSlaveDiscipline_WhenPresent()
    {
        var manager = new PtyManager(_logger, NewScheduler());
        var pair = manager.AllocatePty();
        var discipline = pair!.Slave.GetOrCreateDiscipline(new MockSignalBroadcaster(), _logger, NewScheduler());

        var written = pair.Master.Write("hello\n"u8);
        Assert.Equal(6, written);
        discipline.ProcessPendingInput();

        var buffer = new byte[16];
        var read = pair.Slave.Read(null, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(6, read);
        Assert.Equal("hello\n", System.Text.Encoding.ASCII.GetString(buffer, 0, read));
    }

    private sealed class PtyTaskContext : IDisposable
    {
        public PtyTaskContext()
        {
            Engine = new Engine();
            Memory = new VMAManager();
            SyscallManager = new SyscallManager(Engine, Memory, 0);
            Scheduler = new KernelScheduler();
            Process = new Process(1234, Memory, SyscallManager)
            {
                PGID = 1234,
                SID = 1234
            };
            Scheduler.RegisterProcess(Process);
            Task = new FiberTask(1234, Process, Engine, Scheduler);
            Engine.Owner = Task;
            Scheduler.CurrentTask = Task;
            Manager = new PtyManager(NullLogger.Instance, Scheduler);
        }

        public Engine Engine { get; }
        public VMAManager Memory { get; }
        public SyscallManager SyscallManager { get; }
        public KernelScheduler Scheduler { get; }
        public Process Process { get; }
        public FiberTask Task { get; }
        public PtyManager Manager { get; }

        public void MapUserPage(uint addr)
        {
            ProcessAddressSpaceSync.Mmap(Memory, Engine, addr, 4096, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]");
            Assert.True(Memory.PrefaultRange(addr, 4096, Engine, true));
        }

        public void Dispose()
        {
            SyscallManager.Close();
            GC.KeepAlive(Task);
        }
    }

    private sealed class MockSignalBroadcaster : ISignalBroadcaster
    {
        public void SignalProcessGroup(FiberTask? task, int pgid, int signal)
        {
        }

        public void SignalForegroundTask(FiberTask? task, int signal)
        {
        }
    }
}
