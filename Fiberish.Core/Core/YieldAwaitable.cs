using System.Runtime.CompilerServices;

namespace Bifrost.Core;

public struct YieldAwaitable
{
    public readonly YieldAwaiter GetAwaiter() => new();

    public readonly struct YieldAwaiter : ICriticalNotifyCompletion
    {
        public bool IsCompleted => false; // Always force async continuation

        public void GetResult() { }

        public void OnCompleted(Action continuation)
        {
            UnsafeOnCompleted(continuation);
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            var scheduler = KernelScheduler.Current;
            var task = scheduler.CurrentTask ?? throw new InvalidOperationException("YieldAwaitable: No active FiberTask.");

            // Update task state
            task.Continuation = continuation;

            // Re-schedule immediately for next tick
            scheduler.Schedule(task);
        }
    }
}
