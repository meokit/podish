using System.Runtime.CompilerServices;

namespace Fiberish.Core;

/// <summary>
///     A lightweight, thread-safe async coordination primitive.
///     Allows tasks (FiberTasks) to await events like I/O, timers, or process exit.
///     Replaces standard WaitHandle/ManualResetEvent for our cooperative scheduler.
/// </summary>
public class AsyncWaitQueue
{
    private readonly object _lock = new();
    private long _nextWaiterId;

    // List of continuations to resume when Signaled.
    // Each entry stores the continuation Action, FiberTask context, and the KernelScheduler reference.
    // We capture the scheduler at registration time because Signal() may be called from a
    // background thread where KernelScheduler.Current is null (AsyncLocal doesn't flow to other threads).
    private readonly List<(long Id, Action Continuation, FiberTask? Context, FiberTask.WaitToken? Token, KernelScheduler
            Scheduler)>
        _waiters = new();

    private sealed class NoopRegistration : IDisposable
    {
        public static readonly NoopRegistration Instance = new();
        public void Dispose()
        {
        }
    }

    private sealed class WaitRegistration : IDisposable
    {
        private AsyncWaitQueue? _owner;
        private readonly long _id;

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

    public bool IsSignaled { get; private set; }

    public void Signal()
    {
        List<(long Id, Action Continuation, FiberTask? Context, FiberTask.WaitToken? Token, KernelScheduler Scheduler)>
            toWake;
        lock (_lock)
        {
            if (IsSignaled) return;
            IsSignaled = true;
            if (_waiters.Count == 0) return;
            toWake =
                new List<(long Id, Action Continuation, FiberTask? Context, FiberTask.WaitToken? Token, KernelScheduler
                        Scheduler)>(
                    _waiters);
            _waiters.Clear();
        }

        // Resume all waiters outside the lock
        // Use the captured scheduler reference (not KernelScheduler.Current) because
        // Signal() may be called from a background thread where Current is null
        foreach (var (_, continuation, context, token, scheduler) in toWake)
        {
            ScheduleContinuationWithWaitReason(scheduler, continuation, context, token);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            IsSignaled = false;
            _waiters.Clear();
        }
    }

    public WaitQueueAwaitable WaitAsync()
    {
        return new WaitQueueAwaitable(this);
    }

    // For compatibility with previous WaitHandle usage
    public void Set()
    {
        Signal();
    }

    public void Register(Action continuation, FiberTask.WaitToken? token = null)
    {
        var scheduler = KernelScheduler.Current;
        var context = scheduler?.CurrentTask;
        RegisterCancelable(continuation, context, token, scheduler);
    }

    public void Register(Action continuation, FiberTask? context, FiberTask.WaitToken? token = null)
    {
        var scheduler = KernelScheduler.Current;
        RegisterCancelable(continuation, context, token, scheduler);
    }

    public IDisposable? RegisterCancelable(Action continuation, FiberTask? context = null, FiberTask.WaitToken? token = null)
    {
        var scheduler = KernelScheduler.Current;
        return RegisterCancelable(continuation, context ?? scheduler?.CurrentTask, token, scheduler);
    }

    private IDisposable? RegisterCancelable(Action continuation, FiberTask? context, FiberTask.WaitToken? token,
        KernelScheduler? scheduler)
    {
        var effectiveScheduler = scheduler ?? context?.CommonKernel;
        lock (_lock)
        {
            if (IsSignaled)
            {
                // If already signaled, schedule immediately
                if (effectiveScheduler != null)
                    ScheduleContinuationWithWaitReason(effectiveScheduler, continuation, context, token);
                else
                    continuation();
                return NoopRegistration.Instance;
            }

            if (effectiveScheduler == null)
                throw new InvalidOperationException(
                    "AsyncWaitQueue.RegisterCancelable requires an active scheduler or a FiberTask context.");

            var id = ++_nextWaiterId;
            _waiters.Add((id, continuation, context, token, effectiveScheduler));
            return new WaitRegistration(this, id);
        }
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
        lock (_lock)
        {
            var idx = _waiters.FindIndex(x => x.Id == id);
            if (idx >= 0) _waiters.RemoveAt(idx);
        }
    }

    // Support for GetAwaiter directly on the instance (like Task)
    public WaitQueueAwaiter GetAwaiter()
    {
        return new WaitQueueAwaiter(this);
    }
}

public readonly struct WaitQueueAwaitable
{
    private readonly AsyncWaitQueue _queue;

    public WaitQueueAwaitable(AsyncWaitQueue queue)
    {
        _queue = queue;
    }

    public WaitQueueAwaiter GetAwaiter()
    {
        return new WaitQueueAwaiter(_queue);
    }
}

public struct WaitQueueAwaiter : INotifyCompletion
{
    private readonly AsyncWaitQueue _queue;
    private FiberTask.WaitToken? _token;

    public WaitQueueAwaiter(AsyncWaitQueue queue)
    {
        _queue = queue;
    }

    public bool IsCompleted => _queue.IsSignaled;

    public void OnCompleted(Action continuation)
    {
        var currentTask = KernelScheduler.Current?.CurrentTask;
        if (currentTask != null)
        {
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
            return;
        }

        _queue.Register(continuation);
    }

    public AwaitResult GetResult()
    {
        var task = KernelScheduler.Current?.CurrentTask;
        if (task == null || _token == null) return AwaitResult.Completed;

        var reason = task.CompleteWaitToken(_token);
        if (reason != WakeReason.Event && reason != WakeReason.None) return AwaitResult.Interrupted;
        return AwaitResult.Completed;
    }
}

public static class SchedulerUtils
{
    public static SelectAwaitable WaitAny(params AsyncWaitQueue[] queues)
    {
        return new SelectAwaitable(queues);
    }
}

public readonly struct SelectAwaitable
{
    private readonly AsyncWaitQueue[] _queues;

    public SelectAwaitable(AsyncWaitQueue[] queues)
    {
        _queues = queues;
    }

    public SelectAwaiter GetAwaiter()
    {
        return new SelectAwaiter(_queues);
    }
}

public struct SelectAwaiter : INotifyCompletion
{
    private readonly AsyncWaitQueue[] _queues;
    private FiberTask.WaitToken? _token;

    public SelectAwaiter(AsyncWaitQueue[] queues)
    {
        _queues = queues;
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
        var currentTask = KernelScheduler.Current?.CurrentTask;
        var runOnce = new RunOnceAction(continuation);
        var action = runOnce.Invoke;

        if (currentTask != null)
        {
            _token = currentTask.BeginWaitToken();
            foreach (var q in _queues) q.Register(action, currentTask, _token);
            currentTask.ArmSignalSafetyNet(_token, () => runOnce.Invoke());
            return;
        }

        foreach (var q in _queues) q.Register(action);
    }

    public AwaitResult GetResult()
    {
        var task = KernelScheduler.Current?.CurrentTask;
        if (task != null && _token != null)
        {
            var reason = task.CompleteWaitToken(_token);
            if (reason != WakeReason.Event && reason != WakeReason.None) return AwaitResult.Interrupted;
            return AwaitResult.Completed;
        }

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

    public ChildStateAwaitable(Process parent, int targetPid, bool wantStopped = true, bool wantContinued = true)
    {
        _parent = parent;
        _targetPid = targetPid;
        _wantStopped = wantStopped;
        _wantContinued = wantContinued;
        _scheduler = KernelScheduler.Current ?? throw new InvalidOperationException("No active KernelScheduler");
        _task = _scheduler.CurrentTask ?? throw new InvalidOperationException("No active FiberTask");
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
            if (reason != WakeReason.Event && reason != WakeReason.None)
            {
                return AwaitResult.Interrupted;
            }

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
