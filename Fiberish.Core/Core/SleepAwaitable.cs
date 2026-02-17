using System.Runtime.CompilerServices;

namespace Fiberish.Core;

public readonly struct SleepAwaitable(long tickDuration)
{
    private readonly long _tickDuration = tickDuration;

    public SleepAwaiter GetAwaiter()
    {
        return new SleepAwaiter(_tickDuration);
    }

    public readonly struct SleepAwaiter(long tickDuration) : INotifyCompletion
    {
        private readonly long _tickDuration = tickDuration;

        public bool IsCompleted => false;

        public int GetResult()
        {
            return 0;
        }

        public void OnCompleted(Action continuation)
        {
            var scheduler = KernelScheduler.Current ?? throw new InvalidOperationException("No active KernelScheduler");
            var task = scheduler.CurrentTask ?? throw new InvalidOperationException("No active FiberTask");

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