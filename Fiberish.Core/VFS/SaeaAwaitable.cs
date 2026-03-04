using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Fiberish.Core;
using Fiberish.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Fiberish.VFS;

internal class SaeaAwaitable : SocketAsyncEventArgs, INotifyCompletion
{
    private static readonly ILogger Logger = Logging.CreateLogger<SaeaAwaitable>();
    private static readonly Action CompletedSentinel = () => { };
    private Action? _continuation;
    private volatile bool _isCompleted;
    private KernelScheduler? _scheduler;
    private FiberTask? _task;

    public SaeaAwaitable()
    {
        Completed += OnCompletedEvent;
    }

    public bool IsCompleted => _isCompleted;

    public void OnCompleted(Action continuation)
    {
        _scheduler = KernelScheduler.Current;
        _task = _scheduler?.CurrentTask;
        Logger.LogTrace(
            "[SaeaAwaitable] OnCompleted register: task={TaskId} scheduler={HasScheduler} isCompleted={IsCompleted} bytes={Bytes} error={Error}",
            _task?.TID, _scheduler != null, _isCompleted, BytesTransferred, SocketError);

        if (_task != null && _task.HasUnblockedPendingSignal())
        {
            Logger.LogTrace("[SaeaAwaitable] OnCompleted immediate schedule due to pending signal: task={TaskId}",
                _task.TID);
            _scheduler!.Schedule(continuation, _task);
            return;
        }

        var prev = Interlocked.CompareExchange(ref _continuation, continuation, null);
        if (ReferenceEquals(prev, CompletedSentinel))
        {
            Logger.LogTrace(
                "[SaeaAwaitable] OnCompleted saw CompletedSentinel, scheduling continuation immediately: task={TaskId}",
                _task?.TID);
            _scheduler?.Schedule(continuation, _task);
        }
        else
        {
            Logger.LogTrace(
                "[SaeaAwaitable] OnCompleted stored continuation: task={TaskId} prevWasNull={PrevNull}",
                _task?.TID, prev == null);
        }
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
        _isCompleted = true;
        Logger.LogTrace(
            "[SaeaAwaitable] Completed callback: bytes={Bytes} error={Error} socketFlags=0x{Flags:X} remote={Remote}",
            BytesTransferred, SocketError, (int)SocketFlags, RemoteEndPoint?.ToString());
        var c = Interlocked.Exchange(ref _continuation, CompletedSentinel);
        if (c != null && !ReferenceEquals(c, CompletedSentinel))
        {
            if (_scheduler != null)
            {
                _task?.TrySetActiveWaitReason(WakeReason.IO);
                Logger.LogTrace(
                    "[SaeaAwaitable] Completed scheduling continuation: task={TaskId} scheduler={HasScheduler}",
                    _task?.TID, true);
                _scheduler.Schedule(c, _task);
            }
        }
        else
        {
            Logger.LogTrace(
                "[SaeaAwaitable] Completed without continuation yet (or already consumed): hasContinuation={HasCont}",
                c != null);
        }
    }

    public void ResetState()
    {
        _isCompleted = false;
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