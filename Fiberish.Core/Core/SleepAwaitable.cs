using System.Runtime.CompilerServices;

namespace Fiberish.Core;

public readonly struct SleepAwaitable
{
    private readonly long _tickDuration;
    private readonly KernelScheduler _scheduler;
    private readonly FiberTask _task;

    public SleepAwaitable(long tickDuration)
    {
        _tickDuration = tickDuration;
        _scheduler = KernelScheduler.Current ?? throw new InvalidOperationException("No active KernelScheduler");
        _task = _scheduler.CurrentTask ?? throw new InvalidOperationException("No active FiberTask");
    }

    public SleepAwaitable(long tickDuration, KernelScheduler scheduler, FiberTask task)
    {
        _tickDuration = tickDuration;
        _scheduler = scheduler;
        _task = task;
    }

    public SleepAwaiter GetAwaiter()
    {
        return new SleepAwaiter(_tickDuration, _scheduler, _task);
    }

    public readonly struct SleepAwaiter(long tickDuration, KernelScheduler scheduler, FiberTask task) : INotifyCompletion
    {
        private readonly long _tickDuration = tickDuration;
        private readonly KernelScheduler _scheduler = scheduler;
        private readonly FiberTask _task = task;

        public bool IsCompleted => false;

        public int GetResult()
        {
            return 0;
        }

        public void OnCompleted(Action continuation)
        {
            var scheduler = _scheduler;
            var task = _task;

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
