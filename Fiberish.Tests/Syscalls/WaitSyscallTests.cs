using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class WaitSyscallTests
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

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

    [Fact]
    public async Task Ppoll_EventFdWake_CompletesAndSetsRevents()
    {
        using var env = new TestEnv();
        const uint pollfdPtr = 0x18000;
        const uint tsPtr = 0x19000;
        env.MapUserPage(pollfdPtr);
        env.MapUserPage(tsPtr);

        var eventFd = new EventFdInode(10, env.SyscallManager.MemfdSuperBlock, 0, FileFlags.O_RDWR);
        var file = new LinuxFile(new Dentry("eventfd", eventFd, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        var fd = env.SyscallManager.AllocFD(file);

        env.WriteStruct(pollfdPtr, new PollFd
        {
            Fd = fd,
            Events = LinuxConstants.POLLIN,
            Revents = 0
        });

        var ts = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(4, 4), 0);
        env.Write(tsPtr, ts);

        var pending = Invoke(env, "SysPpoll", pollfdPtr, 1, tsPtr, 0, 0, 0).AsTask();
        Assert.False(pending.IsCompleted);

        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, 1);
        Assert.Equal(8, eventFd.Write(file, payload, 0));
        env.DrainEvents();

        var rc = await pending.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, rc);

        var pfd = env.ReadStruct<PollFd>(pollfdPtr);
        Assert.Equal(LinuxConstants.POLLIN, pfd.Revents);
    }

    [Fact]
    public async Task EpollPwait2_ReadyEvent_ReturnsOneAndWritesEvent()
    {
        using var env = new TestEnv();
        const uint eventsPtr = 0x1A000;
        const uint epollEventPtr = 0x1B000;
        env.MapUserPage(eventsPtr);
        env.MapUserPage(epollEventPtr);

        var epfd = await Invoke(env, "SysEpollCreate1", 0, 0, 0, 0, 0, 0);
        Assert.True(epfd >= 0);

        var eventFd = new EventFdInode(11, env.SyscallManager.MemfdSuperBlock, 0, FileFlags.O_RDWR);
        var file = new LinuxFile(new Dentry("eventfd", eventFd, null, env.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, env.SyscallManager.AnonMount);
        var fd = env.SyscallManager.AllocFD(file);

        var epollEvent = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(epollEvent.AsSpan(0, 4), LinuxConstants.EPOLLIN);
        BinaryPrimitives.WriteUInt64LittleEndian(epollEvent.AsSpan(4, 8), 0x1122334455667788UL);
        env.Write(epollEventPtr, epollEvent);

        var ctl = await Invoke(env, "SysEpollCtl", (uint)epfd, LinuxConstants.EPOLL_CTL_ADD, (uint)fd, epollEventPtr, 0, 0);
        Assert.Equal(0, ctl);

        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, 1);
        Assert.Equal(8, eventFd.Write(file, payload, 0));

        var rc = await Invoke(env, "SysEpollPwait2", (uint)epfd, eventsPtr, 1, 0, 0, 0);
        Assert.Equal(1, rc);
        Assert.Equal(LinuxConstants.EPOLLIN, BinaryPrimitives.ReadUInt32LittleEndian(env.Read(eventsPtr, 12).AsSpan(0, 4)));
        Assert.Equal(0x1122334455667788UL, BinaryPrimitives.ReadUInt64LittleEndian(env.Read(eventsPtr, 12).AsSpan(4, 8)));
    }

    private sealed class TestEnv : IDisposable
    {
        private static readonly MethodInfo DrainEventsMethod =
            typeof(KernelScheduler).GetMethod("DrainEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;

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

        public byte[] Read(uint addr, int count)
        {
            var buffer = new byte[count];
            Assert.True(Engine.CopyFromUser(addr, buffer));
            return buffer;
        }

        public void WriteStruct<T>(uint addr, T value) where T : struct
        {
            var size = Marshal.SizeOf<T>();
            var buffer = new byte[size];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), false);
            }
            finally
            {
                handle.Free();
            }
            Write(addr, buffer);
        }

        public T ReadStruct<T>(uint addr) where T : struct
        {
            var buffer = Read(addr, Marshal.SizeOf<T>());
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        public void DrainEvents()
        {
            _ = (bool)DrainEventsMethod.Invoke(Scheduler, null)!;
        }
    }
}
