using System.Collections.Concurrent;

namespace Bifrost.Core;

public class Waiter
{
    public TaskCompletionSource<bool> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public class FutexManager
{
    private readonly object _lock = new();
    private readonly Dictionary<uint, List<Waiter>> _queues = new();

    public Waiter PrepareWait(uint addr)
    {
        lock (_lock)
        {
            if (!_queues.TryGetValue(addr, out var list))
            {
                list = new List<Waiter>();
                _queues[addr] = list;
            }
            var w = new Waiter();
            list.Add(w);
            return w;
        }
    }

    public void CancelWait(uint addr, Waiter w)
    {
        lock (_lock)
        {
            if (_queues.TryGetValue(addr, out var list))
            {
                list.Remove(w);
                if (list.Count == 0)
                {
                    _queues.Remove(addr);
                }
            }
        }
    }

    public int Wake(uint addr, int count)
    {
        lock (_lock)
        {
            if (!_queues.TryGetValue(addr, out var list) || list.Count == 0)
            {
                return 0;
            }

            int woken = 0;
            while (count > 0 && list.Count > 0)
            {
                var w = list[0];
                list.RemoveAt(0);
                w.Tcs.TrySetResult(true);
                woken++;
                count--;
            }

            if (list.Count == 0)
            {
                _queues.Remove(addr);
            }

            return woken;
        }
    }
}
