using System.Buffers.Binary;
using System.Reflection;
using System.Threading;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class GetRusageSyscallTests
{
    private const int Rusage32Size = 72;
    private const int RusageSelf = 0;
    private const int RusageChildren = -1;

    [Fact]
    public async Task DispatchSyscall_GetRusage_IsHandledAndWritesUsage()
    {
        using var env = new TestEnv();
        const uint usagePtr = 0x10000;
        env.MapUserPage(usagePtr);

        var (handled, rc) = await env.Dispatch(X86SyscallNumbers.getrusage, unchecked((uint)RusageSelf), usagePtr);

        Assert.True(handled);
        Assert.Equal(0, rc);
        Assert.True(env.ReadInt32(usagePtr) >= 0);
        Assert.InRange(env.ReadInt32(usagePtr + 4), 0, 999_999);
        Assert.Equal(0, env.ReadInt32(usagePtr + 8));
        Assert.Equal(0, env.ReadInt32(usagePtr + 12));
    }

    [Fact]
    public async Task SysGetRusage_Children_WritesZeroedStruct()
    {
        using var env = new TestEnv();
        const uint usagePtr = 0x11000;
        env.MapUserPage(usagePtr);

        var rc = await env.Invoke("SysGetRusage", unchecked((uint)RusageChildren), usagePtr, 0, 0, 0, 0);

        Assert.Equal(0, rc);
        Assert.All(env.ReadBytes(usagePtr, Rusage32Size), b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task SysGetRusage_InvalidWho_ReturnsEinval()
    {
        using var env = new TestEnv();
        const uint usagePtr = 0x12000;
        env.MapUserPage(usagePtr);

        var rc = await env.Invoke("SysGetRusage", 1234, usagePtr, 0, 0, 0, 0);

        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task SysGetRusage_Thread_IncludesActiveGuestSlice()
    {
        using var env = new TestEnv();
        const uint usagePtr = 0x13000;
        env.MapUserPage(usagePtr);

        env.Task.BeginGuestCpuAccounting();
        Thread.Sleep(10);

        try
        {
            var rc = await env.Invoke("SysGetRusage", unchecked((uint)1), usagePtr, 0, 0, 0, 0);

            Assert.Equal(0, rc);
            Assert.True(env.ReadInt32(usagePtr) > 0 || env.ReadInt32(usagePtr + 4) > 0);
        }
        finally
        {
            env.Task.EndGuestCpuAccounting();
        }
    }

    [Fact]
    public async Task SysGetRusage_SelfAndChildren_UseProcessAccounting()
    {
        using var env = new TestEnv();
        const uint selfUsagePtr = 0x14000;
        const uint childrenUsagePtr = 0x15000;
        env.MapUserPage(selfUsagePtr);
        env.MapUserPage(childrenUsagePtr);

        env.Task.AddCpuTime(11_000_000);
        env.Process.AccumulateExitedThreadCpuTime(new CpuTimeSnapshot(7_000_000, 0));
        env.Process.AccumulateChildrenCpuTime(new CpuTimeSnapshot(5_000_000, 0));

        Assert.Equal(0, await env.Invoke("SysGetRusage", unchecked((uint)RusageSelf), selfUsagePtr, 0, 0, 0, 0));
        Assert.Equal(0,
            await env.Invoke("SysGetRusage", unchecked((uint)RusageChildren), childrenUsagePtr, 0, 0, 0, 0));

        Assert.Equal(0, env.ReadInt32(selfUsagePtr));
        Assert.Equal(18_000, env.ReadInt32(selfUsagePtr + 4));
        Assert.Equal(0, env.ReadInt32(childrenUsagePtr));
        Assert.Equal(5_000, env.ReadInt32(childrenUsagePtr + 4));
    }

    [Fact]
    public async Task SysTimes_UsesSelfAndChildrenTicks()
    {
        using var env = new TestEnv();
        const uint tmsPtr = 0x16000;
        env.MapUserPage(tmsPtr);

        env.Task.AddCpuTime(50_000_000);
        env.Process.AccumulateExitedThreadCpuTime(new CpuTimeSnapshot(20_000_000, 0));
        env.Process.AccumulateChildrenCpuTime(new CpuTimeSnapshot(30_000_000, 0));

        var rc = await env.Invoke("SysTimes", tmsPtr, 0, 0, 0, 0, 0);

        Assert.True(rc >= 0);
        Assert.Equal(7, env.ReadInt32(tmsPtr));
        Assert.Equal(0, env.ReadInt32(tmsPtr + 4));
        Assert.Equal(3, env.ReadInt32(tmsPtr + 8));
        Assert.Equal(0, env.ReadInt32(tmsPtr + 12));
    }

    [Fact]
    public async Task SysClockGetTime64_CpuClocks_UseThreadAndProcessAccounting()
    {
        using var env = new TestEnv();
        const uint processTsPtr = 0x17000;
        const uint threadTsPtr = 0x18000;
        env.MapUserPage(processTsPtr);
        env.MapUserPage(threadTsPtr);

        env.Task.AddCpuTime(25_000_000);
        env.Process.AccumulateExitedThreadCpuTime(new CpuTimeSnapshot(15_000_000, 0));

        Assert.Equal(0,
            await env.Invoke("SysClockGetTime64", LinuxConstants.CLOCK_PROCESS_CPUTIME_ID, processTsPtr, 0, 0, 0,
                0));
        Assert.Equal(0,
            await env.Invoke("SysClockGetTime64", LinuxConstants.CLOCK_THREAD_CPUTIME_ID, threadTsPtr, 0, 0, 0, 0));

        Assert.Equal(40_000_000L, env.ReadInt64(processTsPtr + 8));
        Assert.Equal(25_000_000L, env.ReadInt64(threadTsPtr + 8));
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly TestRuntimeFactory _runtime = new();

        public TestEnv()
        {
            Engine = _runtime.CreateEngine();
            Vma = _runtime.CreateAddressSpace();
            SyscallManager = new SyscallManager(Engine, Vma, 0);
            Process = new Process(100, Vma, SyscallManager);
            Scheduler = new KernelScheduler();
            Task = new FiberTask(100, Process, Engine, Scheduler);
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
            SyscallManager.Close();
        }

        public ValueTask<int> Invoke(string methodName, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
        {
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            return (ValueTask<int>)method!.Invoke(SyscallManager, [Engine, a1, a2, a3, a4, a5, a6])!;
        }

        public async ValueTask<(bool Handled, int Rc)> Dispatch(uint eax, uint ebx = 0, uint ecx = 0, uint edx = 0,
            uint esi = 0, uint edi = 0, uint ebp = 0)
        {
            var method = typeof(SyscallManager).GetMethod("DispatchSyscall",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            object?[] args = [eax, Engine, ebx, ecx, edx, esi, edi, ebp, false];
            var task = (ValueTask<int>)method!.Invoke(SyscallManager, args)!;
            var rc = await task;
            return ((bool)args[8]!, rc);
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public byte[] ReadBytes(uint addr, int length)
        {
            var buf = new byte[length];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return buf;
        }

        public int ReadInt32(uint addr)
        {
            var buf = new byte[4];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return BinaryPrimitives.ReadInt32LittleEndian(buf);
        }

        public long ReadInt64(uint addr)
        {
            var buf = new byte[8];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return BinaryPrimitives.ReadInt64LittleEndian(buf);
        }
    }
}
