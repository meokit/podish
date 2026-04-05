using Fiberish.Native;

namespace Fiberish.Core;

public class Waiter
{
    private int _keyRefReleased;

    internal Waiter(FutexKey key)
    {
        Key = key;
    }

    internal FutexKey Key { get; private set; }
    internal uint BitsetMask { get; init; } = LinuxConstants.FUTEX_BITSET_MATCH_ANY;
    public TaskCompletionSource<bool> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void RequeueTo(FutexKey key)
    {
        if (Key.Equals(key)) return;

        key.AcquireRef();
        var previousKey = Key;
        Key = key;
        previousKey.ReleaseRef();
    }

    public void ReleaseKeyRef()
    {
        if (Interlocked.Exchange(ref _keyRefReleased, 1) != 0) return;
        Key.ReleaseRef();
    }
}

public class FutexManager
{
    // Single-container scheduler-thread ownership: futex queue state is mutated only from scheduler thread.

    private readonly Dictionary<FutexKey, List<Waiter>> _queues = [];

    internal Waiter PrepareWait(FutexKey key, uint bitsetMask = LinuxConstants.FUTEX_BITSET_MATCH_ANY)
    {
        if (!_queues.TryGetValue(key, out var list))
        {
            list = [];
            _queues[key] = list;
        }

        key.AcquireRef();
        var w = new Waiter(key) { BitsetMask = bitsetMask };
        list.Add(w);
        return w;
    }

    internal ITaskAsyncRegistration CreateWaitRegistration(FutexKey key, Waiter waiter)
    {
        return new WaitRegistration(this, waiter);
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

    internal int Wake(FutexKey key, int count, uint bitsetMask = LinuxConstants.FUTEX_BITSET_MATCH_ANY)
    {
        if (count <= 0) return 0;
        if (!_queues.TryGetValue(key, out var list) || list.Count == 0) return 0;

        var woken = 0;
        for (var index = 0; count > 0 && index < list.Count;)
        {
            var w = list[index];
            if ((w.BitsetMask & bitsetMask) == 0)
            {
                index++;
                continue;
            }

            list.RemoveAt(index);
            w.ReleaseKeyRef();
            w.Tcs.TrySetResult(true);
            woken++;
            count--;
        }

        if (list.Count == 0) _queues.Remove(key);

        return woken;
    }

    internal int Requeue(FutexKey sourceKey, FutexKey targetKey, int count)
    {
        if (count <= 0) return 0;
        if (sourceKey.Equals(targetKey)) return 0;
        if (!_queues.TryGetValue(sourceKey, out var sourceList) || sourceList.Count == 0) return 0;

        if (!_queues.TryGetValue(targetKey, out var targetList))
        {
            targetList = [];
            _queues[targetKey] = targetList;
        }

        var moved = 0;
        while (count > 0 && sourceList.Count > 0)
        {
            var waiter = sourceList[0];
            sourceList.RemoveAt(0);
            waiter.RequeueTo(targetKey);
            targetList.Add(waiter);
            moved++;
            count--;
        }

        if (sourceList.Count == 0) _queues.Remove(sourceKey);

        return moved;
    }

    internal int GetWaiterCount(FutexKey key)
    {
        return _queues.TryGetValue(key, out var list) ? list.Count : 0;
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

            _manager.CancelWait(waiter.Key, waiter);
            waiter.Tcs.TrySetResult(false);
        }

        public void Dispose()
        {
            Cancel();
        }
    }
}