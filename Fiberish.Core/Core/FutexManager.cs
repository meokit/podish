namespace Fiberish.Core;

public class Waiter
{
    public TaskCompletionSource<bool> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public class FutexManager
{
    // Single-container scheduler-thread ownership: futex queue state is mutated only from scheduler thread.

    // Private futex: keyed by guest virtual address (FUTEX_PRIVATE_FLAG or same-process)
    private readonly Dictionary<uint, List<Waiter>> _privateQueues = [];

    // Shared futex: keyed by host physical pointer (cross-process MAP_SHARED)
    private readonly Dictionary<nint, List<Waiter>> _sharedQueues = [];

    public Waiter PrepareWait(uint addr)
    {
        if (!_privateQueues.TryGetValue(addr, out var list))
        {
            list = [];
            _privateQueues[addr] = list;
        }

        var w = new Waiter();
        list.Add(w);
        return w;
    }

    internal ITaskAsyncRegistration CreatePrivateWaitRegistration(uint addr, Waiter waiter)
    {
        return new WaitRegistration(this, addr, waiter, isShared: false);
    }

    public void CancelWait(uint addr, Waiter w)
    {
        if (_privateQueues.TryGetValue(addr, out var list))
        {
            list.Remove(w);
            if (list.Count == 0) _privateQueues.Remove(addr);
        }
    }

    public int Wake(uint addr, int count)
    {
        if (!_privateQueues.TryGetValue(addr, out var list) || list.Count == 0) return 0;

        var woken = 0;
        while (count > 0 && list.Count > 0)
        {
            var w = list[0];
            list.RemoveAt(0);
            w.Tcs.TrySetResult(true);
            woken++;
            count--;
        }

        if (list.Count == 0) _privateQueues.Remove(addr);

        return woken;
    }

    public int GetWaiterCount(uint addr)
    {
        return _privateQueues.TryGetValue(addr, out var list) ? list.Count : 0;
    }

    public Waiter PrepareWaitShared(nint hostKey)
    {
        if (!_sharedQueues.TryGetValue(hostKey, out var list))
        {
            list = [];
            _sharedQueues[hostKey] = list;
        }

        var w = new Waiter();
        list.Add(w);
        return w;
    }

    internal ITaskAsyncRegistration CreateSharedWaitRegistration(nint hostKey, Waiter waiter)
    {
        return new WaitRegistration(this, hostKey, waiter, isShared: true);
    }

    public void CancelWaitShared(nint hostKey, Waiter w)
    {
        if (_sharedQueues.TryGetValue(hostKey, out var list))
        {
            list.Remove(w);
            if (list.Count == 0) _sharedQueues.Remove(hostKey);
        }
    }

    public int WakeShared(nint hostKey, int count)
    {
        if (!_sharedQueues.TryGetValue(hostKey, out var list) || list.Count == 0) return 0;

        var woken = 0;
        while (count > 0 && list.Count > 0)
        {
            var w = list[0];
            list.RemoveAt(0);
            w.Tcs.TrySetResult(true);
            woken++;
            count--;
        }

        if (list.Count == 0) _sharedQueues.Remove(hostKey);

        return woken;
    }

    public int GetWaiterCountShared(nint hostKey)
    {
        return _sharedQueues.TryGetValue(hostKey, out var list) ? list.Count : 0;
    }

    private sealed class WaitRegistration : ITaskAsyncRegistration
    {
        private readonly FutexManager _manager;
        private readonly bool _isShared;
        private readonly uint _privateAddr;
        private readonly nint _sharedKey;
        private Waiter? _waiter;

        public WaitRegistration(FutexManager manager, uint privateAddr, Waiter waiter, bool isShared)
        {
            _manager = manager;
            _privateAddr = privateAddr;
            _waiter = waiter;
            _isShared = isShared;
        }

        public WaitRegistration(FutexManager manager, nint sharedKey, Waiter waiter, bool isShared)
        {
            _manager = manager;
            _sharedKey = sharedKey;
            _waiter = waiter;
            _isShared = isShared;
        }

        public bool IsActive => _waiter != null;

        public void Cancel()
        {
            var waiter = Interlocked.Exchange(ref _waiter, null);
            if (waiter == null) return;

            if (_isShared)
                _manager.CancelWaitShared(_sharedKey, waiter);
            else
                _manager.CancelWait(_privateAddr, waiter);

            waiter.Tcs.TrySetResult(false);
        }

        public void Dispose()
        {
            Cancel();
        }
    }
}
