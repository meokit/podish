namespace Fiberish.Core;

internal sealed class KernelSyncContext : SynchronizationContext
{
    private readonly KernelScheduler _scheduler;
    private readonly FiberTask? _taskContext;

    public KernelSyncContext(KernelScheduler scheduler, FiberTask? taskContext = null)
    {
        _scheduler = scheduler;
        _taskContext = taskContext;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        _scheduler.PostSynchronizationContext(d, state, _taskContext);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        _scheduler.SendSynchronizationContext(d, state, _taskContext);
    }

    internal KernelScheduler Scheduler => _scheduler;

    internal FiberTask? TaskContext => _taskContext;
}
