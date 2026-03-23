namespace Fiberish.Core;

public class Waiter
{
    internal Waiter(FutexKey key)
    {
        Key = key;
    }

    internal FutexKey Key { get; }
    public TaskCompletionSource<bool> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseKeyRef()
    {
        if (Interlocked.Exchange(ref _keyRefReleased, 1) != 0) return;
        Key.ReleaseRef();
    }

    private int _keyRefReleased;
}

public class FutexManager
{
    // Single-container scheduler-thread ownership: futex queue state is mutated only from scheduler thread.

    private readonly Dictionary<FutexKey, List<Waiter>> _queues = [];

    internal Waiter PrepareWait(FutexKey key)
    {
        if (!_queues.TryGetValue(key, out var list))
        {
            list = [];
            _queues[key] = list;
        }

        key.AcquireRef();
        var w = new Waiter(key);
        list.Add(w);
        return w;
    }

    internal ITaskAsyncRegistration CreateWaitRegistration(FutexKey key, Waiter waiter)
    {
        return new WaitRegistration(this, key, waiter);
    }

    internal void CancelWait(FutexKey key, Waiter waiter)
    {
        if (_queues.TryGetValue(key, out var list))
        {
            list.Remove(waiter);
            if (list.Count == 0) _queues.Remove(key);
        }

        waiter.ReleaseKeyRef();
    }

    internal int Wake(FutexKey key, int count)
    {
        if (!_queues.TryGetValue(key, out var list) || list.Count == 0) return 0;

        var woken = 0;
        while (count > 0 && list.Count > 0)
        {
            var w = list[0];
            list.RemoveAt(0);
            w.ReleaseKeyRef();
            w.Tcs.TrySetResult(true);
            woken++;
            count--;
        }

        if (list.Count == 0) _queues.Remove(key);

        return woken;
    }

    internal int GetWaiterCount(FutexKey key)
    {
        return _queues.TryGetValue(key, out var list) ? list.Count : 0;
    }

    private sealed class WaitRegistration : ITaskAsyncRegistration
    {
        private readonly FutexManager _manager;
        private readonly FutexKey _key;
        private Waiter? _waiter;

        public WaitRegistration(FutexManager manager, FutexKey key, Waiter waiter)
        {
            _manager = manager;
            _key = key;
            _waiter = waiter;
        }

        public bool IsActive => _waiter != null;

        public void Cancel()
        {
            var waiter = Interlocked.Exchange(ref _waiter, null);
            if (waiter == null) return;

            _manager.CancelWait(_key, waiter);
            waiter.Tcs.TrySetResult(false);
        }

        public void Dispose()
        {
            Cancel();
        }
    }
}
