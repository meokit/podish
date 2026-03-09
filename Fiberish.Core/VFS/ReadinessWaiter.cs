using System.Collections.Concurrent;
using Fiberish.Core;

namespace Fiberish.VFS;

internal sealed class ReadinessWaiter
{
    private static readonly ConcurrentBag<AsyncWaitQueue> WaitQueuePool = new();

    private readonly Func<LinuxFile, short, short> _poll;
    private readonly Func<LinuxFile, Action, short, IDisposable?> _registerWaitHandle;
    private readonly Func<FiberTask?> _currentTaskAccessor;

    public ReadinessWaiter(
        Func<LinuxFile, short, short> poll,
        Func<LinuxFile, Action, short, IDisposable?> registerWaitHandle,
        Func<FiberTask?> currentTaskAccessor)
    {
        _poll = poll;
        _registerWaitHandle = registerWaitHandle;
        _currentTaskAccessor = currentTaskAccessor;
    }

    public async ValueTask<bool> WaitAsync(LinuxFile file, short events)
    {
        while (true)
        {
            if ((_poll(file, events) & events) != 0)
                return true;

            var task = _currentTaskAccessor();
            var scheduler = task?.CommonKernel;
            if (task != null && task.HasUnblockedPendingSignal())
                return false;

            var waitQueue = RentWaitQueue();
            IDisposable? registration = null;
            try
            {
                registration = _registerWaitHandle(file, () => DispatchSignal(waitQueue, scheduler), events);

                if (registration == null)
                {
                    if ((_poll(file, events) & events) != 0)
                        return true;
                    var spin = await new SleepAwaitable(1);
                    if (spin == AwaitResult.Interrupted)
                        return false;
                    continue;
                }

                var result = await waitQueue.WaitAsync();
                if (result == AwaitResult.Interrupted)
                    return false;
            }
            finally
            {
                registration?.Dispose();
                RecycleWaitQueue(waitQueue);
            }
        }
    }

    private static AsyncWaitQueue RentWaitQueue()
    {
        if (WaitQueuePool.TryTake(out var queue))
            return queue;
        return new AsyncWaitQueue();
    }

    private static void RecycleWaitQueue(AsyncWaitQueue queue)
    {
        queue.Reset();
        WaitQueuePool.Add(queue);
    }

    private static void DispatchSignal(AsyncWaitQueue queue, KernelScheduler? scheduler)
    {
        if (scheduler == null)
            throw new InvalidOperationException("ReadinessWaiter callback requires an active scheduler-bound task context.");
        scheduler.ScheduleFromAnyThread(queue.Signal);
    }
}
