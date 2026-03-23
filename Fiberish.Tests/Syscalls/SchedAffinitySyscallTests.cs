using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class SchedAffinitySyscallTests
{
    [Fact]
    public async Task SchedGetAffinity_ReturnsSingleCpuMask()
    {
        using var env = new TestEnv();
        const uint maskPtr = 0x10000;
        env.MapUserPage(maskPtr);

        var rc = await env.Invoke("SysSchedGetAffinity", 0, sizeof(uint), maskPtr, 0, 0, 0);

        Assert.Equal(sizeof(uint), rc);
        var mask = new byte[sizeof(uint)];
        Assert.True(env.Engine.CopyFromUser(maskPtr, mask));
        Assert.Equal(0x01, mask[0]);
        Assert.Equal(0x00, mask[1]);
        Assert.Equal(0x00, mask[2]);
        Assert.Equal(0x00, mask[3]);
    }

    [Fact]
    public async Task SchedGetAffinity_TooSmallBuffer_ReturnsEinval()
    {
        using var env = new TestEnv();
        const uint maskPtr = 0x11000;
        env.MapUserPage(maskPtr);

        var rc = await env.Invoke("SysSchedGetAffinity", 0, sizeof(ushort), maskPtr, 0, 0, 0);

        Assert.Equal(-(int)Errno.EINVAL, rc);
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
            SyscallManager = new SyscallManager(Engine, Vma, 0);
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public Process Process { get; }
        public KernelScheduler Scheduler { get; }
        public FiberTask Task { get; }
        public SyscallManager SyscallManager { get; }

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
    }
}
