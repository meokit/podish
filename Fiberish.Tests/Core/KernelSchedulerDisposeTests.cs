using System.Reflection;
using System.Threading.Channels;
using Fiberish.Core;
using Xunit;

namespace Fiberish.Tests.Core;

public class KernelSchedulerDisposeTests
{
    [Fact]
    public void IdleSchedulers_DoNotAllocatePreciseWaitHandles()
    {
        var eventField = typeof(KernelScheduler).GetField("_preciseWakeEvent",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var timerField = typeof(KernelScheduler).GetField("_preciseTimerHandle",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        for (var i = 0; i < 200; i++)
        {
            using var scheduler = new KernelScheduler();
            scheduler.WakeUp();
            Assert.Null(eventField.GetValue(scheduler));
            Assert.Null(timerField.GetValue(scheduler));
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
            BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<int>>(scheduler);
        var waiters = typeof(KernelScheduler).GetField("_eventWaiters",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var waker = Task.Run(scheduler.WakeUp);
        Task? waiter = null;
        try
        {
            Assert.True(publishing.Wait(TimeSpan.FromSeconds(5)));
            waiter = Task.Factory.StartNew(() => wait(5000), TaskCreationOptions.LongRunning);
            Assert.True(SpinWait.SpinUntil(() => (int)waiters.GetValue(scheduler)! != 0,
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
            ((ManualResetEventSlim)typeof(KernelScheduler).GetField("_wakeEvent",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scheduler)!).Set();
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
        var waiters = typeof(KernelScheduler).GetField("_eventWaiters",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        waiters.SetValue(scheduler, 0);
        for (var i = 0; i < 1000; i++)
            enqueue();
        Assert.Equal(0, scheduler.WakeSetCount);

        waiters.SetValue(scheduler, 1);
        enqueue();
        Assert.Equal(1, scheduler.WakeSetCount);
    }

    [Fact]
    public async Task WaitForEvent_WakesWhenWorkIsPublished()
    {
        using var scheduler = new KernelScheduler();
        var wait = typeof(KernelScheduler).GetMethod("WaitForEvent",
            BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<int>>(scheduler);
        var enqueue = typeof(KernelScheduler).GetMethod("EnqueueWake",
            BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action>(scheduler);
        var waiters = typeof(KernelScheduler).GetField("_eventWaiters",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var started = new Task(() => wait(5000), TaskCreationOptions.LongRunning);
        started.Start();

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while ((int)waiters.GetValue(scheduler)! == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(1);
        Assert.NotEqual(0, (int)waiters.GetValue(scheduler)!);

        enqueue();
        await started.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Dispose_ClosesWaitHandlesAndIgnoresLateWake()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var scheduler = new KernelScheduler();
        var precise = typeof(KernelScheduler).GetMethod("TryWaitPrecise",
            BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Func<int, bool>>(scheduler);
        Assert.True(precise(1));

        var preciseEvent = (ManualResetEvent)typeof(KernelScheduler).GetField("_preciseWakeEvent",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scheduler)!;
        Assert.False(preciseEvent.SafeWaitHandle.IsClosed);

        scheduler.Dispose();
        Assert.True(preciseEvent.SafeWaitHandle.IsClosed);

        var enqueue = typeof(KernelScheduler).GetMethod("EnqueueWake",
            BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action>(scheduler);
        enqueue(); // must not throw after disposal
    }

    [Fact]
    public void FirstPreciseWait_RechecksWorkPublishedBeforeEventCreation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var scheduler = new KernelScheduler();
        scheduler.WakeUp();
        var precise = typeof(KernelScheduler).GetMethod("TryWaitPrecise",
            BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Func<int, bool>>(scheduler);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(precise(2000));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
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
