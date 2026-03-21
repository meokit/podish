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
    private bool _enableSignalSafetyNet;
    private volatile bool _isCompleted;
    private KernelScheduler? _scheduler;
    private FiberTask? _task;
    private FiberTask.WaitToken? _waitToken;

    public SaeaOperation()
    {
        Completed += OnCompletedEvent;
    }

    public bool IsCompleted => _isCompleted;

    public void OnCompleted(Action continuation)
    {
        if (_task == null && _scheduler == null) 
            throw new InvalidOperationException("SaeaOperation requires BeginWait to be called with a FiberTask before awaiting.");
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
            _task.ArmInterruptingSignalSafetyNet(_waitToken, OnSignalSafetyNet);
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
                    if (_waitToken != null)
                        _scheduler.ScheduleWaitContinuation(task, _waitToken.Value, WakeReason.IO, c);
                    else
                        _scheduler.Schedule(c, task);
                }
                else
                    _scheduler.Schedule(c);

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
    ///     Must be called immediately after ResetState() and before the async socket call,
    ///     so that signals can interrupt this wait via <see cref="FiberTask.SetWaitContinuation" />.
    /// </summary>
    public void BeginWait(FiberTask task, bool enableSignalSafetyNet = true)
    {
        _task = task;
        _scheduler = task.CommonKernel;
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

    private void OnSignalSafetyNet()
    {
        var c = Interlocked.Exchange(ref _continuation, CompletedSentinel);
        if (c != null && !ReferenceEquals(c, CompletedSentinel) && _scheduler != null && _task != null)
            _scheduler.ScheduleContinuation(c, _task, WaitContinuationMode.RunAction);
    }
}

internal readonly struct SaeaAwaitable
{
    public SaeaAwaitable(SaeaOperation operation)
    {
        Operation = operation;
    }

    public void BeginWait(FiberTask task, bool enableSignalSafetyNet = true)
    {
        Operation.BeginWait(task, enableSignalSafetyNet);
    }

    public void ResetState()
    {
        Operation.ResetState();
    }

    public void SetBuffer(byte[]? buffer, int offset, int count)
    {
        Operation.SetBuffer(buffer, offset, count);
    }

    public SocketFlags SocketFlags
    {
        get => Operation.SocketFlags;
        set => Operation.SocketFlags = value;
    }

    public EndPoint? RemoteEndPoint
    {
        get => Operation.RemoteEndPoint;
        set => Operation.RemoteEndPoint = value;
    }

    public Socket? AcceptSocket
    {
        get => Operation.AcceptSocket;
        set => Operation.AcceptSocket = value;
    }

    public int BytesTransferred => Operation.BytesTransferred;
    public SocketError SocketError => Operation.SocketError;

    public SaeaAwaiter GetAwaiter()
    {
        return new SaeaAwaiter(Operation);
    }

    public static implicit operator SocketAsyncEventArgs(SaeaAwaitable value)
    {
        return value.Operation;
    }

    internal SaeaOperation Operation { get; }
}

internal readonly struct SaeaAwaiter : INotifyCompletion
{
    private readonly SaeaOperation _operation;

    public SaeaAwaiter(SaeaOperation operation)
    {
        _operation = operation;
    }

    public bool IsCompleted => _operation.IsCompleted;

    public void OnCompleted(Action continuation)
    {
        _operation.OnCompleted(continuation);
    }

    public void GetResult()
    {
        _operation.GetResult();
    }
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
