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

public sealed class LtpRegressionTests
{
    [Theory]
    [InlineData("SysUname")]
    [InlineData("SysGetTimeOfDay")]
    public async Task OutputPointers_MustReportEfault(string syscall)
    {
        using var env = new TestEnv();
        var rc = syscall == "SysUname" ? await env.Call(syscall, 0xDEAD0000)
            : await env.Call(syscall, 0, 0xDEAD0000);
        Assert.Equal(-(int)Errno.EFAULT, rc);
    }

    [Theory]
    [InlineData(uint.MaxValue)]
    [InlineData(16u)]
    public async Task Dup2_RejectsDestinationOutsideRlimit(uint destination)
    {
        using var env = new TestEnv();
        var fd = await env.CreateFile();
        Assert.True(env.Process.TrySetResourceLimit(LinuxConstants.RLIMIT_NOFILE, new ResourceLimit(16, 16)));
        Assert.Equal(-(int)Errno.EBADF, await env.Call("SysDup2", (uint)fd, destination));
        Assert.NotNull(env.Syscalls.GetFD(fd));
        Assert.True(env.Process.TrySetResourceLimit(LinuxConstants.RLIMIT_NOFILE, new ResourceLimit(0, 16)));
        Assert.Equal(fd, await env.Call("SysDup2", (uint)fd, (uint)fd));
    }

    [Theory]
    [InlineData("SysReadV")]
    [InlineData("SysWriteV")]
    [InlineData("SysPReadV")]
    [InlineData("SysPWriteV")]
    [InlineData("SysPReadV2")]
    [InlineData("SysPWriteV2")]
    public async Task VectorIo_DistinguishesInvalidCountsLengthsAndPointers(string syscall)
    {
        using var env = new TestEnv();
        var fd = (uint)await env.CreateFile();
        Assert.Equal(-(int)Errno.EINVAL, await env.Call(syscall, fd, 0xDEAD0000, uint.MaxValue));
        Assert.Equal(-(int)Errno.EINVAL, await env.Call(syscall, fd, 0xDEAD0000, 1025));
        Assert.Equal(-(int)Errno.EFAULT, await env.Call(syscall, fd, 0xDEAD0000, 1));
        env.WriteIovec(0x11000, 0x12000, uint.MaxValue);
        Assert.Equal(-(int)Errno.EINVAL, await env.Call(syscall, fd, 0x11000, 1));
        if (syscall.Contains("Read", StringComparison.Ordinal))
        {
            // Linux caps the total, but does not reject multiple individually
            // valid lengths just because their sum exceeds SSIZE_MAX.
            env.WriteIovec(0x11000, 0x12000, 0x40000000);
            env.WriteIovec(0x11008, 0x13000, 0x40000000);
            Assert.Equal(0, await env.Call(syscall, fd, 0x11000, 2));
        }
    }

    [Fact]
    public async Task DirectoryRead_ReportsEisdir()
    {
        using var env = new TestEnv();
        env.WriteString(0x10000, "/");
        var fd = (uint)await env.Call("SysOpen", 0x10000);
        Assert.Equal(-(int)Errno.EISDIR, await env.Call("SysRead", fd, 0x12000, 8));
        env.WriteIovec(0x11000, 0x12000, 8);
        Assert.Equal(-(int)Errno.EISDIR, await env.Call("SysReadV", fd, 0x11000, 1));
    }

    [Theory]
    [InlineData("SysPRead")]
    [InlineData("SysPWrite")]
    public async Task PositionedIo_RejectsPipesAndNegativeOffsets(string syscall)
    {
        using var env = new TestEnv();
        var (reader, writer) = await env.CreatePipe(0);
        var fd = syscall == "SysPRead" ? reader : writer;
        Assert.Equal(-(int)Errno.ESPIPE, await env.Call(syscall, (uint)fd, 0x12000, 1));
        var regular = (uint)await env.CreateFile();
        Assert.Equal(-(int)Errno.EINVAL,
            await env.Call(syscall, regular, 0x12000, 1, uint.MaxValue, uint.MaxValue));
    }

    [Theory]
    [InlineData("SysFtruncate")]
    [InlineData("SysFtruncate64")]
    public async Task Truncate_RejectsNegativeLengthWithoutGrowingTheFile(string syscall)
    {
        using var env = new TestEnv();
        var fd = (uint)await env.CreateFile();
        Assert.Equal(-(int)Errno.EINVAL, await env.Call(syscall, fd, uint.MaxValue, uint.MaxValue));
        Assert.Equal(0UL, env.Syscalls.GetFD((int)fd)!.OpenedInode!.Size);
    }

    [Theory]
    [InlineData("SysTruncate")]
    [InlineData("SysTruncate64")]
    public async Task PathTruncate_RejectsSignedNegativeLength(string syscall)
    {
        using var env = new TestEnv();
        var fd = await env.CreateFile();
        Assert.Equal(-(int)Errno.EINVAL, await env.Call(syscall, 0x10000, uint.MaxValue, uint.MaxValue));
        Assert.Equal(0UL, env.Syscalls.GetFD(fd)!.OpenedInode!.Size);
    }

    [Theory]
    [InlineData(uint.MaxValue)]
    [InlineData(1u)]
    public async Task EpollCreate1_RejectsUnknownFlags(uint flags)
    {
        using var env = new TestEnv();
        Assert.Equal(-(int)Errno.EINVAL, await env.Call("SysEpollCreate1", flags));
    }

    [Theory]
    [InlineData("SysClockGetRes")]
    [InlineData("SysClockGetResTime64")]
    public async Task ClockResolution_ValidatesClockEvenForNullOutput(string syscall)
    {
        using var env = new TestEnv();
        Assert.Equal(-(int)Errno.EINVAL, await env.Call(syscall, uint.MaxValue, 0));
        Assert.Equal(-(int)Errno.EINVAL, await env.Call(syscall, uint.MaxValue, 0x12000));
        Assert.Equal(0, await env.Call(syscall, LinuxConstants.CLOCK_REALTIME_ALARM, 0x12000));
        Assert.Equal(0, await env.Call(syscall, LinuxConstants.CLOCK_BOOTTIME_ALARM, 0));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 1000000000)]
    public async Task LegacyClockNanosleep_ValidatesTimespec32(int seconds, int nanoseconds)
    {
        using var env = new TestEnv();
        env.WriteInt(0x12000, seconds);
        env.WriteInt(0x12004, nanoseconds);
        Assert.Equal(-(int)Errno.EINVAL,
            await env.Call("SysClockNanosleep", LinuxConstants.CLOCK_MONOTONIC, 0, 0x12000));
    }

    [Fact]
    public async Task LegacyClockNanosleep_PastAbsoluteDeadlineDoesNotWriteRemainingTime()
    {
        using var env = new TestEnv();
        env.WriteInt(0x12000, 0);
        env.WriteInt(0x12004, 0);
        env.WriteInt(0x12008, 123);
        Assert.Equal(0, await env.Call("SysClockNanosleep", LinuxConstants.CLOCK_MONOTONIC, 1, 0x12000, 0x12008));
        Assert.Equal(123, BinaryPrimitives.ReadInt32LittleEndian(env.Read(0x12008, 4)));
    }

    [Fact]
    public async Task ProcPidMax_ExistsInLookupAndEnumeration()
    {
        using var env = new TestEnv();
        env.Syscalls.MountStandardProc();
        var kernel = env.Syscalls.PathWalk("/proc/sys/kernel");
        Assert.Contains(kernel.Dentry!.Inode!.GetEntries(), entry => entry.Name == "pid_max");
        env.WriteString(0x10000, "/proc/sys/kernel/pid_max");
        var fd = (uint)await env.Call("SysOpen", 0x10000);
        var count = await env.Call("SysRead", fd, 0x12000, 32);
        Assert.True(count > 0);
        Assert.Equal(int.MaxValue.ToString() + "\n", Encoding.ASCII.GetString(env.Read(0x12000, count)));
    }

    [Fact]
    public void ProcState_ReportsBlockedTasksAndPreservesLifecycleStates()
    {
        using var env = new TestEnv();
        env.Task.Status = FiberTaskStatus.Waiting;
        Assert.Contains("State:\tS (sleeping)", ProcFsManager.GenerateStatus(env.Process));
        Assert.Contains(") S ", ProcFsManager.GenerateStat(env.Process));
        env.Task.Status = FiberTaskStatus.Ready;
        Assert.Contains(") R ", ProcFsManager.GenerateStat(env.Process));
        env.Process.State = ProcessState.Stopped;
        Assert.Contains(") T ", ProcFsManager.GenerateStat(env.Process));
        env.Process.State = ProcessState.Zombie;
        Assert.Contains(") Z ", ProcFsManager.GenerateStat(env.Process));
    }

    [Fact]
    public async Task PacketPipe_ShortReadDiscardsTailButNotTheNextPacket()
    {
        using var env = new TestEnv();
        var (reader, writer) = await env.CreatePipe(FileFlags.O_DIRECT);
        Assert.Equal((FileFlags)0, env.Syscalls.GetFD(reader)!.Flags & FileFlags.O_DIRECT);
        Assert.NotEqual((FileFlags)0, env.Syscalls.GetFD(writer)!.Flags & FileFlags.O_DIRECT);
        env.WriteString(0x12000, "abcdef");
        Assert.Equal(6, await env.Call("SysWrite", (uint)writer, 0x12000, 6));
        env.WriteString(0x12000, "XYZ");
        Assert.Equal(3, await env.Call("SysWrite", (uint)writer, 0x12000, 3));
        Assert.Equal(0, await env.Call("SysRead", (uint)reader, 0x13000, 0));
        Assert.Equal(2, await env.Call("SysRead", (uint)reader, 0x13000, 2));
        Assert.Equal("ab", Encoding.ASCII.GetString(env.Read(0x13000, 2)));
        Assert.Equal(3, await env.Call("SysRead", (uint)reader, 0x13000, 32));
        Assert.Equal("XYZ", Encoding.ASCII.GetString(env.Read(0x13000, 3)));
    }

    [Fact]
    public async Task PacketPipe_VectorAndGuestPageBoundariesDoNotSplitPackets()
    {
        using var env = new TestEnv();
        var (reader, writer) = await env.CreatePipe(FileFlags.O_DIRECT);
        env.WriteString(0x12FFE, "abcdef");
        env.WriteIovec(0x11000, 0x12FFE, 3);
        env.WriteIovec(0x11008, 0x13001, 3);
        Assert.Equal(6, await env.Call("SysWriteV", (uint)writer, 0x11000, 2));
        // An EFAULT must not consume the queued packet.
        Assert.Equal(-(int)Errno.EFAULT, await env.Call("SysRead", (uint)reader, 0xDEAD0000, 8));
        env.WriteIovec(0x11000, 0x13FFE, 2);
        env.WriteIovec(0x11008, 0x14000, 4);
        Assert.Equal(6, await env.Call("SysReadV", (uint)reader, 0x11000, 2));
        Assert.Equal("abcdef", Encoding.ASCII.GetString(env.Read(0x13FFE, 6)));
    }

    [Fact]
    public async Task PacketPipe_LargeWriteSplitsAtPipeBuf()
    {
        using var env = new TestEnv();
        var (reader, writer) = await env.CreatePipe(FileFlags.O_DIRECT | FileFlags.O_NONBLOCK);
        var data = Enumerable.Repeat((byte)'a', PipeInode.PipeBuf + 5).ToArray();
        Assert.True(env.Engine.CopyToUser(0x12000, data));
        Assert.Equal(data.Length, await env.Call("SysWrite", (uint)writer, 0x12000, (uint)data.Length));
        Assert.Equal(1, await env.Call("SysRead", (uint)reader, 0x14000, 1));
        Assert.Equal(5, await env.Call("SysRead", (uint)reader, 0x14000, 32));
        Assert.Equal(-(int)Errno.EAGAIN, await env.Call("SysRead", (uint)reader, 0x14000, 1));
    }

    [Theory]
    [InlineData(65536u, -(int)Errno.EFAULT)]
    [InlineData(65537u, -(int)Errno.EINVAL)]
    public async Task SetGroups_UsesLinuxLimitAndReportsBadPointer(uint count, int expected)
    {
        using var env = new TestEnv();
        Assert.Equal(expected, await env.Call("SysSetGroups", count, 0xDEAD0000));
    }

    [Fact]
    public async Task OpenNoAtime_UnprivilegedNonOwnerGetsEperm()
    {
        using var env = new TestEnv();
        var fd = await env.CreateFile();
        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
        env.Process.EUID = 1000;
        env.Process.FSUID = 1000;

        env.WriteString(0x10000, "/file");
        Assert.Equal(-(int)Errno.EPERM,
            await env.Call("SysOpen", 0x10000, (uint)(FileFlags.O_RDONLY | FileFlags.O_NOATIME)));
    }

    [Fact]
    public async Task Chroot_NoSearchPermissionReportsEaccesBeforeEperm()
    {
        using var env = new TestEnv();
        env.WriteString(0x10000, "/noexec");
        Assert.Equal(0, await env.Call("SysMkdir", 0x10000, 0x92)); // 0222: write, no search
        env.Process.EUID = 1000;
        env.Process.FSUID = 1000;

        Assert.Equal(-(int)Errno.EACCES, await env.Call("SysChroot", 0x10000));
    }

    [Fact]
    public async Task PacketPipe_NonblockingFullBufferKeepsPacketsAtomic()
    {
        using var env = new TestEnv();
        var (reader, writer) = await env.CreatePipe(FileFlags.O_DIRECT | FileFlags.O_NONBLOCK);
        var data = Enumerable.Repeat((byte)'a', PipeInode.PipeBuf).ToArray();
        Assert.True(env.Engine.CopyToUser(0x12000, data));
        for (var i = 0; i < 16; i++)
            Assert.Equal(PipeInode.PipeBuf, await env.Call("SysWrite", (uint)writer, 0x12000, PipeInode.PipeBuf));
        Assert.Equal(-(int)Errno.EAGAIN, await env.Call("SysWrite", (uint)writer, 0x12000, 1));
        // A one-byte read frees one entire packet, not one byte.
        Assert.Equal(1, await env.Call("SysRead", (uint)reader, 0x14000, 1));
        Assert.Equal(PipeInode.PipeBuf, await env.Call("SysWrite", (uint)writer, 0x12000, PipeInode.PipeBuf));
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly TestRuntimeFactory _factory = new();
        public TestEnv()
        {
            Engine = _factory.CreateEngine();
            Vma = _factory.CreateAddressSpace();
            Engine.PageFaultResolver = (address, write) => Vma.HandleFault(address, write, Engine);
            Syscalls = new SyscallManager(Engine, Vma, 0);
            var fs = FileSystemRegistry.Get("tmpfs")!;
            var sb = fs.CreateAnonymousFileSystem().ReadSuper(fs, 0, "ltp-regression", null);
            Syscalls.InitializeRoot(sb.Root, new Mount(sb, sb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" });
            Process = new Process(100, Vma, Syscalls);
            Scheduler = new KernelScheduler();
            Task = new FiberTask(100, Process, Engine, Scheduler);
            Scheduler.RegisterProcess(Process);
            for (uint address = 0x10000; address <= 0x15000; address += LinuxConstants.PageSize)
            {
                Vma.Mmap(address, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[ltp-test]", Engine);
                Assert.True(Vma.HandleFault(address, true, Engine));
            }
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public SyscallManager Syscalls { get; }
        public Process Process { get; }
        public FiberTask Task { get; }
        public KernelScheduler Scheduler { get; }

        public void Dispose() { Syscalls.Close(); Engine.Dispose(); }
        public void WriteInt(uint address, int value) => Assert.True(Engine.CopyToUser(address, BitConverter.GetBytes(value)));
        public void WriteString(uint address, string value) => Assert.True(Engine.CopyToUser(address, Encoding.UTF8.GetBytes(value + "\0")));
        public void WriteIovec(uint address, uint pointer, uint length)
        {
            WriteInt(address, unchecked((int)pointer));
            WriteInt(address + 4, unchecked((int)length));
        }
        public byte[] Read(uint address, int length)
        {
            var bytes = new byte[length];
            Assert.True(Engine.CopyFromUser(address, bytes));
            return bytes;
        }
        public async ValueTask<int> CreateFile()
        {
            WriteString(0x10000, "/file");
            var fd = await Call("SysOpen", 0x10000, (uint)(FileFlags.O_RDWR | FileFlags.O_CREAT), 0x1A4);
            Assert.True(fd >= 0);
            return fd;
        }
        public async ValueTask<(int Reader, int Writer)> CreatePipe(FileFlags flags)
        {
            Assert.Equal(0, await Call("SysPipe2", 0x10000, (uint)flags));
            var fds = Read(0x10000, 8);
            return (BinaryPrimitives.ReadInt32LittleEndian(fds), BinaryPrimitives.ReadInt32LittleEndian(fds.AsSpan(4)));
        }
        public async ValueTask<int> Call(string name, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0,
            uint a5 = 0, uint a6 = 0)
        {
            var method = typeof(SyscallManager).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            return await (ValueTask<int>)method.Invoke(Syscalls, [Engine, a1, a2, a3, a4, a5, a6])!;
        }
    }
}
