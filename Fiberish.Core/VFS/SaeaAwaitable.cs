using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Fiberish.Core;

namespace Fiberish.VFS;

internal class SaeaAwaitable : SocketAsyncEventArgs, INotifyCompletion
{
    private Action? _continuation;
    private KernelScheduler? _scheduler;
    private FiberTask? _task;

    public SaeaAwaitable()
    {
        Completed += OnCompletedEvent;
    }

    public bool IsCompleted => false;

    public void OnCompleted(Action continuation)
    {
        _scheduler = KernelScheduler.Current;
        _task = _scheduler?.CurrentTask;

        if (_task != null && _task.HasUnblockedPendingSignal())
        {
            _scheduler!.Schedule(continuation, _task);
            return;
        }

        _continuation = continuation;
    }

    public SaeaAwaitable GetAwaiter()
    {
        return this;
    }

    public void GetResult()
    {
    }

    private void OnCompletedEvent(object? sender, SocketAsyncEventArgs e)
    {
        var c = _continuation;
        if (c != null)
        {
            _continuation = null;
            if (_scheduler != null)
            {
                if (_task != null) _task.WakeReason = WakeReason.IO;
                _scheduler.Schedule(c, _task);
            }
        }
    }

    public void ResetState()
    {
        _continuation = null;
        _scheduler = null;
        _task = null;
        SetBuffer(null, 0, 0);
        AcceptSocket = null;
        RemoteEndPoint = null;
        SocketFlags = SocketFlags.None;
        UserToken = null;
    }
}

internal static class SaeaPool
{
    private static readonly ConcurrentQueue<SaeaAwaitable> _pool = new();

    public static SaeaAwaitable Rent()
    {
        if (_pool.TryDequeue(out var item)) return item;
        return new SaeaAwaitable();
    }

    public static void Return(SaeaAwaitable item)
    {
        item.ResetState();
        _pool.Enqueue(item);
    }
}
