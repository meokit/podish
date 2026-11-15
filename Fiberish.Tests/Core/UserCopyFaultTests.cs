using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Core;

/// <summary>
///     User-copy faults raised from an async syscall continuation must map to -EFAULT
///     instead of being escalated into a guest SIGSEGV.
/// </summary>
public class UserCopyFaultTests
{
    [Fact]
    public void CopyToUser_ProtNonePage_WhileAsyncSyscallPending_ReturnsFalseWithoutQueueingSigsegv()
    {
        using var env = new TestEnv();
        const uint badAddr = 0x00600000;
        env.Vma.Mmap(badAddr, LinuxConstants.PageSize, Protection.None,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test-bad]", env.Engine);

        // State inside an async syscall continuation: the engine's syscall context is
        // restored when the handler suspends, but the syscall is still in flight.
        Assert.Null(env.Engine.CurrentSyscallManager);
        env.Task.PendingSyscall = new ValueTask<int>(0);
        env.Task.PendingSignals = 0;

        var copied = env.Engine.CopyToUser(badAddr, new byte[8]);

        Assert.False(copied);
        Assert.Equal(0UL, env.Task.PendingSignals);
    }

    [Fact]
    public void CopyToUser_ValidUnmappedPage_WhileAsyncSyscallPending_StillFaultsIn()
    {
        using var env = new TestEnv();
        const uint addr = 0x00601000;
        env.Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test-rw]", env.Engine);

        env.Task.PendingSyscall = new ValueTask<int>(0);
        env.Task.PendingSignals = 0;

        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        Assert.True(env.Engine.CopyToUser(addr, payload));

        var readBack = new byte[8];
        Assert.True(env.Engine.CopyFromUser(addr, readBack));
        Assert.Equal(payload, readBack);
        Assert.Equal(0UL, env.Task.PendingSignals);
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
            GC.KeepAlive(Task);
        }
    }
}
