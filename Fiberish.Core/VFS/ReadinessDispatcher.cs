using Fiberish.Core;

namespace Fiberish.VFS;

internal interface IReadyDispatcher
{
    bool CanDispatch { get; }
    FiberTask? CurrentTask { get; }
    void Post(Action callback);
}

internal sealed class SchedulerReadyDispatcher : IReadyDispatcher
{
    private readonly KernelScheduler? _scheduler;

    public SchedulerReadyDispatcher(KernelScheduler? scheduler)
    {
        _scheduler = scheduler;
    }

    public bool CanDispatch => _scheduler != null;

    public FiberTask? CurrentTask => _scheduler?.CurrentTask;

    public void Post(Action callback)
    {
        var scheduler = _scheduler;
        if (scheduler == null)
        {
            callback();
            return;
        }

        scheduler.ScheduleFromAnyThread(callback);
    }

    public static SchedulerReadyDispatcher FromCurrent()
    {
        return new SchedulerReadyDispatcher(KernelScheduler.Current);
    }
}