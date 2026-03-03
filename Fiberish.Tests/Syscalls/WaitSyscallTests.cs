using System.Buffers.Binary;
using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class WaitSyscallTests
{
    private static ValueTask<int> Invoke(TestEnv env, string methodName, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (ValueTask<int>)method!.Invoke(null, [env.Engine.State, a1, a2, a3, a4, a5, a6])!;
    }

    [Fact]
    public async Task Pselect6_ZeroTimeout_NoFds_ReturnsZero()
    {
        using var env = new TestEnv();
        const uint tsPtr = 0x10000;
        env.MapUserPage(tsPtr);
        env.Write(tsPtr, new byte[8]); // timespec{0,0}

        var rc = await Invoke(env, "SysPselect6", 0, 0, 0, 0, tsPtr, 0);
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task Pselect6_InvalidSigsetSize_ReturnsEinval()
    {
        using var env = new TestEnv();
        const uint tsPtr = 0x11000;
        const uint sigArgPtr = 0x12000;
        const uint sigMaskPtr = 0x13000;
        env.MapUserPage(tsPtr);
        env.MapUserPage(sigArgPtr);
        env.MapUserPage(sigMaskPtr);
        env.Write(tsPtr, new byte[8]); // zero timeout
        env.Write(sigMaskPtr, new byte[8]);

        var sigArg = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(sigArg.AsSpan(0, 4), sigMaskPtr);
        BinaryPrimitives.WriteUInt32LittleEndian(sigArg.AsSpan(4, 4), 4); // invalid, expected 8
        env.Write(sigArgPtr, sigArg);

        var rc = await Invoke(env, "SysPselect6", 0, 0, 0, 0, tsPtr, sigArgPtr);
        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task Ppoll_InvalidSigsetSize_ReturnsEinval()
    {
        using var env = new TestEnv();
        const uint sigMaskPtr = 0x14000;
        env.MapUserPage(sigMaskPtr);
        env.Write(sigMaskPtr, new byte[8]);

        var rc = await Invoke(env, "SysPpoll", 0, 0, 0, sigMaskPtr, 4, 0);
        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task PpollTime64_InvalidNsec_ReturnsEinval()
    {
        using var env = new TestEnv();
        const uint ts64Ptr = 0x15000;
        env.MapUserPage(ts64Ptr);
        var ts = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(ts.AsSpan(0, 8), 0);
        BinaryPrimitives.WriteInt64LittleEndian(ts.AsSpan(8, 8), 1_000_000_000); // invalid nsec
        env.Write(ts64Ptr, ts);

        var rc = await Invoke(env, "SysPpollTime64", 0, 0, ts64Ptr, 0, 0, 0);
        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task EpollPwait2_ZeroTimeout_ReturnsZero()
    {
        using var env = new TestEnv();
        const uint eventsPtr = 0x16000;
        const uint ts64Ptr = 0x17000;
        env.MapUserPage(eventsPtr);
        env.MapUserPage(ts64Ptr);
        env.Write(ts64Ptr, new byte[16]); // timespec64 {0,0}

        var epfd = await Invoke(env, "SysEpollCreate1", 0, 0, 0, 0, 0, 0);
        Assert.True(epfd >= 0);

        var rc = await Invoke(env, "SysEpollPwait2", (uint)epfd, eventsPtr, 1, ts64Ptr, 0, 0);
        Assert.Equal(0, rc);
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
            Process = new Process(100, Vma, null!);
            Scheduler = new KernelScheduler();
            Task = new FiberTask(100, Process, Engine, Scheduler);
            Engine.Owner = Task;
            KernelScheduler.Current = Scheduler;

            SyscallManager = new SyscallManager(Engine, Vma, 0);
            SyscallManager.MountRootHostfs(".");
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public Process Process { get; }
        public FiberTask Task { get; }
        public KernelScheduler Scheduler { get; }
        public SyscallManager SyscallManager { get; }

        public void Dispose()
        {
            KernelScheduler.Current = null;
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, LinuxConstants.PageSize, "[test]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void Write(uint addr, ReadOnlySpan<byte> data)
        {
            Assert.True(Engine.CopyToUser(addr, data));
        }
    }
}
