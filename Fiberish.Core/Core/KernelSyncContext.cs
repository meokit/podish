using System.Threading;

namespace Fiberish.Core;

internal sealed class KernelSyncContext : SynchronizationContext
{
    private readonly KernelScheduler _scheduler;

    public KernelSyncContext(KernelScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        _scheduler.Schedule(() => d(state), _scheduler.CurrentTask);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (KernelScheduler.Current == _scheduler)
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
        }, _scheduler.CurrentTask);
        gate.Wait();
        if (thrown != null) throw thrown;
    }
}
