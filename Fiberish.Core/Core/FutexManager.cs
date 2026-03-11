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
}