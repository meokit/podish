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
            var wakeHandler = new SleepWakeHandler(_scheduler, _task, _token, continuation);
            wakeHandler.Register(_tickDuration);
            _task.ArmInterruptingSignalSafetyNet(_token, wakeHandler.OnSignal);
        }
    }

    private sealed class SleepWakeHandler
    {
        private readonly Action _continuation;
        private readonly KernelScheduler _scheduler;
        private readonly FiberTask _task;
        private readonly FiberTask.WaitToken _token;
        private Timer? _timer;

        public SleepWakeHandler(KernelScheduler scheduler, FiberTask task, FiberTask.WaitToken token,
            Action continuation)
        {
            _scheduler = scheduler;
            _task = task;
            _token = token;
            _continuation = continuation;
        }

        public void Register(long tickDuration)
        {
            _timer = _scheduler.ScheduleTimer(tickDuration, OnTimerFired);
        }

        private void OnTimerFired()
        {
            if (!_task.TrySetWaitReason(_token, WakeReason.Timer, scheduleStoredContinuation: false)) return;
            _scheduler.ScheduleContinuation(_continuation, _task, WaitContinuationMode.ResumeTask);
        }

        public void OnSignal()
        {
            _timer?.Cancel();
            _scheduler.ScheduleContinuation(_continuation, _task, WaitContinuationMode.ResumeTask);
        }
    }
}
