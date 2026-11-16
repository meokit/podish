using Fiberish.Core;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class PollNotificationTests
{
    [Fact]
    public void WarmedOwnerThreadSignals_WithPersistentSubscription_DoNotAllocate()
    {
        var queue = new PollNotificationQueue();
        var calls = 0;
        using var subscription = queue.Subscribe(new SchedulerReadyDispatcher(null),
            _ => { calls++; return true; }, PollEvents.POLLIN, false);
        for (var i = 0; i < 32; i++) queue.Signal(PollEvents.POLLIN);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 256; i++) queue.Signal(PollEvents.POLLIN);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(288, calls);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void DeferredPublication_KeepsItsOriginalSnapshot_WhenSubscriptionsChange()
    {
        var queue = new PollNotificationQueue();
        var stopped = new ManualDispatcher();
        var calls = new List<int>();
        using var first = queue.Subscribe(stopped, _ => { calls.Add(1); return false; }, PollEvents.POLLIN, false);
        using var old = queue.Subscribe(new SchedulerReadyDispatcher(null), _ => { calls.Add(2); return false; }, PollEvents.POLLIN, false);
        queue.Signal(PollEvents.POLLIN);
        old.Dispose();
        using var added = queue.Subscribe(new SchedulerReadyDispatcher(null), _ => { calls.Add(3); return false; }, PollEvents.POLLIN, false);
        queue.Signal(PollEvents.POLLIN);
        stopped.Drain();
        Assert.Equal(new[] { 1, 1, 3 }, calls);
    }

    [Fact]
    public void Signal_SkipsInactiveExclusiveAndNotifiesAllOrdinaryListeners()
    {
        var queue = new PollNotificationQueue();
        var dispatcher = new SchedulerReadyDispatcher(null);
        var calls = new List<string>();
        using var inactive = queue.Subscribe(dispatcher, _ => { calls.Add("inactive"); return false; },
            PollEvents.POLLIN, true);
        using var first = queue.Subscribe(dispatcher, _ => { calls.Add("first"); return true; },
            PollEvents.POLLIN, true);
        using var second = queue.Subscribe(dispatcher, _ => { calls.Add("second"); return true; },
            PollEvents.POLLIN, true);
        using var ordinary = queue.Subscribe(dispatcher, _ => { calls.Add("ordinary"); return false; },
            PollEvents.POLLIN, false);

        queue.Signal(PollEvents.POLLIN);
        Assert.Equal(new[] { "ordinary", "inactive", "first" }, calls);
        calls.Clear();
        queue.Signal(PollEvents.POLLIN); // Persistent, not a one-shot registration.
        Assert.Equal(new[] { "ordinary", "inactive", "first" }, calls);
        first.Dispose();
        calls.Clear();
        queue.Signal(PollEvents.POLLIN);
        Assert.Equal(new[] { "ordinary", "inactive", "second" }, calls);
    }

    [Fact]
    public void Signal_OverlappingMasksShareOneBudgetAndNonmatchingMaskDoesNotConsumeIt()
    {
        var queue = new PollNotificationQueue();
        var dispatcher = new SchedulerReadyDispatcher(null);
        var calls = new List<int>();
        using var output = queue.Subscribe(dispatcher, _ => { calls.Add(0); return true; }, PollEvents.POLLOUT, true);
        using var input = queue.Subscribe(dispatcher, _ => { calls.Add(1); return true; }, PollEvents.POLLIN, true);
        using var both = queue.Subscribe(dispatcher, _ => { calls.Add(2); return true; },
            PollEvents.POLLIN | PollEvents.POLLOUT, true);
        queue.Signal(PollEvents.POLLIN);
        Assert.Equal(new[] { 1 }, calls);
        calls.Clear();
        queue.Signal(PollEvents.POLLIN | PollEvents.POLLOUT);
        Assert.Equal(new[] { 0, 1, 2 }, calls);
    }

    [Fact]
    public void Signal_ForeignDispatcherWaitsForAcknowledgementAndSkipsDisposedSnapshotEntry()
    {
        var queue = new PollNotificationQueue();
        var dispatcher = new ManualDispatcher();
        var firstCalls = 0;
        var secondCalls = 0;
        using var first = queue.Subscribe(dispatcher, _ => { firstCalls++; return false; }, PollEvents.POLLIN, true);
        using var second = queue.Subscribe(dispatcher, _ => { secondCalls++; return true; }, PollEvents.POLLIN, true);
        queue.Signal(PollEvents.POLLIN);
        queue.Signal(PollEvents.POLLIN);
        Assert.Equal(0, firstCalls);
        Assert.Single(dispatcher.Pending); // The second publication cannot overtake the first.
        first.Dispose();
        dispatcher.Drain();
        Assert.Equal(0, firstCalls);
        Assert.Equal(2, secondCalls);
    }

    [Fact]
    public void Signal_CallbackFailureDoesNotStrandLaterPublications()
    {
        var queue = new PollNotificationQueue();
        var dispatcher = new SchedulerReadyDispatcher(null);
        using var broken = queue.Subscribe(dispatcher, _ => throw new InvalidOperationException(), PollEvents.POLLIN, false);
        Assert.Throws<InvalidOperationException>(() => queue.Signal(PollEvents.POLLIN));
        broken.Dispose();
        var calls = 0;
        using var working = queue.Subscribe(dispatcher, _ => { calls++; return false; }, PollEvents.POLLIN, false);
        queue.Signal(PollEvents.POLLIN);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dispose_QueuedDeliveryOnStoppedDispatcher_ResumesOtherListenersWithoutDrainingIt(bool exclusive)
    {
        var queue = new PollNotificationQueue();
        var stopped = new ManualDispatcher();
        var deadCalls = 0;
        var liveCalls = 0;
        using var dead = queue.Subscribe(stopped, _ => { deadCalls++; return true; }, PollEvents.POLLIN, exclusive);
        using var live = queue.Subscribe(new SchedulerReadyDispatcher(null),
            _ => { liveCalls++; return true; }, PollEvents.POLLIN, exclusive);
        queue.Signal(PollEvents.POLLIN);
        queue.Signal(PollEvents.POLLIN);
        Assert.Equal(0, liveCalls);
        dead.Dispose();
        Assert.Equal(2, liveCalls); // No callback from the stopped scheduler is needed.
        stopped.Drain(); // A stale posted callback must not advance/acknowledge a second time.
        Assert.Equal(0, deadCalls);
        Assert.Equal(2, liveCalls);
        queue.Signal(PollEvents.POLLIN);
        Assert.Equal(3, liveCalls);
    }

    [Fact]
    public void Dispose_LargeDeferredBacklog_DrainsWithoutRecursivePublicationCompletion()
    {
        const int publications = 20000;
        var queue = new PollNotificationQueue();
        var stopped = new ManualDispatcher();
        using var dead = queue.Subscribe(stopped, _ => true, PollEvents.POLLIN, true);
        var calls = 0;
        using var live = queue.Subscribe(new SchedulerReadyDispatcher(null),
            _ => { calls++; return true; }, PollEvents.POLLIN, true);
        for (var i = 0; i < publications; i++) queue.Signal(PollEvents.POLLIN);
        dead.Dispose();
        Assert.Equal(publications, calls);
        stopped.Drain();
        Assert.Equal(publications, calls);
    }

    [Fact]
    public void Signal_ReentrantPublications_DoNotGrowTheCallbackStack()
    {
        const int publications = 20000;
        var queue = new PollNotificationQueue();
        var calls = 0;
        using var subscription = queue.Subscribe(new SchedulerReadyDispatcher(null), _ =>
        {
            if (++calls < publications) queue.Signal(PollEvents.POLLIN);
            return true;
        }, PollEvents.POLLIN, true);
        queue.Signal(PollEvents.POLLIN);
        Assert.Equal(publications, calls);
    }

    [Fact]
    public void Signal_ThrowingListener_DoesNotSuppressLaterListenersOfTheSamePublication()
    {
        var queue = new PollNotificationQueue();
        var calls = 0;
        using var broken = queue.Subscribe(new SchedulerReadyDispatcher(null),
            _ => throw new InvalidOperationException("probe"), PollEvents.POLLIN, false);
        using var working = queue.Subscribe(new SchedulerReadyDispatcher(null),
            _ => { calls++; return true; }, PollEvents.POLLIN, true);
        Assert.Throws<InvalidOperationException>(() => queue.Signal(PollEvents.POLLIN));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Signal_InlineForeignPost_RecyclesOnlyAfterAcknowledgement(bool throwAfterPost)
    {
        var queue = new PollNotificationQueue();
        var dispatcher = new ManualDispatcher(inline: true, throwAfterPost: throwAfterPost);
        var foreignCalls = 0;
        var localCalls = 0;
        using var foreign = queue.Subscribe(dispatcher, _ => { foreignCalls++; return false; }, PollEvents.POLLIN, true);
        using var local = queue.Subscribe(new SchedulerReadyDispatcher(null),
            _ => { localCalls++; return true; }, PollEvents.POLLIN, true);
        for (var i = 0; i < 32; i++)
        {
            var error = Record.Exception(() => queue.Signal(PollEvents.POLLIN));
            if (throwAfterPost) Assert.IsType<InvalidOperationException>(error);
            else Assert.Null(error);
            Assert.Equal(i + 1, foreignCalls);
            Assert.Equal(i + 1, localCalls);
        }
    }

    [Fact]
    public async Task Dispose_RacingPostedDelivery_ConsumesAtMostOneWakeAndCannotStrandThePublication()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var queue = new PollNotificationQueue();
            var dispatcher = new ManualDispatcher();
            var calls = 0;
            using var first = queue.Subscribe(dispatcher,
                _ => { Interlocked.Increment(ref calls); return true; }, PollEvents.POLLIN, true);
            using var second = queue.Subscribe(new SchedulerReadyDispatcher(null),
                _ => { Interlocked.Increment(ref calls); return true; }, PollEvents.POLLIN, true);
            queue.Signal(PollEvents.POLLIN);
            var posted = dispatcher.Pending.Dequeue();
            await Task.WhenAll(Task.Run(posted), Task.Run(first.Dispose));
            Assert.Equal(1, calls);
            posted(); // Replayed ingress remains inert after either cancellation or acknowledgement.
            Assert.Equal(1, calls);
            queue.Signal(PollEvents.POLLIN);
            Assert.Equal(2, calls);
        }
    }

    private sealed class ManualDispatcher : IReadyDispatcher
    {
        private readonly bool _inline;
        private readonly bool _throwAfterPost;
        public ManualDispatcher(bool inline = false, bool throwAfterPost = false)
        {
            _inline = inline;
            _throwAfterPost = throwAfterPost;
            typeof(KernelScheduler).GetField("_isInsideRunLoop",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(Scheduler, false);
        }
        public Queue<Action> Pending { get; } = new();
        public bool CanDispatch => true;
        public FiberTask? CurrentTask => null;
        public KernelScheduler? Scheduler { get; } = new KernelScheduler();
        public void Post(Action callback)
        {
            if (_inline) callback();
            else Pending.Enqueue(callback);
            if (_throwAfterPost) throw new InvalidOperationException("Post failed after delivery");
        }
        public void Drain()
        {
            while (Pending.TryDequeue(out var callback)) callback();
        }
    }
}
