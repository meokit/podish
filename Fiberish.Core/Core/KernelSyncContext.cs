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
        _scheduler.Schedule(() => d(state), _taskContext);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (KernelScheduler.Current == _scheduler && _scheduler.CurrentTask == _taskContext)
        {
            d(state);
            return;
        }

        using var gate = new ManualResetEventSlim(false);
        Exception? thrown = null;
        _scheduler.Schedule(() =>
        {
            try
            {
                d(state);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
            finally
            {
                gate.Set();
            }
        }, _taskContext);
        gate.Wait();
        if (thrown != null) throw thrown;
    }

    public KernelSyncContext WithTaskContext(FiberTask? task)
    {
        if (ReferenceEquals(task, _taskContext)) return this;
        return new KernelSyncContext(_scheduler, task);
    }
}