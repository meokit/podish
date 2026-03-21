using System.Runtime.CompilerServices;

namespace Fiberish.Core;

internal interface ITaskAsyncRegistration : IDisposable
{
    bool IsActive { get; }
    void Cancel();
}

internal static class TaskAsyncRegistration
{
    public static ITaskAsyncRegistration? From(object? registration)
    {
        return registration switch
        {
            null => null,
            ITaskAsyncRegistration asyncRegistration => asyncRegistration,
            Timer timer => new TimerAsyncRegistration(timer),
            IDisposable disposable => new DisposableAsyncRegistration(disposable),
            _ => null
        };
    }

    private sealed class DisposableAsyncRegistration : ITaskAsyncRegistration
    {
        private IDisposable? _registration;

        public DisposableAsyncRegistration(IDisposable registration)
        {
            _registration = registration;
        }

        public bool IsActive => _registration != null;

        public void Cancel()
        {
            Dispose();
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _registration, null)?.Dispose();
        }
    }

    private sealed class TimerAsyncRegistration : ITaskAsyncRegistration
    {
        private Timer? _timer;

        public TimerAsyncRegistration(Timer timer)
        {
            _timer = timer;
        }

        public bool IsActive => _timer is { Canceled: false };

        public void Cancel()
        {
            var timer = _timer;
            if (timer == null) return;
            timer.Cancel();
        }

        public void Dispose()
        {
            var timer = Interlocked.Exchange(ref _timer, null);
            timer?.Cancel();
        }
    }
}

internal sealed class FiberTaskAsyncScope
{
    private readonly object _gate = new();
    private readonly Dictionary<int, TaskAsyncOperationHandle> _operations = [];
    private readonly FiberTask _task;
    private int _nextOperationId;
    private AsyncScopeState _state;
    private bool _finalizeQueued;

    public FiberTaskAsyncScope(FiberTask task)
    {
        _task = task;
    }

    public bool IsClosing
    {
        get
        {
            lock (_gate)
            {
                return _state != AsyncScopeState.Open;
            }
        }
    }

    public bool TryEnter(FiberTask.WaitToken? token, out TaskAsyncOperationHandle? handle)
    {
        lock (_gate)
        {
            if (_state != AsyncScopeState.Open)
            {
                handle = null;
                return false;
            }

            var id = ++_nextOperationId;
            handle = new TaskAsyncOperationHandle(this, _task, id, token);
            _operations.Add(id, handle);
            return true;
        }
    }

    public void Close()
    {
        List<TaskAsyncOperationHandle>? snapshot = null;
        lock (_gate)
        {
            if (_state != AsyncScopeState.Open) return;
            _state = AsyncScopeState.Closing;
            if (_operations.Count > 0)
                snapshot = _operations.Values.ToList();
        }

        if (snapshot != null)
            foreach (var operation in snapshot)
                operation.CancelFromScope();

        TryQueueFinalizer();
    }

    public bool TryFinalizeTaskRetirement()
    {
        lock (_gate)
        {
            if (_state != AsyncScopeState.Closing || _operations.Count != 0)
                return false;

            _state = AsyncScopeState.Closed;
            return true;
        }
    }

    internal void OnOperationCompleted(TaskAsyncOperationHandle operation)
    {
        lock (_gate)
        {
            _operations.Remove(operation.OperationId);
        }

        TryQueueFinalizer();
    }

    internal bool IsTaskClosing()
    {
        lock (_gate)
        {
            return _state != AsyncScopeState.Open;
        }
    }

    private void TryQueueFinalizer()
    {
        lock (_gate)
        {
            if (_state != AsyncScopeState.Closing || _operations.Count != 0 || _finalizeQueued)
                return;

            _finalizeQueued = true;
        }

        _task.CommonKernel.Schedule(SchedulerWorkItem.FinalizeTaskRetirement(_task));
    }

    private enum AsyncScopeState
    {
        Open,
        Closing,
        Closed
    }
}

internal sealed class TaskAsyncOperationHandle
{
    private readonly object _gate = new();
    private readonly FiberTaskAsyncScope _scope;
    private readonly FiberTask _task;
    private readonly List<ITaskAsyncRegistration> _registrations = [];
    private Action? _continuation;
    private WaitContinuationMode _continuationMode = WaitContinuationMode.RunAction;
    private int _state;

    public TaskAsyncOperationHandle(FiberTaskAsyncScope scope, FiberTask task, int operationId,
        FiberTask.WaitToken? token)
    {
        _scope = scope;
        _task = task;
        OperationId = operationId;
        Token = token;
    }

    public int OperationId { get; }
    public FiberTask.WaitToken? Token { get; }
    public bool IsActive => Volatile.Read(ref _state) == 0;

    public bool TryInitialize(Action continuation, WaitContinuationMode continuationMode)
    {
        if (!IsActive) return false;
        _continuation = continuation;
        _continuationMode = continuationMode;
        return true;
    }

    public bool TryAddRegistration(ITaskAsyncRegistration? registration)
    {
        if (registration == null)
            return true;

        lock (_gate)
        {
            if (!IsActive || _scope.IsTaskClosing())
            {
                registration.Cancel();
                registration.Dispose();
                return false;
            }

            _registrations.Add(registration);
            return true;
        }
    }

    public bool TryComplete(WakeReason? reason)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            return false;

        var continuation = _continuation;
        CancelRegistrations();

        if (!_scope.IsTaskClosing() && continuation != null)
        {
            if (reason.HasValue && Token.HasValue)
                _task.TrySetWaitReason(Token.Value, reason.Value, scheduleStoredContinuation: false);

            if (_continuationMode == WaitContinuationMode.RunAction && _task.CommonKernel.IsSchedulerThread)
                continuation();
            else
                _task.CommonKernel.ScheduleContinuation(continuation, _task, _continuationMode);
        }

        CompleteCore();
        return true;
    }

    public bool TryCompleteWithoutScheduling()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            return false;

        CancelRegistrations();
        CompleteCore();
        return true;
    }

    public void CancelFromScope()
    {
        if (Interlocked.Exchange(ref _state, 2) != 0)
            return;

        CancelRegistrations();
        CompleteCore();
    }

    public void Cancel()
    {
        CancelFromScope();
    }

    public bool TryRun(Action action)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            return false;

        CancelRegistrations();
        if (!_scope.IsTaskClosing())
            action();
        CompleteCore();
        return true;
    }

    private void CancelRegistrations()
    {
        List<ITaskAsyncRegistration>? registrations = null;
        lock (_gate)
        {
            if (_registrations.Count == 0) return;
            registrations = _registrations.ToList();
            _registrations.Clear();
        }

        foreach (var registration in registrations)
        {
            registration.Cancel();
            registration.Dispose();
        }
    }

    private void CompleteCore()
    {
        _continuation = null;
        _scope.OnOperationCompleted(this);
    }
}
