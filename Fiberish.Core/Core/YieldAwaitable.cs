using System.Runtime.CompilerServices;

namespace Fiberish.Core;

public struct YieldAwaitable
{
    private readonly FiberTask _task;

    public YieldAwaitable(FiberTask task)
    {
        _task = task;
    }

    public readonly YieldAwaiter GetAwaiter()
    {
        return new YieldAwaiter(_task);
    }

    public readonly struct YieldAwaiter : ICriticalNotifyCompletion
    {
        private readonly FiberTask _task;

        public YieldAwaiter(FiberTask task)
        {
            _task = task;
        }

        public bool IsCompleted => false; // Always force async continuation

        public void GetResult()
        {
        }

        public void OnCompleted(Action continuation)
        {
            UnsafeOnCompleted(continuation);
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            var task = _task;
            var scheduler = task.CommonKernel;

            // Update task state
            task.Continuation = continuation;

            // Re-schedule immediately for next tick
            scheduler.Schedule(task);
        }
    }
}
