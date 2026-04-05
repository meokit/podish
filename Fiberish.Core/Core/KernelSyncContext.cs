namespace Fiberish.Core;

internal sealed class KernelSyncContext : SynchronizationContext
{
    public KernelSyncContext(KernelScheduler scheduler, FiberTask? taskContext = null)
    {
        Scheduler = scheduler;
        TaskContext = taskContext;
    }

    internal KernelScheduler Scheduler { get; }

    internal FiberTask? TaskContext { get; }

    public override void Post(SendOrPostCallback d, object? state)
    {
        Scheduler.PostSynchronizationContext(d, state, TaskContext);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        Scheduler.SendSynchronizationContext(d, state, TaskContext);
    }
}