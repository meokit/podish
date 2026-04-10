using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Core.VFS.TTY;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class TtyIoContextRegressionTests
{
    [Fact]
    public async Task Sendfile64_BackgroundReadFromTty_SendsSigttin()
    {
        using var env = new TestEnv();
        env.Process.PGID = 200;

        var rc = await CallSysSendfile64(env, 1, 0, 4);

        Assert.Equal(-(int)Errno.ERESTARTSYS, rc);
        Assert.True(env.Broadcaster.SignalSent);
        Assert.Equal((int)Signal.SIGTTIN, env.Broadcaster.LastSignal);
    }

    [Fact]
    public async Task Sendfile64_ForegroundReadFromTty_WritesIntoPipe()
    {
        using var env = new TestEnv();
        env.Tty.Input(Encoding.ASCII.GetBytes("abc\n"));
        env.Tty.ProcessPendingInput(env.Task);

        var rc = await CallSysSendfile64(env, 1, 0, 3);

        Assert.Equal(3, rc);
        Assert.False(env.Broadcaster.SignalSent);

        var buffer = new byte[8];
        var read = env.PipeReadFile.OpenedInode!.ReadToHost(env.Task, env.PipeReadFile, buffer, 0);
        Assert.Equal(3, read);
        Assert.Equal("abc", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task Splice_BackgroundReadFromTty_SendsSigttin()
    {
        using var env = new TestEnv();
        env.Process.PGID = 200;

        var rc = await CallSysSplice(env, 0, 0, 1, 0, 4, 0);

        Assert.Equal(-(int)Errno.ERESTARTSYS, rc);
        Assert.True(env.Broadcaster.SignalSent);
        Assert.Equal((int)Signal.SIGTTIN, env.Broadcaster.LastSignal);
    }

    [Fact]
    public async Task Splice_ForegroundReadFromTty_WritesIntoPipe()
    {
        using var env = new TestEnv();
        env.Tty.Input(Encoding.ASCII.GetBytes("xyz\n"));
        env.Tty.ProcessPendingInput(env.Task);

        var rc = await CallSysSplice(env, 0, 0, 1, 0, 3, 0);

        Assert.Equal(3, rc);
        Assert.False(env.Broadcaster.SignalSent);

        var buffer = new byte[8];
        var read = env.PipeReadFile.OpenedInode!.ReadToHost(env.Task, env.PipeReadFile, buffer, 0);
        Assert.Equal(3, read);
        Assert.Equal("xyz", Encoding.ASCII.GetString(buffer, 0, read));
    }

    private static ValueTask<int> CallSysSendfile64(TestEnv env, uint outFd, uint inFd, uint count)
    {
        var method = typeof(SyscallManager).GetMethod("SysSendfile64", BindingFlags.NonPublic | BindingFlags.Instance);
        var previous = env.Engine.CurrentSyscallManager;
        env.Engine.CurrentSyscallManager = env.SyscallManager;
        try
        {
            return (ValueTask<int>)method!.Invoke(env.SyscallManager, [env.Engine, outFd, inFd, 0u, count, 0u, 0u])!;
        }
        finally
        {
            env.Engine.CurrentSyscallManager = previous;
        }
    }

    private static ValueTask<int> CallSysSplice(TestEnv env, uint fdIn, uint offInPtr, uint fdOut, uint offOutPtr,
        uint len, uint flags)
    {
        var method = typeof(SyscallManager).GetMethod("SysSplice", BindingFlags.NonPublic | BindingFlags.Instance);
        var previous = env.Engine.CurrentSyscallManager;
        env.Engine.CurrentSyscallManager = env.SyscallManager;
        try
        {
            return (ValueTask<int>)method!.Invoke(env.SyscallManager,
                [env.Engine, fdIn, offInPtr, fdOut, offOutPtr, len, flags])!;
        }
        finally
        {
            env.Engine.CurrentSyscallManager = previous;
        }
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
            SyscallManager = new SyscallManager(Engine, Vma, 0);
            Scheduler = new KernelScheduler();
            Process = new Process(1234, Vma, SyscallManager);
            Scheduler.RegisterProcess(Process);
            Task = new FiberTask(1234, Process, Engine, Scheduler);
            Engine.Owner = Task;
            Scheduler.CurrentTask = Task;
            Process.PGID = 100;
            Process.SID = 100;

            Broadcaster = new MockSignalBroadcaster();
            Tty = new TtyDiscipline(new MockTtyDriver(), Broadcaster, NullLogger.Instance, Scheduler)
            {
                ForegroundPgrp = 100,
                SessionId = 100
            };
            Process.ControllingTty = Tty;

            var stdinInode = new ConsoleInode(SyscallManager.MemfdSuperBlock, true, Tty);
            var stdinDentry = new Dentry(FsName.FromString("stdin"), stdinInode, null, SyscallManager.MemfdSuperBlock);
            var stdinFile = new LinuxFile(stdinDentry, FileFlags.O_RDONLY, SyscallManager.AnonMount);
            SyscallManager.FDs[0] = stdinFile;

            var pipe = new PipeInode(Scheduler)
            {
                SuperBlock = SyscallManager.MemfdSuperBlock
            };
            var pipeReadDentry = new Dentry(FsName.FromString("pipe:[read]"), pipe, null, SyscallManager.MemfdSuperBlock);
            var pipeWriteDentry = new Dentry(FsName.FromString("pipe:[write]"), pipe, null, SyscallManager.MemfdSuperBlock);
            PipeReadFile = new LinuxFile(pipeReadDentry, FileFlags.O_RDONLY, SyscallManager.AnonMount);
            var pipeWriteFile = new LinuxFile(pipeWriteDentry, FileFlags.O_WRONLY, SyscallManager.AnonMount);
            SyscallManager.FDs[1] = pipeWriteFile;
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public SyscallManager SyscallManager { get; }
        public KernelScheduler Scheduler { get; }
        public Process Process { get; }
        public FiberTask Task { get; }
        public MockSignalBroadcaster Broadcaster { get; }
        public TtyDiscipline Tty { get; }
        public LinuxFile PipeReadFile { get; }

        public void Dispose()
        {
            SyscallManager.Close();
            GC.KeepAlive(Task);
        }
    }

    private sealed class MockSignalBroadcaster : ISignalBroadcaster
    {
        public int LastSignal { get; private set; }
        public bool SignalSent { get; private set; }

        public void SignalProcessGroup(FiberTask? task, int pgid, int signal)
        {
            _ = task;
            _ = pgid;
            LastSignal = signal;
            SignalSent = true;
        }

        public void SignalForegroundTask(FiberTask? task, int signal)
        {
            _ = task;
            LastSignal = signal;
            SignalSent = true;
        }
    }

    private sealed class MockTtyDriver : ITtyDriver
    {
        public int Write(TtyEndpointKind kind, ReadOnlySpan<byte> buffer)
        {
            _ = kind;
            _ = buffer;
            return buffer.Length;
        }

        public bool CanWrite => true;

        public bool RegisterWriteWait(Action callback, KernelScheduler scheduler)
        {
            _ = callback;
            _ = scheduler;
            return false;
        }

        public void Flush()
        {
        }
    }
}