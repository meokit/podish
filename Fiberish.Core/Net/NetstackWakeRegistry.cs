using System.Collections.Concurrent;
using System.Threading;

namespace Fiberish.Core.Net;

public static class NetstackWakeRegistry
{
    private static long _nextToken = 1;
    private static readonly ConcurrentDictionary<nint, AutoResetEvent> _events = new();

    public static nint Register(AutoResetEvent ev)
    {
        var token = (nint)Interlocked.Increment(ref _nextToken);
        _events[token] = ev;
        return token;
    }

    public static void Unregister(nint token)
    {
        _events.TryRemove(token, out _);
    }

    public static void Signal(nint token)
    {
        try
        {
            if (_events.TryGetValue(token, out var ev))
            {
                ev.Set();
            }
        }
        catch
        {
            // Best effort, ignore errors
        }
    }
}
