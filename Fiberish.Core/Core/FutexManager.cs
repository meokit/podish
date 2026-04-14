using Fiberish.Native;

namespace Fiberish.Core;

internal enum WaiterCompletionState : byte
{
    Pending,
    Completed,
    Canceled
}

internal sealed class FutexQueue : IDisposable
{
    public FutexQueue(FutexKey key)
    {
        Key = key;
        key.AcquireRef();
    }

    public FutexKey Key { get; }
    public Waiter? Head { get; private set; }
    public Waiter? Tail { get; private set; }
    public int Count { get; private set; }

    public void Enqueue(Waiter waiter)
    {
        waiter.Prev = Tail;
        waiter.Next = null;
        waiter.Queue = this;
        waiter.Key = Key;

        if (Tail != null)
            Tail.Next = waiter;
        else
            Head = waiter;

        Tail = waiter;
        Count++;
    }

    public bool Remove(Waiter waiter)
    {
        if (!ReferenceEquals(waiter.Queue, this))
            return false;

        var prev = waiter.Prev;
        var next = waiter.Next;
        if (prev != null)
            prev.Next = next;
        else
            Head = next;

        if (next != null)
            next.Prev = prev;
        else
            Tail = prev;

        waiter.Prev = null;
        waiter.Next = null;
        waiter.Queue = null;
        Count--;
        return true;
    }

    public void Dispose()
    {
        Key.ReleaseRef();
    }
}

public class Waiter
{
    private Action? _successCallback;
    private int _completionState;

    internal Waiter(FutexKey key)
    {
        Key = key;
    }

    internal FutexKey Key { get; set; }
    internal uint BitsetMask { get; init; } = LinuxConstants.FUTEX_BITSET_MATCH_ANY;
    internal FutexQueue? Queue { get; set; }
    internal Waiter? Prev { get; set; }
    internal Waiter? Next { get; set; }
    internal bool IsCompleted => Volatile.Read(ref _completionState) != (int)WaiterCompletionState.Pending;
    internal bool Result => Volatile.Read(ref _completionState) == (int)WaiterCompletionState.Completed;

    internal void BindSuccessCallback(Action callback)
    {
        var state = (WaiterCompletionState)Volatile.Read(ref _completionState);
        if (state == WaiterCompletionState.Completed)
        {
            callback();
            return;
        }

        if (state == WaiterCompletionState.Canceled)
            return;

        _successCallback = callback;
        state = (WaiterCompletionState)Volatile.Read(ref _completionState);
        if (state == WaiterCompletionState.Completed)
            Interlocked.Exchange(ref _successCallback, null)?.Invoke();
        else if (state == WaiterCompletionState.Canceled)
            _ = Interlocked.Exchange(ref _successCallback, null);
    }

    internal bool TryComplete()
    {
        if (Interlocked.CompareExchange(ref _completionState, (int)WaiterCompletionState.Completed,
                (int)WaiterCompletionState.Pending) != (int)WaiterCompletionState.Pending)
            return false;

        Interlocked.Exchange(ref _successCallback, null)?.Invoke();
        return true;
    }

    internal bool TryCancel()
    {
        if (Interlocked.CompareExchange(ref _completionState, (int)WaiterCompletionState.Canceled,
                (int)WaiterCompletionState.Pending) != (int)WaiterCompletionState.Pending)
            return false;

        _ = Interlocked.Exchange(ref _successCallback, null);
        return true;
    }
}

public class FutexManager
{
    // Single-container scheduler-thread ownership: futex queue state is mutated only from scheduler thread.

    private readonly Dictionary<FutexKey, FutexQueue> _queues = [];

    internal Waiter PrepareWait(FutexKey key, uint bitsetMask = LinuxConstants.FUTEX_BITSET_MATCH_ANY)
    {
        var queue = GetOrCreateQueue(key);
        var waiter = new Waiter(key) { BitsetMask = bitsetMask };
        queue.Enqueue(waiter);
        return waiter;
    }

    internal ITaskAsyncRegistration CreateWaitRegistration(FutexKey key, Waiter waiter)
    {
        return new WaitRegistration(this, waiter);
    }

    internal void CancelWait(FutexKey key, Waiter waiter)
    {
        CancelWait(waiter);
    }

    internal int Wake(FutexKey key, int count, uint bitsetMask = LinuxConstants.FUTEX_BITSET_MATCH_ANY)
    {
        if (count <= 0) return 0;
        if (!_queues.TryGetValue(key, out var queue) || queue.Count == 0) return 0;

        var woken = 0;
        for (var waiter = queue.Head; count > 0 && waiter != null;)
        {
            var next = waiter.Next;
            if ((waiter.BitsetMask & bitsetMask) != 0)
            {
                _ = queue.Remove(waiter);
                _ = waiter.TryComplete();
                woken++;
                count--;
            }

            waiter = next;
        }

        RemoveQueueIfEmpty(queue);
        return woken;
    }

    internal int Requeue(FutexKey sourceKey, FutexKey targetKey, int count)
    {
        if (count <= 0) return 0;
        if (sourceKey.Equals(targetKey)) return 0;
        if (!_queues.TryGetValue(sourceKey, out var sourceQueue) || sourceQueue.Count == 0) return 0;

        var targetQueue = GetOrCreateQueue(targetKey);
        var moved = 0;
        while (count > 0 && sourceQueue.Head is { } waiter)
        {
            _ = sourceQueue.Remove(waiter);
            targetQueue.Enqueue(waiter);
            moved++;
            count--;
        }

        RemoveQueueIfEmpty(sourceQueue);
        return moved;
    }

    internal int GetWaiterCount(FutexKey key)
    {
        return _queues.TryGetValue(key, out var queue) ? queue.Count : 0;
    }

    private void CancelWait(Waiter waiter)
    {
        if (waiter.Queue is { } queue)
        {
            _ = queue.Remove(waiter);
            RemoveQueueIfEmpty(queue);
        }

        _ = waiter.TryCancel();
    }

    private FutexQueue GetOrCreateQueue(FutexKey key)
    {
        if (_queues.TryGetValue(key, out var queue))
            return queue;

        queue = new FutexQueue(key);
        _queues[key] = queue;
        return queue;
    }

    private void RemoveQueueIfEmpty(FutexQueue queue)
    {
        if (queue.Count != 0)
            return;

        if (!_queues.TryGetValue(queue.Key, out var existing) || !ReferenceEquals(existing, queue))
            return;

        _queues.Remove(queue.Key);
        queue.Dispose();
    }

    private sealed class WaitRegistration : ITaskAsyncRegistration
    {
        private readonly FutexManager _manager;
        private Waiter? _waiter;

        public WaitRegistration(FutexManager manager, Waiter waiter)
        {
            _manager = manager;
            _waiter = waiter;
        }

        public bool IsActive => _waiter != null;

        public void Cancel()
        {
            var waiter = Interlocked.Exchange(ref _waiter, null);
            if (waiter == null) return;
            _manager.CancelWait(waiter);
        }

        public void Dispose()
        {
            Cancel();
        }
    }
}
