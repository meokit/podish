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
            if (!_task.TryEnterAsyncOperation(_token, out var operation) || operation == null)
                return;

            var wakeHandler = new SleepWakeHandler(_scheduler, _task, _token, continuation, operation);
            wakeHandler.Register(_tickDuration);
            _task.ArmInterruptingSignalSafetyNet(_token, wakeHandler.OnSignal);
        }
    }

    private sealed class SleepWakeHandler
    {
        private readonly TaskAsyncOperationHandle _operation;
        private readonly KernelScheduler _scheduler;
        private Timer? _timer;

        public SleepWakeHandler(KernelScheduler scheduler, FiberTask task, FiberTask.WaitToken token,
            Action continuation, TaskAsyncOperationHandle operation)
        {
            _scheduler = scheduler;
            _operation = operation;
            _operation.TryInitialize(continuation, WaitContinuationMode.ResumeTask);
        }

        public void Register(long tickDuration)
        {
            _timer = _scheduler.ScheduleTimer(tickDuration, OnTimerFired);
            _operation.TryAddRegistration(TaskAsyncRegistration.From(_timer));
        }

        private void OnTimerFired()
        {
            _operation.TryComplete(WakeReason.Timer);
        }

        public void OnSignal()
        {
            _operation.TryComplete(WakeReason.Signal);
        }
    }
}