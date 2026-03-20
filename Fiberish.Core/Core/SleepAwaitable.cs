using System.Runtime.CompilerServices;

namespace Fiberish.Core;

public readonly struct SleepAwaitable
{
    private readonly long _tickDuration;
    private readonly KernelScheduler _scheduler;
    private readonly FiberTask _task;


    public SleepAwaitable(long tickDuration, FiberTask task, KernelScheduler? scheduler = null)
    {
        _tickDuration = tickDuration;
        _scheduler = scheduler ?? task.CommonKernel;
        _task = task;
    }


    public SleepAwaiter GetAwaiter()
    {
        return new SleepAwaiter(_tickDuration, _task, _scheduler);
    }

    public readonly struct SleepAwaiter(long tickDuration, FiberTask task, KernelScheduler scheduler)
        : INotifyCompletion
    {
        private readonly long _tickDuration = tickDuration;
        private readonly KernelScheduler _scheduler = scheduler;
        private readonly FiberTask _task = task;
        private readonly FiberTask.WaitToken _token = task.BeginWaitToken();

        public bool IsCompleted => false;

        public AwaitResult GetResult()
        {
            var reason = _task.CompleteWaitToken(_token);
            if (reason != WakeReason.Timer && reason != WakeReason.None) return AwaitResult.Interrupted;
            return AwaitResult.Completed;
        }

        public void OnCompleted(Action continuation)
        {
            var scheduler = _scheduler;
            var task = _task;
            var token = _token;

            // Register timer callback: when timer fires, schedule the task back to run queue.
            var timer = scheduler.ScheduleTimer(_tickDuration, () =>
            {
                if (!task.TrySetWaitReason(token, WakeReason.Timer)) return;
                task.Continuation = continuation;
                scheduler.Schedule(task);
            });

            // ArmSignalSafetyNet: register wake continuation and re-check for signals that
            // arrived before BeginWaitToken was called — TOCTOU-safe.
            task.ArmSignalSafetyNet(token, () =>
            {
                timer.Cancel();
                task.Continuation = continuation;
                scheduler.Schedule(task);
            });
        }
    }
}
