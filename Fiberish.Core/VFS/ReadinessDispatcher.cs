using Fiberish.Core;

namespace Fiberish.VFS;

internal interface IReadyDispatcher
{
    bool CanDispatch { get; }
    FiberTask? CurrentTask { get; }
    KernelScheduler? Scheduler { get; }
    void Post(Action callback);
}

internal sealed class SchedulerReadyDispatcher : IReadyDispatcher
{
    public SchedulerReadyDispatcher(KernelScheduler? scheduler)
    {
        Scheduler = scheduler;
    }

    public bool CanDispatch => Scheduler != null;

    public FiberTask? CurrentTask => Scheduler?.CurrentTask;

    public KernelScheduler? Scheduler { get; }

    public void Post(Action callback)
    {
        var scheduler = Scheduler;
        if (scheduler == null)
        {
            callback();
            return;
        }

        scheduler.ScheduleFromAnyThread(callback);
    }

    public static SchedulerReadyDispatcher FromCurrent()
    {
        return SynchronizationContext.Current is KernelSyncContext context
            ? new SchedulerReadyDispatcher(context.Scheduler)
            : new SchedulerReadyDispatcher(null);
    }
}