using System.Runtime.CompilerServices;

namespace Fiberish.Core;

/// <summary>
///     A lightweight, single-threaded async coordination primitive.
///     Allows tasks (FiberTasks) to await events like I/O, timers, or process exit.
///     Replaces standard WaitHandle/ManualResetEvent for our cooperative scheduler.
/// </summary>
public class AsyncWaitQueue
{
    // List of continuations to resume when Signaled.
    // Each entry stores the continuation Action and the FiberTask context it belongs to.
    private readonly List<(Action Continuation, FiberTask? Context)> _waiters = new();

    public bool IsSignaled { get; private set; }

    public void Signal()
    {
        IsSignaled = true;
        if (_waiters.Count > 0)
        {
            // Resume all waiters
            // We copy the list to allow modification during iteration (though unlikely here)
            var toWake = _waiters.ToArray();
            _waiters.Clear();

            foreach (var (continuation, context) in toWake)
                // Schedule the continuation on the kernel scheduler
                KernelScheduler.Current?.Schedule(continuation, context);
        }
    }

    public void Reset()
    {
        IsSignaled = false;
        _waiters.Clear();
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
        Register(continuation, KernelScheduler.Current?.CurrentTask);
    }

    public void Register(Action continuation, FiberTask? context)
    {
        if (IsSignaled)
            // If already signaled, schedule immediately
            KernelScheduler.Current?.Schedule(continuation, context);
        else
            _waiters.Add((continuation, context));
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
        _queue.Register(continuation, currentTask);
    }

    public void GetResult()
    {
        // No result, just completion
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
        var currentTask = KernelScheduler.Current?.CurrentTask;

        // Register continuation with ALL queues
        // Note: This is a simple implementation. When one queue fires,
        // the continuation runs. The other registrations remain but
        // running the continuation again is harmless if idempotent or handled.
        // For better performance/correctness, we should use a "RunOnce" wrapper.

        // We use a shared state to ensure only one invocation
        var runOnce = new RunOnceAction(continuation);
        var action = runOnce.Invoke;

        foreach (var q in _queues) q.Register(action, currentTask);
    }

    public void GetResult()
    {
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