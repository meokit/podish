using System.Buffers.Binary;
using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class SysinfoSyscallTests
{
    [Fact]
    public async Task Sysinfo_WritesExpectedFields()
    {
        using var env = new TestEnv();
        env.MemoryContext.MemoryQuotaBytes = 64L * 1024 * 1024;
        env.RegisterProcess(101);
        env.RegisterProcess(202);

        const uint sysinfoPtr = 0x10000;
        env.MapUserPage(sysinfoPtr);

        var rc = await env.Invoke("SysSysinfo", sysinfoPtr, 0, 0, 0, 0, 0);

        Assert.Equal(0, rc);
        var raw = env.ReadBytes(sysinfoPtr, 64);
        Assert.Equal(64 * 1024 * 1024, env.ReadInt32(sysinfoPtr + 16));
        Assert.Equal(65536, env.ReadInt32(sysinfoPtr + 4));
        Assert.Equal(65536, env.ReadInt32(sysinfoPtr + 8));
        Assert.Equal(65536, env.ReadInt32(sysinfoPtr + 12));
        Assert.Equal(2, env.ReadInt16(sysinfoPtr + 40));
        Assert.Equal(1, env.ReadInt32(sysinfoPtr + 52));
        Assert.InRange(env.ReadInt32(sysinfoPtr + 20), 0, 64 * 1024 * 1024);
        Assert.All(raw[56..64], b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Sysinfo_ScalesMemoryFieldsWhenValuesExceedInt32()
    {
        using var env = new TestEnv();
        env.MemoryContext.MemoryQuotaBytes = (long)int.MaxValue + 2L * LinuxConstants.PageSize;

        const uint sysinfoPtr = 0x12000;
        env.MapUserPage(sysinfoPtr);

        var rc = await env.Invoke("SysSysinfo", sysinfoPtr, 0, 0, 0, 0, 0);

        Assert.Equal(0, rc);
        Assert.Equal(LinuxConstants.PageSize, env.ReadInt32(sysinfoPtr + 52));
        Assert.Equal(env.MemoryContext.MemoryQuotaBytes / LinuxConstants.PageSize, env.ReadInt32(sysinfoPtr + 16));
        Assert.Equal(1, env.ReadInt16(sysinfoPtr + 40));
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly TestRuntimeFactory _runtime = new();

        public TestEnv()
        {
            Engine = _runtime.CreateEngine();
            Vma = _runtime.CreateAddressSpace();
            Process = new Process(100, Vma, null!);
            Scheduler = new KernelScheduler();
            Task = new FiberTask(100, Process, Engine, Scheduler);
            Engine.Owner = Task;
            SyscallManager = new SyscallManager(Engine, Vma, 0);
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public Process Process { get; }
        public KernelScheduler Scheduler { get; }
        public FiberTask Task { get; }
        public SyscallManager SyscallManager { get; }
        public MemoryRuntimeContext MemoryContext => _runtime.MemoryContext;

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

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void RegisterProcess(int pid)
        {
            Scheduler.RegisterProcess(new Process(pid, Vma, SyscallManager));
        }

        public byte[] ReadBytes(uint addr, int length)
        {
            var buf = new byte[length];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return buf;
        }

        public short ReadInt16(uint addr)
        {
            var buf = new byte[2];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return BinaryPrimitives.ReadInt16LittleEndian(buf);
        }

        public int ReadInt32(uint addr)
        {
            var buf = new byte[4];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return BinaryPrimitives.ReadInt32LittleEndian(buf);
        }
    }
}
