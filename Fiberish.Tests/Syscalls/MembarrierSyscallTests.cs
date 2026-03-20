using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class MembarrierSyscallTests
{
    private static ValueTask<int> CallSysMembarrier(TestEnv env, uint cmd, uint flags = 0)
    {
        var method = typeof(SyscallManager).GetMethod("SysMembarrier", BindingFlags.NonPublic | BindingFlags.Instance);
        return (ValueTask<int>)method!.Invoke(env.SyscallManager, [env.Engine, cmd, flags, 0u, 0u, 0u, 0u])!;
    }

    [Fact]
    public async Task Query_ReturnsSupportedCommandMask()
    {
        using var env = new TestEnv();
        var rc = await CallSysMembarrier(env, LinuxConstants.MEMBARRIER_CMD_QUERY);

        Assert.True((rc & LinuxConstants.MEMBARRIER_CMD_PRIVATE_EXPEDITED) != 0);
        Assert.True((rc & LinuxConstants.MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED) != 0);
        Assert.True((rc & LinuxConstants.MEMBARRIER_CMD_PRIVATE_EXPEDITED_SYNC_CORE) != 0);
        Assert.True((rc & LinuxConstants.MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED_SYNC_CORE) != 0);
    }

    [Fact]
    public async Task PrivateExpeditedSyncCore_RequiresRegistration()
    {
        using var env = new TestEnv();

        var rc = await CallSysMembarrier(env, LinuxConstants.MEMBARRIER_CMD_PRIVATE_EXPEDITED_SYNC_CORE);
        Assert.Equal(-(int)Errno.EPERM, rc);

        rc = await CallSysMembarrier(env, LinuxConstants.MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED_SYNC_CORE);
        Assert.Equal(0, rc);

        rc = await CallSysMembarrier(env, LinuxConstants.MEMBARRIER_CMD_PRIVATE_EXPEDITED_SYNC_CORE);
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task GlobalExpedited_RequiresRegistration()
    {
        using var env = new TestEnv();

        var rc = await CallSysMembarrier(env, LinuxConstants.MEMBARRIER_CMD_GLOBAL_EXPEDITED);
        Assert.Equal(-(int)Errno.EPERM, rc);

        rc = await CallSysMembarrier(env, LinuxConstants.MEMBARRIER_CMD_REGISTER_GLOBAL_EXPEDITED);
        Assert.Equal(0, rc);

        rc = await CallSysMembarrier(env, LinuxConstants.MEMBARRIER_CMD_GLOBAL_EXPEDITED);
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task NonZeroFlags_ReturnsEinval()
    {
        using var env = new TestEnv();
        var rc = await CallSysMembarrier(env, LinuxConstants.MEMBARRIER_CMD_QUERY, 1);
        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    [Fact]
    public async Task UnsupportedCommand_ReturnsEinval()
    {
        using var env = new TestEnv();
        var rc = await CallSysMembarrier(env, LinuxConstants.MEMBARRIER_CMD_PRIVATE_EXPEDITED_RSEQ);
        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
            SyscallManager = new SyscallManager(Engine, Vma, 0);
            Process = new Process(5001, Vma, SyscallManager);
            Scheduler = new KernelScheduler();

            Task = new FiberTask(5001, Process, Engine, Scheduler);
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
            GC.KeepAlive(Task);
        }
    }
}