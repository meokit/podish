using System.Runtime.CompilerServices;

namespace Fiberish.Core;

/// <summary>
///     A lightweight async coordination primitive owned by a single KernelScheduler.
///     Allows tasks (FiberTasks) to await events like I/O, timers, or process exit.
///     Replaces standard WaitHandle/ManualResetEvent for our cooperative scheduler.
/// </summary>
public class AsyncWaitQueue
{
    private readonly List<WaiterEntry> _drainBuffer = new();
    private readonly object _gate = new();
    private readonly List<WaiterEntry> _waiters = new();

    private bool _isSignaled;
    private long _nextWaiterId;
    private KernelScheduler? _ownerScheduler;

    public AsyncWaitQueue(KernelScheduler ownerScheduler)
    {
        _ownerScheduler = ownerScheduler;
    }

    public bool IsSignaled
    {
        get
        {
            AssertSchedulerThread();
            lock (_gate)
            {
                return _isSignaled;
            }
        }
    }

    public void Signal()
    {
        AssertSchedulerThread();
        List<WaiterEntry>? toWake = null;
        lock (_gate)
        {
            if (_isSignaled) return;
            _isSignaled = true;
            if (_waiters.Count == 0) return;

            toWake = _drainBuffer;
            toWake.Clear();
            toWake.AddRange(_waiters);
            _waiters.Clear();
        }

        foreach (var waiter in toWake)
            waiter.Schedule();

        toWake.Clear();
    }

    public void Reset()
    {
        AssertSchedulerThread();
        lock (_gate)
        {
            _isSignaled = false;
            _waiters.Clear();
        }
    }

    internal void ResetForReuse(KernelScheduler ownerScheduler)
    {
        ownerScheduler.AssertSchedulerThread();
        lock (_gate)
        {
            _ownerScheduler = ownerScheduler;
            _isSignaled = false;
            _waiters.Clear();
        }
    }

    public WaitQueueAwaitable WaitAsync(FiberTask currentTask)
    {
        return new WaitQueueAwaitable(this, currentTask, false);
    }

    public WaitQueueAwaitable WaitInterruptiblyAsync(FiberTask currentTask)
    {
        return new WaitQueueAwaitable(this, currentTask, true);
    }

    // For compatibility with previous WaitHandle usage
    public void Set()
    {
        Signal();
    }

    public void Register(Action continuation, FiberTask context, FiberTask.WaitToken? token = null)
    {
        RegisterCancelable(continuation, context, token, context.CommonKernel);
    }

    public void Register(Action continuation, KernelScheduler scheduler)
    {
        RegisterCancelable(continuation, null, null, scheduler);
    }

    public IDisposable? RegisterCancelable(Action continuation, FiberTask context,
        FiberTask.WaitToken? token = null)
    {
        return RegisterCancelable(continuation, context, token, context.CommonKernel);
    }

    public IDisposable? RegisterCancelable(Action continuation, KernelScheduler scheduler)
    {
        return RegisterCancelable(continuation, null, null, scheduler);
    }

    private IDisposable? RegisterCancelable(Action continuation, FiberTask? context, FiberTask.WaitToken? token,
        KernelScheduler scheduler)
    {
        AssertSchedulerThread(scheduler);
        lock (_gate)
        {
            AssertSchedulerOwnership(scheduler);

            if (_isSignaled)
            {
                ScheduleContinuationWithWaitReason(scheduler, continuation, context, token);
                return NoopRegistration.Instance;
            }

            var id = ++_nextWaiterId;
            _waiters.Add(new WaiterEntry(id, continuation, context, token, scheduler));
            return new WaitRegistration(this, id);
        }
    }

    private static void ScheduleContinuationWithWaitReason(
        KernelScheduler scheduler,
        Action continuation,
        FiberTask? context,
        FiberTask.WaitToken? token)
    {
        if (context == null || token == null || token.Value.Owner == null)
        {
            scheduler.Schedule(continuation, context);
            return;
        }

        scheduler.ScheduleWaitContinuation(context, token.Value, WakeReason.Event, continuation);
    }

    private void Unregister(long id)
    {
        AssertSchedulerThread();
        lock (_gate)
        {
            var idx = _waiters.FindIndex(x => x.Id == id);
            if (idx >= 0) _waiters.RemoveAt(idx);
        }
    }

    private void AssertSchedulerThread(KernelScheduler? schedulerHint = null, [CallerMemberName] string? caller = null)
    {
        var scheduler = schedulerHint ?? _ownerScheduler;
        if (scheduler == null) return;
        scheduler.AssertSchedulerThread(caller);
    }

    private void AssertSchedulerOwnership(KernelScheduler schedulerHint)
    {
        if (_ownerScheduler == null)
            _ownerScheduler = schedulerHint;
        else if (!ReferenceEquals(_ownerScheduler, schedulerHint))
            throw new InvalidOperationException(
                "AsyncWaitQueue is bound to a different KernelScheduler instance.");
    }

    // Support for GetAwaiter directly on the instance (like Task)
    private sealed class NoopRegistration : IDisposable
    {
        public static readonly NoopRegistration Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class WaitRegistration : IDisposable
    {
        private readonly long _id;
        private AsyncWaitQueue? _owner;

        public WaitRegistration(AsyncWaitQueue owner, long id)
        {
            _owner = owner;
            _id = id;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Unregister(_id);
        }
    }

    private readonly struct WaiterEntry
    {
        public WaiterEntry(long id, Action continuation, FiberTask? context, FiberTask.WaitToken? token,
            KernelScheduler scheduler)
        {
            Id = id;
            Continuation = continuation;
            Context = context;
            Token = token;
            Scheduler = scheduler;
        }

        public long Id { get; }
        public Action Continuation { get; }
        public FiberTask? Context { get; }
        public FiberTask.WaitToken? Token { get; }
        public KernelScheduler Scheduler { get; }

        public void Schedule()
        {
            ScheduleContinuationWithWaitReason(Scheduler, Continuation, Context, Token);
        }
    }
}

public readonly struct WaitQueueAwaitable
{
    private readonly AsyncWaitQueue _queue;
    private readonly FiberTask _currentTask;
    private readonly bool _interruptOnSignals;

    public WaitQueueAwaitable(AsyncWaitQueue queue, FiberTask currentTask, bool interruptOnSignals)
    {
        _queue = queue;
        _currentTask = currentTask;
        _interruptOnSignals = interruptOnSignals;
    }

    public WaitQueueAwaiter GetAwaiter()
    {
        return new WaitQueueAwaiter(_queue, _currentTask, _interruptOnSignals);
    }
}

public struct WaitQueueAwaiter : INotifyCompletion
{
    private readonly AsyncWaitQueue _queue;
    private readonly FiberTask _currentTask;
    private readonly bool _interruptOnSignals;
    private FiberTask.WaitToken? _token;

    public WaitQueueAwaiter(AsyncWaitQueue queue, FiberTask currentTask, bool interruptOnSignals)
    {
        _queue = queue;
        _currentTask = currentTask;
        _interruptOnSignals = interruptOnSignals;
    }

    public bool IsCompleted => _queue.IsSignaled;

    public void OnCompleted(Action continuation)
    {
        var currentTask = _currentTask;
        _token = currentTask.BeginWaitToken();
        if (!currentTask.TryEnterAsyncOperation(_token, out var operation) || operation == null)
            return;

        var state = new QueueWaitOperation(currentTask, continuation, operation);
        state.TryRegister(_queue.RegisterCancelable(state.OnQueueSignaled, currentTask, _token));
        if (_interruptOnSignals)
            currentTask.ArmInterruptingSignalSafetyNet(_token, state.OnSignal);
        else
            currentTask.ArmSignalSafetyNet(_token, state.OnSignal);
    }

    public AwaitResult GetResult()
    {
        var task = _currentTask;
        if (_token == null) return AwaitResult.Completed;

        var reason = task.CompleteWaitToken(_token);
        if (reason != WakeReason.Event && reason != WakeReason.None) return AwaitResult.Interrupted;
        return AwaitResult.Completed;
    }

    private sealed class QueueWaitOperation
    {
        private readonly TaskAsyncOperationHandle _operation;

        public QueueWaitOperation(FiberTask task, Action continuation, TaskAsyncOperationHandle operation)
        {
            _operation = operation;
            _operation.TryInitialize(continuation, WaitContinuationMode.ResumeTask);
        }

        public void TryRegister(IDisposable? registration)
        {
            _operation.TryAddRegistration(TaskAsyncRegistration.From(registration));
        }

        public void OnQueueSignaled()
        {
            _operation.TryComplete(WakeReason.Event);
        }

        public void OnSignal()
        {
            _operation.TryComplete(WakeReason.Signal);
        }
    }
}

public static class SchedulerUtils
{
    public static SelectAwaitable WaitAny(FiberTask currentTask, params AsyncWaitQueue[] queues)
    {
        return new SelectAwaitable(queues, currentTask);
    }
}

public readonly struct SelectAwaitable
{
    private readonly AsyncWaitQueue[] _queues;
    private readonly FiberTask _currentTask;

    public SelectAwaitable(AsyncWaitQueue[] queues, FiberTask currentTask)
    {
        _queues = queues;
        _currentTask = currentTask;
    }

    public SelectAwaiter GetAwaiter()
    {
        return new SelectAwaiter(_queues, _currentTask);
    }
}

public struct SelectAwaiter : INotifyCompletion
{
    private readonly AsyncWaitQueue[] _queues;
    private readonly FiberTask _currentTask;
    private FiberTask.WaitToken? _token;

    public SelectAwaiter(AsyncWaitQueue[] queues, FiberTask currentTask)
    {
        _queues = queues;
        _currentTask = currentTask;
    }

    public bool IsCompleted
    {
        get
        {
            foreach (var q in _queues)
                if (q.IsSignaled)
                    return true;
            return false;
        }
    }

    public void OnCompleted(Action continuation)
    {
        var currentTask = _currentTask;
        _token = currentTask.BeginWaitToken();
        if (!currentTask.TryEnterAsyncOperation(_token, out var operation) || operation == null)
            return;

        var state = new SelectWaitOperation(currentTask, continuation, operation);
        foreach (var q in _queues)
            state.TryRegister(q.RegisterCancelable(state.OnQueueSignaled, currentTask, _token));
        currentTask.ArmSignalSafetyNet(_token, state.OnSignal);
    }

    public AwaitResult GetResult()
    {
        var task = _currentTask;
        if (_token == null) return AwaitResult.Completed;
        var reason = task.CompleteWaitToken(_token);
        if (reason != WakeReason.Event && reason != WakeReason.None) return AwaitResult.Interrupted;
        return AwaitResult.Completed;
    }

    private sealed class SelectWaitOperation
    {
        private readonly TaskAsyncOperationHandle _operation;

        public SelectWaitOperation(FiberTask task, Action continuation, TaskAsyncOperationHandle operation)
        {
            _operation = operation;
            _operation.TryInitialize(continuation, WaitContinuationMode.ResumeTask);
        }

        public void TryRegister(IDisposable? registration)
        {
            _operation.TryAddRegistration(TaskAsyncRegistration.From(registration));
        }

        public void OnQueueSignaled()
        {
            _operation.TryComplete(WakeReason.Event);
        }

        public void OnSignal()
        {
            _operation.TryComplete(WakeReason.Signal);
        }
    }
}

/// <summary>
///     Awaitable for waiting on child process state changes.
///     Used by wait4() to avoid busy-polling.
/// </summary>
public readonly struct ChildStateAwaitable
{
    private readonly Process _parent;
    private readonly int _targetPid; // -1=any, 0=same pgid, >0=specific, <-1=pgid
    private readonly bool _wantStopped;
    private readonly bool _wantContinued;
    private readonly KernelScheduler _scheduler;
    private readonly FiberTask _task;

    public ChildStateAwaitable(Process parent, FiberTask task, int targetPid, bool wantStopped = true,
        bool wantContinued = true)
    {
        _parent = parent;
        _targetPid = targetPid;
        _wantStopped = wantStopped;
        _wantContinued = wantContinued;
        _scheduler = task.CommonKernel;
        _task = task;
    }

    public ChildStateAwaiter GetAwaiter()
    {
        return new ChildStateAwaiter(_parent, _targetPid, _wantStopped, _wantContinued, _scheduler, _task);
    }

    public struct ChildStateAwaiter : INotifyCompletion
    {
        private readonly Process _parent;
        private readonly int _targetPid;
        private readonly bool _wantStopped;
        private readonly bool _wantContinued;
        private readonly KernelScheduler _scheduler;
        private readonly FiberTask _task;
        private FiberTask.WaitToken? _token;

        public ChildStateAwaiter(Process parent, int targetPid, bool wantStopped, bool wantContinued,
            KernelScheduler scheduler, FiberTask task)
        {
            _parent = parent;
            _targetPid = targetPid;
            _wantStopped = wantStopped;
            _wantContinued = wantContinued;
            _scheduler = scheduler;
            _task = task;
        }

        public bool IsCompleted
        {
            get
            {
                // Check if any matching child already has a waitable state
                foreach (var childPid in _parent.Children)
                {
                    var childProc = _scheduler.GetProcess(childPid);
                    if (childProc == null) continue;
                    if (!MatchesTarget(childPid, childProc)) continue;
                    if (childProc.State == ProcessState.Zombie ||
                        (_wantStopped && childProc.HasWaitableStop) ||
                        (_wantContinued && childProc.HasWaitableContinue))
                        return true;
                }

                return false;
            }
        }

        public void OnCompleted(Action continuation)
        {
            _token = _task.BeginWaitToken();
            if (!_task.TryEnterAsyncOperation(_token, out var operation) || operation == null)
                return;

            var state = new ChildStateOperation(_task, continuation, operation);

            // Register on all matching children's StateChangeEvent queues
            // Only register on events that are NOT yet signaled to avoid duplicate scheduling
            var registered = false;

            foreach (var childPid in _parent.Children)
            {
                var childProc = _scheduler.GetProcess(childPid);
                if (childProc == null) continue;
                if (!MatchesTarget(childPid, childProc)) continue;

                // Child state notifications are edge-triggered in wait* semantics.
                // Clear stale sticky state so we can register for the next transition.
                if (childProc.StateChangeEvent.IsSignaled) childProc.StateChangeEvent.Reset();
                state.TryRegister(childProc.StateChangeEvent.RegisterCancelable(state.OnChildStateChanged, _scheduler));
                registered = true;
            }

            // If no registrations happened (all events already signaled), schedule immediately
            if (!registered)
            {
                state.OnChildStateChanged();
                return; // already scheduled, no need for safety net
            }

            // wait4/waitid should only be interrupted by signals that would actually
            // break the wait. Default-ignored signals like SIGCHLD/SIGWINCH should
            // not wake the wait just to force a rescan.
            _task.ArmInterruptingSignalSafetyNet(_token, state.OnSignal);
        }

        public AwaitResult GetResult()
        {
            if (_token == null) return AwaitResult.Completed;
            var reason = _task.CompleteWaitToken(_token);
            if (reason != WakeReason.Event && reason != WakeReason.None) return AwaitResult.Interrupted;

            return AwaitResult.Completed;
        }

        private bool MatchesTarget(int childPid, Process childProc)
        {
            if (_targetPid == -1) return true;
            if (_targetPid > 0) return childPid == _targetPid;
            if (_targetPid == 0) return childProc.PGID == _parent.PGID;
            return childProc.PGID == -_targetPid;
        }

        private sealed class ChildStateOperation
        {
            private readonly TaskAsyncOperationHandle _operation;

            public ChildStateOperation(FiberTask task, Action continuation, TaskAsyncOperationHandle operation)
            {
                _operation = operation;
                _operation.TryInitialize(continuation, WaitContinuationMode.RunAction);
            }

            public void TryRegister(IDisposable? registration)
            {
                _operation.TryAddRegistration(TaskAsyncRegistration.From(registration));
            }

            public void OnChildStateChanged()
            {
                _operation.TryComplete(WakeReason.Event);
            }

            public void OnSignal()
            {
                _operation.TryComplete(WakeReason.Signal);
            }
        }
    }
}
