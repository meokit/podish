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

    // List of continuations to resume when Signaled.
    // Each entry stores the continuation Action, FiberTask context, and the KernelScheduler reference.
    // We capture the scheduler at registration time because Signal() may be called from a
    // background thread where KernelScheduler.Current is null (AsyncLocal doesn't flow to other threads).
    private readonly List<(Action Continuation, FiberTask? Context, KernelScheduler Scheduler)> _waiters = new();

    public bool IsSignaled { get; private set; }

    public void Signal()
    {
        List<(Action Continuation, FiberTask? Context, KernelScheduler Scheduler)> toWake;
        lock (_lock)
        {
            if (IsSignaled) return;
            IsSignaled = true;
            if (_waiters.Count == 0) return;
            toWake = new List<(Action Continuation, FiberTask? Context, KernelScheduler Scheduler)>(_waiters);
            _waiters.Clear();
        }

        // Resume all waiters outside the lock
        // Use the captured scheduler reference (not KernelScheduler.Current) because
        // Signal() may be called from a background thread where Current is null
        foreach (var (continuation, context, scheduler) in toWake)
        {
            if (context != null) context.WakeReason = WakeReason.Event;
            scheduler.Schedule(continuation, context);
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

    public WaitQueueAwaiter WaitAsync()
    {
        return new WaitQueueAwaiter(this);
    }

    // For compatibility with previous WaitHandle usage
    public void Set()
    {
        Signal();
    }

    public void Register(Action continuation)
    {
        var scheduler = KernelScheduler.Current;
        var context = scheduler?.CurrentTask;
        Register(continuation, context, scheduler);
    }

    public void Register(Action continuation, FiberTask? context)
    {
        var scheduler = KernelScheduler.Current;
        Register(continuation, context, scheduler);
    }

    private void Register(Action continuation, FiberTask? context, KernelScheduler? scheduler)
    {
        lock (_lock)
        {
            if (IsSignaled)
            {
                // If already signaled, schedule immediately
                if (context != null) context.WakeReason = WakeReason.Event;
                scheduler?.Schedule(continuation, context);
            }
            else
            {
                _waiters.Add((continuation, context, scheduler!));
            }
        }
    }

    // Support for GetAwaiter directly on the instance (like Task)
    public WaitQueueAwaiter GetAwaiter()
    {
        return new WaitQueueAwaiter(this);
    }
}

public readonly struct WaitQueueAwaiter(AsyncWaitQueue queue) : INotifyCompletion
{
    private readonly AsyncWaitQueue _queue = queue;

    public bool IsCompleted => _queue.IsSignaled;

    public void OnCompleted(Action continuation)
    {
        // Capture current task context
        var currentTask = KernelScheduler.Current?.CurrentTask;

        if (currentTask != null && currentTask.HasUnblockedPendingSignal())
        {
            currentTask.WakeReason = WakeReason.Signal;
            KernelScheduler.Current?.Schedule(continuation, currentTask);
            return;
        }

        _queue.Register(continuation, currentTask);
    }

    public AwaitResult GetResult()
    {
        var task = KernelScheduler.Current?.CurrentTask;
        if (task != null && task.WakeReason != WakeReason.Event && task.WakeReason != WakeReason.None)
        {
            task.WakeReason = WakeReason.None;
            return AwaitResult.Interrupted;
        }

        if (task != null) task.WakeReason = WakeReason.None;
        return AwaitResult.Completed;
    }

    public WaitQueueAwaiter GetAwaiter()
    {
        return this;
    }
}

public static class SchedulerUtils
{
    public static SelectAwaiter WaitAny(params AsyncWaitQueue[] queues)
    {
        return new SelectAwaiter(queues);
    }
}

public readonly struct SelectAwaiter(AsyncWaitQueue[] queues) : INotifyCompletion
{
    private readonly AsyncWaitQueue[] _queues = queues;

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
        // Capture scheduler and task context at registration time
        var scheduler = KernelScheduler.Current;
        var currentTask = scheduler?.CurrentTask;

        if (currentTask != null && currentTask.HasUnblockedPendingSignal())
        {
            currentTask.WakeReason = WakeReason.Signal;
            scheduler?.Schedule(continuation, currentTask);
            return;
        }

        // We use a shared state to ensure only one invocation
        var runOnce = new RunOnceAction(continuation);
        var action = runOnce.Invoke;

        foreach (var q in _queues) q.Register(action, currentTask);
    }

    public AwaitResult GetResult()
    {
        var task = KernelScheduler.Current?.CurrentTask;
        if (task != null && task.WakeReason != WakeReason.Event && task.WakeReason != WakeReason.None)
        {
            task.WakeReason = WakeReason.None;
            return AwaitResult.Interrupted;
        }

        if (task != null) task.WakeReason = WakeReason.None;
        return AwaitResult.Completed;
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

    public readonly struct ChildStateAwaiter : INotifyCompletion
    {
        private readonly Process _parent;
        private readonly int _targetPid;
        private readonly bool _wantStopped;
        private readonly bool _wantContinued;
        private readonly KernelScheduler _scheduler;
        private readonly FiberTask _task;

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
            if (_task.HasUnblockedPendingSignal())
            {
                _task.WakeReason = WakeReason.Signal;
                _scheduler.Schedule(continuation, _task);
                return;
            }

            // Register on all matching children's StateChangeEvent queues
            // Only register on events that are NOT yet signaled to avoid duplicate scheduling
            var runOnce = new RunOnceAction(continuation);
            var registered = false;

            foreach (var childPid in _parent.Children)
            {
                var childProc = _scheduler.GetProcess(childPid);
                if (childProc == null) continue;
                if (!MatchesTarget(childPid, childProc)) continue;

                // Skip if already signaled - IsCompleted check should have caught this
                // but we check again to avoid duplicate scheduling
                if (childProc.StateChangeEvent.IsSignaled) continue;

                childProc.StateChangeEvent.Register(runOnce.Invoke, _task);
                registered = true;
            }

            // If no registrations happened (all events already signaled), schedule immediately
            if (!registered)
            {
                _task.WakeReason = WakeReason.Event;
                _scheduler.Schedule(continuation, _task);
            }
        }

        public AwaitResult GetResult()
        {
            if (_task.WakeReason != WakeReason.Event && _task.WakeReason != WakeReason.None)
            {
                _task.WakeReason = WakeReason.None;
                return AwaitResult.Interrupted;
            }

            _task.WakeReason = WakeReason.None;
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