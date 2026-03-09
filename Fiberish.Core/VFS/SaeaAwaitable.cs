using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Fiberish.Core;
using Fiberish.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Fiberish.VFS;

internal sealed class SaeaOperation : SocketAsyncEventArgs, INotifyCompletion
{
    private static readonly ILogger Logger = Logging.CreateLogger<SaeaOperation>();
    private static readonly Action CompletedSentinel = () => { };
    private Action? _continuation;
    private volatile bool _isCompleted;
    private KernelScheduler? _scheduler;
    private FiberTask? _task;
    private FiberTask.WaitToken? _waitToken;
    private bool _enableSignalSafetyNet;

    public SaeaOperation()
    {
        Completed += OnCompletedEvent;
    }

    public bool IsCompleted => _isCompleted;

    public void OnCompleted(Action continuation)
    {
        _scheduler = KernelScheduler.Current;
        if (_task == null) _task = _scheduler?.CurrentTask;
        Logger.LogTrace(
            "[SaeaAwaitable] OnCompleted register: task={TaskId} scheduler={HasScheduler} isCompleted={IsCompleted} bytes={Bytes} error={Error}",
            _task?.TID, _scheduler != null, _isCompleted, BytesTransferred, SocketError);

        var prev = Interlocked.CompareExchange(ref _continuation, continuation, null);
        if (ReferenceEquals(prev, CompletedSentinel))
        {
            Logger.LogTrace(
                "[SaeaAwaitable] OnCompleted saw CompletedSentinel, scheduling continuation immediately: task={TaskId}",
                _task?.TID);
            _scheduler?.Schedule(continuation, _task);
            return;
        }

        // ArmSignalSafetyNet: registers the continuation AND re-checks for signals that
        // arrived before BeginWait() was called — TOCTOU-safe.
        if (_enableSignalSafetyNet && _task != null && _waitToken != null)
        {
            Logger.LogTrace("[SaeaAwaitable] Arming signal safety net: task={TaskId}", _task.TID);
            _task.ArmSignalSafetyNet(_waitToken, () =>
            {
                // Atomically steal the continuation so only one path (signal or SAEA completion) wins.
                var c = Interlocked.Exchange(ref _continuation, CompletedSentinel);
                if (c != null && !ReferenceEquals(c, CompletedSentinel))
                    _scheduler?.Schedule(c, _task);
            });
        }
        else
        {
            Logger.LogTrace(
                "[SaeaAwaitable] OnCompleted stored continuation (no token): task={TaskId}",
                _task?.TID);
        }
    }

    public SaeaAwaitable GetAwaiter()
    {
        return new SaeaAwaitable(this);
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
                var task = _task;
                if (task != null)
                {
                    _scheduler.Schedule(() =>
                    {
                        task.TrySetActiveWaitReason(WakeReason.IO);
                        c();
                    }, task);
                }
                else
                {
                    _scheduler.Schedule(c, null);
                }
                Logger.LogTrace(
                    "[SaeaAwaitable] Completed scheduling continuation: task={TaskId} scheduler={HasScheduler}",
                    _task?.TID, true);
            }
        }
        else
        {
            Logger.LogTrace(
                "[SaeaAwaitable] Completed without continuation yet (or already consumed): hasContinuation={HasCont}",
                c != null);
        }
    }

    /// <summary>
    /// Must be called immediately after ResetState() and before the async socket call,
    /// so that signals can interrupt this wait via <see cref="FiberTask.SetWaitContinuation"/>.
    /// </summary>
    public void BeginWait(FiberTask task, bool enableSignalSafetyNet = true)
    {
        _task = task;
        _scheduler = KernelScheduler.Current;
        _enableSignalSafetyNet = enableSignalSafetyNet;
        if (enableSignalSafetyNet)
            _waitToken = task.BeginWaitToken();
        else
            _waitToken = null;
    }

    public void ResetState()
    {
        _isCompleted = false;
        _continuation = null;
        _scheduler = null;
        _task = null;
        _waitToken = null;
        _enableSignalSafetyNet = false;
        SetBuffer(null, 0, 0);
        AcceptSocket = null;
        RemoteEndPoint = null;
        SocketFlags = SocketFlags.None;
        UserToken = null;
    }
}

internal readonly struct SaeaAwaitable
{
    private readonly SaeaOperation _operation;

    public SaeaAwaitable(SaeaOperation operation)
    {
        _operation = operation;
    }

    public void BeginWait(FiberTask task, bool enableSignalSafetyNet = true) =>
        _operation.BeginWait(task, enableSignalSafetyNet);
    public void ResetState() => _operation.ResetState();
    public void SetBuffer(byte[]? buffer, int offset, int count) => _operation.SetBuffer(buffer, offset, count);

    public SocketFlags SocketFlags
    {
        get => _operation.SocketFlags;
        set => _operation.SocketFlags = value;
    }

    public EndPoint? RemoteEndPoint
    {
        get => _operation.RemoteEndPoint;
        set => _operation.RemoteEndPoint = value;
    }

    public Socket? AcceptSocket
    {
        get => _operation.AcceptSocket;
        set => _operation.AcceptSocket = value;
    }

    public int BytesTransferred => _operation.BytesTransferred;
    public SocketError SocketError => _operation.SocketError;

    public SaeaAwaiter GetAwaiter() => new(_operation);

    public static implicit operator SocketAsyncEventArgs(SaeaAwaitable value) => value._operation;
    internal SaeaOperation Operation => _operation;
}

internal readonly struct SaeaAwaiter : INotifyCompletion
{
    private readonly SaeaOperation _operation;

    public SaeaAwaiter(SaeaOperation operation)
    {
        _operation = operation;
    }

    public bool IsCompleted => _operation.IsCompleted;
    public void OnCompleted(Action continuation) => _operation.OnCompleted(continuation);
    public void GetResult() => _operation.GetResult();
}

internal static class SaeaPool
{
    private static readonly ConcurrentQueue<SaeaOperation> _pool = new();

    public static SaeaAwaitable Rent()
    {
        if (_pool.TryDequeue(out var item)) return new SaeaAwaitable(item);
        return new SaeaAwaitable(new SaeaOperation());
    }

    public static void Return(SaeaAwaitable item)
    {
        item.ResetState();
        _pool.Enqueue(item.Operation);
    }
}
