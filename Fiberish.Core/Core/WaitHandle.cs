using System.Runtime.CompilerServices;

namespace Fiberish.Core;

/// <summary>
///     A lightweight async coordination primitive owned by a single KernelScheduler.
///     Allows tasks (FiberTasks) to await events like I/O, timers, or process exit.
///     Replaces standard WaitHandle/ManualResetEvent for our cooperative scheduler.
/// </summary>
public class AsyncWaitQueue
{
    // List of continuations to resume when Signaled.
    // Each entry stores the continuation Action, FiberTask context, and the KernelScheduler reference.
    // Signal/Reset/Register are scheduler-thread-only; callers must hop to the owner scheduler first.
    private readonly List<(long Id, Action Continuation, FiberTask? Context, FiberTask.WaitToken? Token, KernelScheduler
            Scheduler)>
        _waiters = new();

    private bool _isSignaled;
    private long _nextWaiterId;
    private KernelScheduler? _ownerScheduler;

    public bool IsSignaled
    {
        get
        {
            AssertSchedulerThread();
            return _isSignaled;
        }
    }

    public void Signal()
    {
        AssertSchedulerThread();

        List<(long Id, Action Continuation, FiberTask? Context, FiberTask.WaitToken? Token, KernelScheduler Scheduler)>
            toWake;
        if (_isSignaled) return;
        _isSignaled = true;
        if (_waiters.Count == 0) return;
        toWake =
            new List<(long Id, Action Continuation, FiberTask? Context, FiberTask.WaitToken? Token, KernelScheduler
                Scheduler)>(
                _waiters);
        _waiters.Clear();

        // Resume all waiters after queue state has been updated.
        // Use the captured scheduler reference (not (KernelScheduler?)null) for explicit ownership.
        foreach (var (_, continuation, context, token, scheduler) in toWake)
            ScheduleContinuationWithWaitReason(scheduler, continuation, context, token);
    }

    public void Reset()
    {
        AssertSchedulerThread();
        _isSignaled = false;
        _waiters.Clear();
    }

    public WaitQueueAwaitable WaitAsync(FiberTask currentTask)
    {
        return new WaitQueueAwaitable(this, currentTask);
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
        var effectiveContext = context;
        var effectiveScheduler = scheduler;
        AssertSchedulerThread(effectiveScheduler);

        if (_isSignaled)
        {
            // If already signaled, schedule immediately
            ScheduleContinuationWithWaitReason(effectiveScheduler, continuation, effectiveContext, token);
            return NoopRegistration.Instance;
        }

        var id = ++_nextWaiterId;
        _waiters.Add((id, continuation, effectiveContext, token, effectiveScheduler));
        return new WaitRegistration(this, id);
    }

    private static void ScheduleContinuationWithWaitReason(
        KernelScheduler scheduler,
        Action continuation,
        FiberTask? context,
        FiberTask.WaitToken? token)
    {
        if (context == null || token == null)
        {
            scheduler.Schedule(continuation, context);
            return;
        }

        // Fast-path for already-signaled queues on scheduler thread:
        // publish Event wake-reason synchronously so signal safety-net cannot
        // race and overwrite the completion into Interrupted.
        if (scheduler.IsSchedulerThread)
        {
            context.TrySetWaitReason(token, WakeReason.Event);
            scheduler.Schedule(continuation, context);
            return;
        }

        scheduler.Schedule(() =>
        {
            context.TrySetWaitReason(token, WakeReason.Event);
            continuation();
        }, context);
    }

    private void Unregister(long id)
    {
        AssertSchedulerThread();
        var idx = _waiters.FindIndex(x => x.Id == id);
        if (idx >= 0) _waiters.RemoveAt(idx);
    }

    private void AssertSchedulerThread(KernelScheduler? schedulerHint = null, [CallerMemberName] string? caller = null)
    {
        var scheduler = schedulerHint ?? _ownerScheduler;
        if (scheduler == null) return;

        if (_ownerScheduler == null)
            _ownerScheduler = scheduler;
        else if (!ReferenceEquals(_ownerScheduler, scheduler))
            throw new InvalidOperationException(
                $"AsyncWaitQueue.{caller ?? "<unknown>"} is bound to a different KernelScheduler instance.");

        scheduler.AssertSchedulerThread(caller);
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
}

public readonly struct WaitQueueAwaitable
{
    private readonly AsyncWaitQueue _queue;
    private readonly FiberTask _currentTask;

    public WaitQueueAwaitable(AsyncWaitQueue queue, FiberTask currentTask)
    {
        _queue = queue;
        _currentTask = currentTask;
    }

    public WaitQueueAwaiter GetAwaiter()
    {
        return new WaitQueueAwaiter(_queue, _currentTask);
    }
}

public struct WaitQueueAwaiter : INotifyCompletion
{
    private readonly AsyncWaitQueue _queue;
    private readonly FiberTask _currentTask;
    private FiberTask.WaitToken? _token;

    public WaitQueueAwaiter(AsyncWaitQueue queue, FiberTask currentTask)
    {
        _queue = queue;
        _currentTask = currentTask;
    }

    public bool IsCompleted => _queue.IsSignaled;

    public void OnCompleted(Action continuation)
    {
        var currentTask = _currentTask;
        _token = currentTask.BeginWaitToken();
        var called = 0;

        void RunOnce()
        {
            if (Interlocked.Exchange(ref called, 1) != 0) return;
            continuation();
        }

        _queue.Register(RunOnce, currentTask, _token);
        currentTask.ArmSignalSafetyNet(_token, () =>
        {
            if (Interlocked.Exchange(ref called, 1) != 0) return;
            currentTask.Continuation = continuation;
            currentTask.CommonKernel.Schedule(currentTask);
        });
    }

    public AwaitResult GetResult()
    {
        var task = _currentTask;
        if (_token == null) return AwaitResult.Completed;

        var reason = task.CompleteWaitToken(_token);
        if (reason != WakeReason.Event && reason != WakeReason.None) return AwaitResult.Interrupted;
        return AwaitResult.Completed;
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
        var runOnce = new RunOnceAction(continuation);
        var action = runOnce.Invoke;
        _token = currentTask.BeginWaitToken();
        foreach (var q in _queues) q.Register(action, currentTask, _token);
        currentTask.ArmSignalSafetyNet(_token, () => runOnce.Invoke());
    }

    public AwaitResult GetResult()
    {
        var task = _currentTask;
        if (_token == null) return AwaitResult.Completed;
        var reason = task.CompleteWaitToken(_token);
        if (reason != WakeReason.Event && reason != WakeReason.None) return AwaitResult.Interrupted;
        return AwaitResult.Completed;
    }

    private sealed class RunOnceAction(Action action)
    {
        private int _called;

        public void Invoke()
        {
            if (Interlocked.Exchange(ref _called, 1) == 0) action();
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

            // Register on all matching children's StateChangeEvent queues
            // Only register on events that are NOT yet signaled to avoid duplicate scheduling
            var runOnce = new RunOnceAction(continuation);
            var registered = false;

            foreach (var childPid in _parent.Children)
            {
                var childProc = _scheduler.GetProcess(childPid);
                if (childProc == null) continue;
                if (!MatchesTarget(childPid, childProc)) continue;

                // Child state notifications are edge-triggered in wait* semantics.
                // Clear stale sticky state so we can register for the next transition.
                if (childProc.StateChangeEvent.IsSignaled) childProc.StateChangeEvent.Reset();
                childProc.StateChangeEvent.Register(runOnce.Invoke, _task, _token);
                registered = true;
            }

            // If no registrations happened (all events already signaled), schedule immediately
            if (!registered)
            {
                if (_token != null) _task.TrySetWaitReason(_token, WakeReason.Event);
                _scheduler.Schedule(continuation, _task);
                return; // already scheduled, no need for safety net
            }

            // ArmSignalSafetyNet: re-check for signals that arrived before BeginWaitToken.
            _task.ArmSignalSafetyNet(_token, () => runOnce.Invoke());
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

        private class RunOnceAction(Action action)
        {
            private int _called;

            public void Invoke()
            {
                if (Interlocked.Exchange(ref _called, 1) == 0) action();
            }
        }
    }
}
