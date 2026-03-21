using System.Collections.Concurrent;
using Fiberish.Core;

namespace Fiberish.VFS;

internal sealed class ReadinessWaiter
{
    private static readonly ConcurrentBag<AsyncWaitQueue> WaitQueuePool = new();
    private readonly Func<LinuxFile, short, short> _poll;
    private readonly Func<LinuxFile, IReadyDispatcher, Action, short, IDisposable?> _registerWaitHandle;

    public ReadinessWaiter(
        Func<LinuxFile, short, short> poll,
        Func<LinuxFile, IReadyDispatcher, Action, short, IDisposable?> registerWaitHandle)
    {
        _poll = poll;
        _registerWaitHandle = registerWaitHandle;
    }

    public async ValueTask<bool> WaitAsync(LinuxFile file, IReadyDispatcher dispatcher, FiberTask task, short events)
    {
        while (true)
        {
            if ((_poll(file, events) & events) != 0)
                return true;

            if (task.HasInterruptingPendingSignal())
                return false;

            var waitQueue = RentWaitQueue();
            IDisposable? registration = null;
            try
            {
                registration = _registerWaitHandle(file, dispatcher, () => DispatchSignal(waitQueue, dispatcher), events);

                if (registration == null)
                {
                    if ((_poll(file, events) & events) != 0)
                        return true;
                    var spin = await new SleepAwaitable(1, task);
                    if (spin == AwaitResult.Interrupted)
                        return false;
                    continue;
                }

                var result = await waitQueue.WaitInterruptiblyAsync(task);
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

    private static void DispatchSignal(AsyncWaitQueue queue, IReadyDispatcher dispatcher)
    {
        if (!dispatcher.CanDispatch)
            throw new InvalidOperationException(
                "ReadinessWaiter callback requires an explicit dispatch-capable context.");
        dispatcher.Post(queue.Signal);
    }

    private bool WaitSynchronously(LinuxFile file, short events)
    {
        SpinWait spin = new();
        while (true)
        {
            if ((_poll(file, events) & events) != 0)
                return true;
            spin.SpinOnce();
        }
    }
}
