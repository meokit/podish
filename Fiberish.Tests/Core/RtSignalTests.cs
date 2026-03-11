using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Xunit;

namespace Fiberish.Tests.Core;

public class RtSignalTests
{
    private static readonly int SIGRTMIN = 34; // Linux SIGRTMIN

    // ─── Standard (non-RT) signals: saturation semantics ─────────────────────

    [Fact]
    public void StandardSignal_PostedMultipleTimes_OnlyQueuedOnce()
    {
        using var env = new TestEnv();

        env.Task.PostSignal((int)Signal.SIGUSR1);
        env.Task.PostSignal((int)Signal.SIGUSR1);
        env.Task.PostSignal((int)Signal.SIGUSR1);

        var count = 0;
        env.Task.PendingSignalQueue.Lock(q =>
        {
            foreach (var s in q)
                if (s.Signo == (int)Signal.SIGUSR1)
                    count++;
        });

        Assert.Equal(1, count); // Should only be in the queue once
    }

    [Fact]
    public void StandardSignal_PendingBitmask_SetOnce()
    {
        using var env = new TestEnv();

        env.Task.PostSignal((int)Signal.SIGUSR1);
        env.Task.PostSignal((int)Signal.SIGUSR1);

        var bit = 1UL << ((int)Signal.SIGUSR1 - 1);
        Assert.NotEqual(0UL, env.Task.PendingSignals & bit); // Bit is set

        // Dequeue once
        SigInfo? info;
        lock (env.Task)
        {
            info = env.Task.DequeueSignalUnsafe((int)Signal.SIGUSR1);
        }

        Assert.NotNull(info);

        // After dequeue, bit should be clear
        Assert.Equal(0UL, env.Task.PendingSignals & bit);
    }

    // ─── Real-time signals: queuing semantics ─────────────────────────────────

    [Fact]
    public void RealTimeSignal_PostedMultipleTimes_AllQueued()
    {
        using var env = new TestEnv();

        var rtSig = SIGRTMIN; // First real-time signal

        env.Task.PostSignalInfo(new SigInfo { Signo = rtSig, Code = 0, Value = 1 });
        env.Task.PostSignalInfo(new SigInfo { Signo = rtSig, Code = 0, Value = 2 });
        env.Task.PostSignalInfo(new SigInfo { Signo = rtSig, Code = 0, Value = 3 });

        var count = 0;
        env.Task.PendingSignalQueue.Lock(q =>
        {
            foreach (var s in q)
                if (s.Signo == rtSig)
                    count++;
        });

        Assert.Equal(3, count); // All three copies must be queued
    }

    [Fact]
    public void RealTimeSignal_DequeuedInOrder()
    {
        using var env = new TestEnv();

        var rtSig = SIGRTMIN + 1;

        env.Task.PostSignalInfo(new SigInfo { Signo = rtSig, Code = 0, Value = 10 });
        env.Task.PostSignalInfo(new SigInfo { Signo = rtSig, Code = 0, Value = 20 });
        env.Task.PostSignalInfo(new SigInfo { Signo = rtSig, Code = 0, Value = 30 });

        SigInfo? s1, s2, s3;
        lock (env.Task)
        {
            s1 = env.Task.DequeueSignalUnsafe(rtSig);
            s2 = env.Task.DequeueSignalUnsafe(rtSig);
            s3 = env.Task.DequeueSignalUnsafe(rtSig);
        }

        Assert.NotNull(s1);
        Assert.NotNull(s2);
        Assert.NotNull(s3);
        // FIFO ordering: values must come out as 10, 20, 30
        Assert.Equal(10UL, s1!.Value.Value);
        Assert.Equal(20UL, s2!.Value.Value);
        Assert.Equal(30UL, s3!.Value.Value);
    }

    [Fact]
    public void RealTimeSignal_PendingBitmask_OnlyClearedWhenQueueEmpty()
    {
        using var env = new TestEnv();

        var rtSig = SIGRTMIN + 2;
        var bit = 1UL << (rtSig - 1);

        env.Task.PostSignalInfo(new SigInfo { Signo = rtSig, Code = 0, Value = 1 });
        env.Task.PostSignalInfo(new SigInfo { Signo = rtSig, Code = 0, Value = 2 });

        Assert.NotEqual(0UL, env.Task.PendingSignals & bit);

        // First dequeue: bit should remain set (another item still in queue)
        lock (env.Task)
        {
            env.Task.DequeueSignalUnsafe(rtSig);
        }

        Assert.NotEqual(0UL, env.Task.PendingSignals & bit);

        // Second dequeue clears bit
        lock (env.Task)
        {
            env.Task.DequeueSignalUnsafe(rtSig);
        }

        Assert.Equal(0UL, env.Task.PendingSignals & bit);
    }

    // ─── Mixed standard + RT ──────────────────────────────────────────────────

    [Fact]
    public void MixedSignals_DoNotInterfereWithEachOther()
    {
        using var env = new TestEnv();

        var rtSig = SIGRTMIN;

        env.Task.PostSignal((int)Signal.SIGUSR1);
        env.Task.PostSignal((int)Signal.SIGUSR1); // Duplicate std signal
        env.Task.PostSignalInfo(new SigInfo { Signo = rtSig, Code = 0, Value = 100 });
        env.Task.PostSignalInfo(new SigInfo { Signo = rtSig, Code = 0, Value = 200 });

        int stdCount = 0, rtCount = 0;
        env.Task.PendingSignalQueue.Lock(q =>
        {
            foreach (var s in q)
            {
                if (s.Signo == (int)Signal.SIGUSR1) stdCount++;
                if (s.Signo == rtSig) rtCount++;
            }
        });

        Assert.Equal(1, stdCount); // Standard signal saturated
        Assert.Equal(2, rtCount); // RT signals both queued
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Scheduler = new KernelScheduler();
            KernelScheduler.Current = Scheduler;
            Engine = new Engine();
            Process = new Process(100, new VMAManager(), null!);
            Task = new FiberTask(100, Process, Engine, Scheduler);
        }

        public KernelScheduler Scheduler { get; }
        public FiberTask Task { get; }
        public Process Process { get; }
        public Engine Engine { get; }

        public void Dispose()
        {
            KernelScheduler.Current = null;
        }
    }
}