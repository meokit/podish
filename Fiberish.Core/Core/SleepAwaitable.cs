using System.Runtime.CompilerServices;

namespace Bifrost.Core;

public struct SleepAwaitable
{
    private readonly long _tickDuration;
    
    public SleepAwaitable(long tickDuration)
    {
        _tickDuration = tickDuration;
    }

    public SleepAwaiter GetAwaiter() => new SleepAwaiter(_tickDuration);

    public readonly struct SleepAwaiter : INotifyCompletion
    {
        private readonly long _tickDuration;

        public SleepAwaiter(long tickDuration)
        {
            _tickDuration = tickDuration;
        }

        public bool IsCompleted => false;

        public int GetResult() => 0;

        public void OnCompleted(Action continuation)
        {
            var scheduler = KernelScheduler.Instance;
            var task = scheduler.CurrentTask;
            if (task == null) throw new InvalidOperationException("No active FiberTask");

            // Register timer callback
            // When timer fires, we schedule the task back to run queue
            scheduler.ScheduleTimer(_tickDuration, () => 
            {
                task.Continuation = continuation;
                scheduler.Schedule(task);
            });
        }
    }
}
