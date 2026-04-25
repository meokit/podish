using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using System.Reflection;
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
        info = env.Task.DequeueSignalUnsafe((int)Signal.SIGUSR1);

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
        s1 = env.Task.DequeueSignalUnsafe(rtSig);
        s2 = env.Task.DequeueSignalUnsafe(rtSig);
        s3 = env.Task.DequeueSignalUnsafe(rtSig);

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
        env.Task.DequeueSignalUnsafe(rtSig);

        Assert.NotEqual(0UL, env.Task.PendingSignals & bit);

        // Second dequeue clears bit
        env.Task.DequeueSignalUnsafe(rtSig);

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

    // ─── rt_sigtimedwait tests ────────────────────────────────────────────────

    [Fact]
    public void RtSigTimedWait_PendingSignal_ReturnsImmediately()
    {
        using var env = new TestEnv();

        // Post a signal
        env.Task.PostSignal((int)Signal.SIGUSR1);

        // The signal should be pending
        var pending = env.Task.GetVisiblePendingSignals();
        var sigusr1Bit = 1UL << ((int)Signal.SIGUSR1 - 1);
        Assert.NotEqual(0UL, pending & sigusr1Bit);
    }

    [Fact]
    public void RtSigTimedWait_MatchingSignalInSet_ReturnsSignalNumber()
    {
        using var env = new TestEnv();

        // Post a signal
        env.Task.PostSignal((int)Signal.SIGUSR1);

        // Dequeue should work
        var dequeued = env.Task.DequeueSignalUnsafe((int)Signal.SIGUSR1);
        Assert.True(dequeued.HasValue);
        Assert.Equal((int)Signal.SIGUSR1, dequeued.Value.Signo);
    }

    [Fact]
    public void RtSigTimedWait_MultipleSignals_ReturnsLowest()
    {
        using var env = new TestEnv();

        // Post multiple RT signals (they get queued)
        env.Task.PostSignalInfo(new SigInfo { Signo = SIGRTMIN + 1, Code = 0 });
        env.Task.PostSignalInfo(new SigInfo { Signo = SIGRTMIN + 2, Code = 0 });

        // First dequeue should get the lower signal number (FIFO order for RT signals)
        var dequeued1 = env.Task.DequeueSignalUnsafe(SIGRTMIN + 1);
        Assert.True(dequeued1.HasValue);
        Assert.Equal(SIGRTMIN + 1, dequeued1.Value.Signo);

        var dequeued2 = env.Task.DequeueSignalUnsafe(SIGRTMIN + 2);
        Assert.True(dequeued2.HasValue);
        Assert.Equal(SIGRTMIN + 2, dequeued2.Value.Signo);
    }

    [Fact]
    public void RtSigTimedWait_NoSignal_DequeueReturnsNull()
    {
        using var env = new TestEnv();

        // No signal posted, dequeue should return null
        var dequeued = env.Task.DequeueSignalUnsafe((int)Signal.SIGUSR1);
        Assert.False(dequeued.HasValue);
    }

    [Fact]
    public void RtSigTimedWait_SignalMask_DoesNotAffectDequeue()
    {
        using var env = new TestEnv();

        // Block SIGUSR1
        env.Task.SignalMask = 1UL << ((int)Signal.SIGUSR1 - 1);

        // Post signal
        env.Task.PostSignal((int)Signal.SIGUSR1);

        // Signal should still be pending (masked signals stay pending)
        var pending = env.Task.GetVisiblePendingSignals();
        var sigusr1Bit = 1UL << ((int)Signal.SIGUSR1 - 1);
        Assert.NotEqual(0UL, pending & sigusr1Bit);

        // Dequeue should still work (sigtimedwait explicitly dequeues)
        var dequeued = env.Task.DequeueSignalUnsafe((int)Signal.SIGUSR1);
        Assert.True(dequeued.HasValue);
    }

    [Fact]
    public void ProcessSignal_Prefers_TaskWaitingInSigTimedWaitSet()
    {
        using var env = new TestEnv();
        var waiterRuntime = new TestRuntimeFactory();
        using var waiterEngine = waiterRuntime.CreateEngine();
        var waiter = new FiberTask(101, env.Process, waiterEngine, env.Scheduler)
        {
            Status = FiberTaskStatus.Waiting
        };

        var signal = (int)Signal.SIGUSR1;
        var signalBit = 1UL << (signal - 1);
        waiter.SignalMask = signalBit;

        var token = waiter.BeginWaitToken();
        RegisterSignalWait(waiter, token, signalBit, "WaitSet");

        var waiterSignalPosted = 0;
        var leaderSignalPosted = 0;
        waiter.SignalPosted += _ => waiterSignalPosted++;
        env.Task.SignalPosted += _ => leaderSignalPosted++;

        Assert.True(env.Scheduler.SignalProcess(env.Process.TGID, signal));

        Assert.Equal(1, waiterSignalPosted);
        Assert.Equal(0, leaderSignalPosted);
    }

    [Fact]
    public void ProcessSignal_Prefers_TaskWaitingInSigSuspend()
    {
        using var env = new TestEnv();
        var waiterRuntime = new TestRuntimeFactory();
        using var waiterEngine = waiterRuntime.CreateEngine();
        var waiter = new FiberTask(101, env.Process, waiterEngine, env.Scheduler)
        {
            Status = FiberTaskStatus.Waiting
        };

        var signal = (int)Signal.SIGUSR1;
        var token = waiter.BeginWaitToken();
        RegisterSignalWait(waiter, token, 0, "Interrupting");

        var waiterSignalPosted = 0;
        var leaderSignalPosted = 0;
        waiter.SignalPosted += _ => waiterSignalPosted++;
        env.Task.SignalPosted += _ => leaderSignalPosted++;

        Assert.True(env.Scheduler.SignalProcess(env.Process.TGID, signal));

        Assert.Equal(1, waiterSignalPosted);
        Assert.Equal(0, leaderSignalPosted);
        Assert.Equal(signal, waiter.InterruptingSignal);
    }

    private static void RegisterSignalWait(FiberTask task, FiberTask.WaitToken token, ulong waitSet, string kindName)
    {
        var kindType = typeof(FiberTask).GetNestedType("SignalWaitKind", BindingFlags.NonPublic);
        Assert.NotNull(kindType);
        var method = typeof(FiberTask).GetMethod("RegisterSignalWait", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var kind = Enum.Parse(kindType!, kindName);
        method!.Invoke(task, [token, waitSet, kind]);
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly TestRuntimeFactory _runtime = new();

        public TestEnv()
        {
            Scheduler = new KernelScheduler();

            Engine = _runtime.CreateEngine();
            Process = new Process(100, _runtime.CreateAddressSpace(), null!);
            Scheduler.RegisterProcess(Process);
            Task = new FiberTask(100, Process, Engine, Scheduler);
        }

        public KernelScheduler Scheduler { get; }
        public FiberTask Task { get; }
        public Process Process { get; }
        public Engine Engine { get; }

        public void Dispose()
        {
        }
    }
}
