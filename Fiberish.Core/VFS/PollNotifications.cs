using Fiberish.Core;
using Fiberish.Syscalls;
using System.Runtime.ExceptionServices;

namespace Fiberish.VFS;

/// <summary>Persistent notifications, separate from the potentially consuming Inode.Poll operation.</summary>
internal interface IPollNotificationSource
{
    // Disposal must be thread-safe and cancel queued, not-yet-invoked notifications without
    // requiring the target dispatcher to run. Callbacks themselves run on the target dispatcher.
    IDisposable SubscribePoll(LinuxFile file, IReadyDispatcher dispatcher, Func<short, bool> notify,
        short interests, bool exclusive);
}

/// <summary>
/// One arbitration domain per source wait queue, not per epoll or interest mask. A callback's true
/// result acknowledges an actual waiter wakeup; merely queuing a ready item must return false.
/// Notifications from foreign threads are serialized through the target dispatchers without blocking.
/// </summary>
internal sealed class PollNotificationQueue
{
    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly Queue<Publication> _pending = new();
    private readonly Queue<Publication> _runs = new();
    private bool _pumping;

    internal IDisposable Subscribe(IReadyDispatcher dispatcher, Func<short, bool> notify, short interests,
        bool exclusive)
    {
        var subscription = new Subscription(this, dispatcher, notify, interests, exclusive);
        lock (_gate) _subscriptions.Add(subscription);
        return subscription;
    }

    internal void Signal(short events = 0)
    {
        Publication publication;
        lock (_gate)
        {
            if (_subscriptions.Count == 0) return;
            publication = new Publication(this, _subscriptions.ToArray(), events);
            _pending.Enqueue(publication);
            if (_pending.Count != 1) return;
        }
        ScheduleRun(publication);
    }

    // Trampoline continuations: cancellation and reentrant Signal must not grow the stack,
    // and a fast foreign dispatcher must not resume a publication while Post is still running.
    private void ScheduleRun(Publication publication)
    {
        lock (_gate)
        {
            _runs.Enqueue(publication);
            if (_pumping) return;
            _pumping = true;
        }

        List<Exception>? errors = null;
        while (true)
        {
            Publication next;
            lock (_gate)
            {
                if (!_runs.TryDequeue(out next!))
                {
                    _pumping = false;
                    break;
                }
            }
            try { next.Run(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        if (errors is { Count: 1 }) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors is { Count: > 1 }) throw new AggregateException(errors);
    }

    private void Complete(Publication publication)
    {
        Publication? next;
        lock (_gate)
        {
            if (!ReferenceEquals(_pending.Peek(), publication))
                throw new InvalidOperationException("Poll notifications completed out of order.");
            _pending.Dequeue();
            next = _pending.TryPeek(out var pending) ? pending : null;
        }
        if (next != null) ScheduleRun(next);
    }

    private sealed class Subscription(PollNotificationQueue owner, IReadyDispatcher dispatcher,
        Func<short, bool> notify, short interests, bool exclusive) : IDisposable
    {
        private int _disposed;
        internal Delivery? PendingDelivery;
        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        internal IReadyDispatcher Dispatcher => dispatcher;
        internal bool Exclusive => exclusive;
        internal bool Matches(short events) => Volatile.Read(ref _disposed) == 0 &&
            (events == 0 || (events & interests) != 0);
        internal bool Invoke(short events) => Volatile.Read(ref _disposed) == 0 && notify(events);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Delivery? delivery;
            lock (owner._gate)
            {
                owner._subscriptions.Remove(this);
                delivery = PendingDelivery;
                PendingDelivery = null;
            }
            // A stopped dispatcher may never execute its posted callback. Cancellation of
            // a not-yet-started delivery must resume the source, without acknowledging a wake.
            delivery?.Cancel();
        }
    }

    private sealed class Delivery(PollNotificationQueue owner, Publication publication,
        Subscription subscription, short events)
    {
        private int _state; // 0 queued, 1 invoking, 2 completed/cancelled

        internal void Invoke()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
            try { publication.Acknowledge(subscription.Invoke(events)); }
            finally
            {
                Volatile.Write(ref _state, 2);
                Finish();
            }
        }

        internal void Cancel()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0) Finish();
        }

        private void Finish()
        {
            lock (owner._gate)
            {
                if (ReferenceEquals(subscription.PendingDelivery, this))
                    subscription.PendingDelivery = null;
            }
            owner.ScheduleRun(publication);
        }
    }

    private sealed class Publication(PollNotificationQueue owner, Subscription[] subscriptions, short events)
    {
        private int _index;
        private bool _exclusivePhase;
        private int _exclusiveBudget = 1;

        internal void Run()
        {
            while (true)
            {
                if (_index == subscriptions.Length)
                {
                    if (_exclusivePhase)
                    {
                        owner.Complete(this);
                        return;
                    }
                    _exclusivePhase = true;
                    _index = 0;
                }
                var subscription = subscriptions[_index++];
                if (subscription.Exclusive != _exclusivePhase || !subscription.Matches(events) ||
                    (_exclusivePhase && _exclusiveBudget == 0)) continue;

                var scheduler = subscription.Dispatcher.Scheduler;
                if (scheduler == null || scheduler.IsSchedulerThread)
                {
                    try { Acknowledge(subscription.Invoke(events)); }
                    catch
                    {
                        // Continue the remaining listeners too, then surface the callback error.
                        owner.ScheduleRun(this);
                        throw;
                    }
                    continue;
                }

                var delivery = new Delivery(owner, this, subscription, events);
                lock (owner._gate)
                {
                    if (subscription.IsDisposed) continue;
                    subscription.PendingDelivery = delivery;
                }
                try { subscription.Dispatcher.Post(delivery.Invoke); }
                catch
                {
                    delivery.Cancel();
                    throw;
                }
                return;
            }
        }

        internal void Acknowledge(bool wokeWaiter)
        {
            // Match ep_poll_callback: simultaneous IN and OUT is not a wake-one acknowledgement.
            var direction = events & (PollEvents.POLLIN | PollEvents.POLLOUT);
            if (_exclusivePhase && wokeWaiter && direction != (PollEvents.POLLIN | PollEvents.POLLOUT))
                _exclusiveBudget--;
        }
    }
}
