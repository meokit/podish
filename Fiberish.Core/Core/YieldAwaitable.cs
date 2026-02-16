using System.Runtime.CompilerServices;

namespace Bifrost.Core;

public struct YieldAwaitable
{
    public YieldAwaiter GetAwaiter() => new YieldAwaiter();

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
            var scheduler = KernelScheduler.Instance;
            var task = scheduler.CurrentTask;
            if (task == null)
            {
                throw new InvalidOperationException("YieldAwaitable: No active FiberTask.");
            }

            // Update task state
            task.Continuation = continuation;
            
            // Re-schedule immediately for next tick
            scheduler.Schedule(task);
        }
    }
}
