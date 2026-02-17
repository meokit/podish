using System.Runtime.CompilerServices;

namespace Bifrost.Core;

public readonly struct SleepAwaitable(long tickDuration)
{
    private readonly long _tickDuration = tickDuration;

    public SleepAwaiter GetAwaiter() => new(_tickDuration);

    public readonly struct SleepAwaiter(long tickDuration) : INotifyCompletion
    {
        private readonly long _tickDuration = tickDuration;

        public bool IsCompleted => false;

        public int GetResult() => 0;

        public void OnCompleted(Action continuation)
        {
            var scheduler = KernelScheduler.Current;
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
