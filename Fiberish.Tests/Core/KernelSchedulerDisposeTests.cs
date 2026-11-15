using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;
using Fiberish.Core;
using Xunit;

namespace Fiberish.Tests.Core;

public class KernelSchedulerDisposeTests
{
    private static readonly FieldInfo PreciseWaitField =
        typeof(KernelScheduler).GetField("_preciseWait", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo WakeSignalField =
        typeof(KernelScheduler).GetField("_wakeSignal", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo WaitingForWakeField =
        typeof(KernelScheduler).GetField("_waitingForWake", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public void IdleSchedulers_DoNotAllocatePreciseWaitHandles()
    {
        for (var i = 0; i < 200; i++)
        {
            using var scheduler = new KernelScheduler();
            scheduler.WakeUp();
            var preciseWait = (HighResolutionWait)PreciseWaitField.GetValue(scheduler)!;
            Assert.False(preciseWait.HasTimer);
        }
    }

    [Fact]
    public async Task WakeUp_PublishingWhileWaiterStarts_DoesNotLoseWake()
    {
        using var scheduler = new KernelScheduler();
        var field = typeof(KernelScheduler).GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var channel = (Channel<SchedulerWorkItem>)field.GetValue(scheduler)!;
        using var publishing = new ManualResetEventSlim();
        using var continuePublishing = new ManualResetEventSlim();
        field.SetValue(scheduler, new PausedChannel(channel, publishing, continuePublishing));
        var wait = typeof(KernelScheduler).GetMethod("WaitForEvent",
            BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<double, bool>>(scheduler);
        var waker = Task.Run(scheduler.WakeUp);
        Task? waiter = null;
        try
        {
            Assert.True(publishing.Wait(TimeSpan.FromSeconds(5)));
            waiter = Task.Factory.StartNew(() => wait(5000, false), TaskCreationOptions.LongRunning);
            Assert.True(SpinWait.SpinUntil(() => (int)WaitingForWakeField.GetValue(scheduler)! != 0,
                TimeSpan.FromSeconds(5)));
            continuePublishing.Set();
            await waker.WaitAsync(TimeSpan.FromSeconds(2));
            await waiter.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(channel.Reader.TryPeek(out _));
        }
        finally
        {
            continuePublishing.Set();
            // Release the waiter even when the assertion fails, before disposing its handles.
            ((AutoResetEvent)WakeSignalField.GetValue(scheduler)!).Set();
            await waker;
            if (waiter != null) await waiter;
        }
    }

    [Fact]
    public void EnqueueWake_DoesNotEnterKernelWithoutWaiter()
    {
        using var scheduler = new KernelScheduler();
        var enqueue = typeof(KernelScheduler).GetMethod("EnqueueWake",
            BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action>(scheduler);

        WaitingForWakeField.SetValue(scheduler, 0);
        for (var i = 0; i < 1000; i++)
            enqueue();
        Assert.Equal(0, scheduler.WakeSetCount);

        WaitingForWakeField.SetValue(scheduler, 1);
        enqueue();
        Assert.Equal(1, scheduler.WakeSetCount);
    }

    [Fact]
    public async Task WaitForEvent_WakesWhenWorkIsPublished()
    {
        using var scheduler = new KernelScheduler();
        var wait = typeof(KernelScheduler).GetMethod("WaitForEvent",
            BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<double, bool>>(scheduler);
        var enqueue = typeof(KernelScheduler).GetMethod("EnqueueWake",
            BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action>(scheduler);

        var started = new Task(() => wait(5000, false), TaskCreationOptions.LongRunning);
        started.Start();

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while ((int)WaitingForWakeField.GetValue(scheduler)! == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(1);
        Assert.NotEqual(0, (int)WaitingForWakeField.GetValue(scheduler)!);

        enqueue();
        await started.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Dispose_ClosesWaitHandlesAndIgnoresLateWake()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var scheduler = new KernelScheduler();
        var wakeSignal = (AutoResetEvent)WakeSignalField.GetValue(scheduler)!;
        var preciseWait = (HighResolutionWait)PreciseWaitField.GetValue(scheduler)!;
        var wakeHandle = wakeSignal.SafeWaitHandle;

        _ = preciseWait.Wait(wakeSignal, 1.0, false);
        Assert.True(preciseWait.HasTimer);
        Assert.False(wakeHandle.IsClosed);

        scheduler.Dispose();
        Assert.True(wakeHandle.IsClosed);
        Assert.False(preciseWait.HasTimer);

        var enqueue = typeof(KernelScheduler).GetMethod("EnqueueWake",
            BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action>(scheduler);
        enqueue(); // must not throw after disposal
    }

    [Fact]
    public void WaitForEvent_RechecksPublishedWorkBeforeArmingTimer()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var scheduler = new KernelScheduler();
        scheduler.WakeUp();
        var wait = typeof(KernelScheduler).GetMethod("WaitForEvent",
            BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<double, bool>>(scheduler);
        var preciseWait = (HighResolutionWait)PreciseWaitField.GetValue(scheduler)!;

        var elapsed = Stopwatch.StartNew();
        wait(2000, false);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(preciseWait.HasTimer);
    }

    private sealed class PausedChannel : Channel<SchedulerWorkItem>
    {
        public PausedChannel(Channel<SchedulerWorkItem> inner, ManualResetEventSlim publishing,
            ManualResetEventSlim continuePublishing)
        {
            Reader = inner.Reader;
            Writer = new PausedWriter(inner.Writer, publishing, continuePublishing);
        }

        private sealed class PausedWriter(ChannelWriter<SchedulerWorkItem> inner, ManualResetEventSlim publishing,
            ManualResetEventSlim continuePublishing) : ChannelWriter<SchedulerWorkItem>
        {
            public override bool TryWrite(SchedulerWorkItem item)
            {
                publishing.Set();
                if (!continuePublishing.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Timed out coordinating wake publication.");
                return inner.TryWrite(item);
            }

            public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
                inner.WaitToWriteAsync(cancellationToken);
        }
    }
}
