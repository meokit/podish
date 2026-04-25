using System.Runtime.CompilerServices;
using Fiberish.Diagnostics;
using Fiberish.Native;
using Microsoft.Extensions.Logging;

namespace Fiberish.Core;

internal readonly struct BlockingHostOperationAwaitable
{
    private readonly BlockingHostOperationState _state;
    private readonly FiberTask _task;

    public BlockingHostOperationAwaitable(FiberTask task, string operationName, Func<int> worker)
    {
        _task = task;
        _state = new BlockingHostOperationState(task, operationName, worker);
    }

    public BlockingHostOperationAwaiter GetAwaiter()
    {
        return new BlockingHostOperationAwaiter(_task, _state);
    }
}

internal readonly struct BlockingHostOperationAwaiter : INotifyCompletion
{
    private readonly BlockingHostOperationState _state;
    private readonly FiberTask _task;
    private readonly FiberTask.WaitToken _token;

    public BlockingHostOperationAwaiter(FiberTask task, BlockingHostOperationState state)
    {
        _task = task;
        _state = state;
        _token = task.BeginWaitToken();
    }

    public bool IsCompleted => false;

    public void OnCompleted(Action continuation)
    {
        var task = _task;
        var token = _token;
        BlockingHostOperationDebug.Trace(
            $"awaiter.OnCompleted op={_state.OperationName} tid={task.TID} token={token.Id} thread={Environment.CurrentManagedThreadId}");
        if (!task.TryEnterAsyncOperation(token, out var operation) || operation == null)
        {
            BlockingHostOperationDebug.Trace(
                $"awaiter.OnCompleted enter-failed op={_state.OperationName} tid={task.TID} token={token.Id}");
            return;
        }

        _state.Begin(continuation, operation);
        task.ArmInterruptingSignalSafetyNet(token, _state.OnSignal);
        BlockingHostOperationDebug.Trace(
            $"awaiter.OnCompleted armed op={_state.OperationName} tid={task.TID} token={token.Id}");
    }

    public int GetResult()
    {
        var reason = _task.CompleteWaitToken(_token);
        BlockingHostOperationDebug.Trace(
            $"awaiter.GetResult op={_state.OperationName} tid={_task.TID} token={_token.Id} reason={reason} result={_state.Result}");
        if (reason != WakeReason.IO && reason != WakeReason.None)
            return -(int)Errno.ERESTARTSYS;
        return _state.Result;
    }
}

internal sealed class BlockingHostOperationState
{
    private static readonly ILogger Logger = Logging.CreateLogger<BlockingHostOperationState>();

    private readonly string _operationName;
    private readonly FiberTask _task;
    private readonly Func<int> _worker;
    private TaskAsyncOperationHandle? _operation;
    private int _result = -(int)Errno.EIO;

    public BlockingHostOperationState(FiberTask task, string operationName, Func<int> worker)
    {
        _task = task;
        _operationName = operationName;
        _worker = worker;
    }

    public string OperationName => _operationName;

    public int Result => Volatile.Read(ref _result);

    public void Begin(Action continuation, TaskAsyncOperationHandle operation)
    {
        _operation = operation;
        BlockingHostOperationDebug.Trace(
            $"state.Begin op={_operationName} tid={_task.TID} operationId={operation.OperationId} thread={Environment.CurrentManagedThreadId}");
        if (!_operation.TryInitialize(continuation))
        {
            BlockingHostOperationDebug.Trace(
                $"state.Begin init-failed op={_operationName} tid={_task.TID} operationId={operation.OperationId}");
            return;
        }

        try
        {
            BlockingHostOperationDebug.Trace(
                $"state.Begin queue-worker op={_operationName} tid={_task.TID} operationId={operation.OperationId}");
            ThreadPool.UnsafeQueueUserWorkItem(static state => state.RunWorker(), this, preferLocal: false);
        }
        catch
        {
            _operation = null;
            throw;
        }
    }

    public void OnSignal()
    {
        BlockingHostOperationDebug.Trace(
            $"state.OnSignal op={_operationName} tid={_task.TID} thread={Environment.CurrentManagedThreadId}");
        _operation?.TryComplete(WakeReason.Signal);
    }

    private void RunWorker()
    {
        BlockingHostOperationDebug.Trace(
            $"worker.start op={_operationName} tid={_task.TID} thread={Environment.CurrentManagedThreadId}");
        try
        {
            Volatile.Write(ref _result, _worker());
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Blocking host operation {OperationName} failed", _operationName);
            Volatile.Write(ref _result, -(int)Errno.EIO);
        }

        BlockingHostOperationDebug.Trace(
            $"worker.complete op={_operationName} tid={_task.TID} thread={Environment.CurrentManagedThreadId} result={_result}");
        _operation?.TryComplete(WakeReason.IO);
    }
}

internal static class BlockingHostOperationDebug
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("FIBERISH_DEBUG_BLOCKING_HOST_OP") == "1";

    public static void Trace(string message)
    {
        if (!Enabled)
            return;

        Console.Error.WriteLine($"[BlockingHostOp {DateTime.UtcNow:O}] {message}");
    }
}
