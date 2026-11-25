using System.Runtime.CompilerServices;

namespace Fiberish.Core;

public static class BrowserSchedulerHostBridge
{
    private static readonly Lock Sync = new();
    private static Action<int>? _armTimer;
    private static Action? _cancelTimer;
    private static Action<int>? _signalInterrupt;
    private static Func<int, int, int>? _waitForInterrupt;
    private static Func<int, int>? _pollInterrupt;
    private static Func<int, int>? _dispatchInput;
    private static Func<int, int>? _flushOutput;
    private static int _irqInputReady;
    private static int _irqOutputDrained;
    private static int _irqTimer;
    private static int _irqSchedulerWake;
    private static int _irqHttpRpc;
    private static readonly HashSet<HttpWaitRegistration> HttpWaiters = [];

    public static bool IsConfigured
    {
        get
        {
            lock (Sync)
            {
                return _armTimer != null
                       && _cancelTimer != null
                       && _signalInterrupt != null
                       && _waitForInterrupt != null
                       && _pollInterrupt != null
                       && _dispatchInput != null
                       && _flushOutput != null;
            }
        }
    }

    public static void Configure(
        int irqInputReady,
        int irqOutputDrained,
        int irqTimer,
        int irqSchedulerWake,
        int irqHttpRpc,
        Action<int> armTimer,
        Action cancelTimer,
        Action<int> signalInterrupt,
        Func<int, int, int> waitForInterrupt,
        Func<int, int> pollInterrupt,
        Func<int, int> dispatchInput,
        Func<int, int> flushOutput)
    {
        lock (Sync)
        {
            _irqInputReady = irqInputReady;
            _irqOutputDrained = irqOutputDrained;
            _irqTimer = irqTimer;
            _irqSchedulerWake = irqSchedulerWake;
            _irqHttpRpc = irqHttpRpc;
            _armTimer = armTimer;
            _cancelTimer = cancelTimer;
            _signalInterrupt = signalInterrupt;
            _waitForInterrupt = waitForInterrupt;
            _pollInterrupt = pollInterrupt;
            _dispatchInput = dispatchInput;
            _flushOutput = flushOutput;
        }
    }

    public static void SignalSchedulerWake()
    {
        Action<int>? signalInterrupt;
        int irqSchedulerWake;
        lock (Sync)
        {
            signalInterrupt = _signalInterrupt;
            irqSchedulerWake = _irqSchedulerWake;
        }

        if (irqSchedulerWake != 0)
            signalInterrupt?.Invoke(irqSchedulerWake);
    }

    public static void WaitForEvent(int timeoutMs)
    {
        Action<int>? armTimer;
        Action? cancelTimer;
        Func<int, int, int>? waitForInterrupt;
        Func<int, int>? pollInterrupt;
        Func<int, int>? dispatchInput;
        Func<int, int>? flushOutput;
        int irqInputReady;
        int irqOutputDrained;
        int irqTimer;
        int irqSchedulerWake;
        int irqHttpRpc;

        lock (Sync)
        {
            armTimer = _armTimer;
            cancelTimer = _cancelTimer;
            waitForInterrupt = _waitForInterrupt;
            pollInterrupt = _pollInterrupt;
            dispatchInput = _dispatchInput;
            flushOutput = _flushOutput;
            irqInputReady = _irqInputReady;
            irqOutputDrained = _irqOutputDrained;
            irqTimer = _irqTimer;
            irqSchedulerWake = _irqSchedulerWake;
            irqHttpRpc = _irqHttpRpc;
        }

        if (armTimer == null
            || cancelTimer == null
            || waitForInterrupt == null
            || pollInterrupt == null
            || dispatchInput == null
            || flushOutput == null)
            throw new InvalidOperationException("Browser scheduler host bridge is not configured.");

        var mask = irqInputReady | irqOutputDrained | irqTimer | irqSchedulerWake | irqHttpRpc;

        if (ProcessInterrupts(pollInterrupt(mask), irqInputReady, irqOutputDrained, irqHttpRpc, dispatchInput, flushOutput))
            return;

        if (timeoutMs >= 0)
            armTimer(timeoutMs);

        try
        {
            while (true)
            {
                var flags = waitForInterrupt(mask, -1);
                if (ProcessInterrupts(flags, irqInputReady, irqOutputDrained, irqHttpRpc, dispatchInput, flushOutput))
                    return;
            }
        }
        finally
        {
            if (timeoutMs >= 0)
                cancelTimer();
        }
    }

    internal static void PollHttpRpc()
    {
        Func<int, int>? poll;
        int mask;
        lock (Sync)
        {
            if (HttpWaiters.Count == 0) return;
            poll = _pollInterrupt;
            mask = _irqHttpRpc;
        }

        if (poll != null && (poll(mask) & mask) != 0)
            WakeHttpWaiters();
    }

    private static void WakeHttpWaiters()
    {
        HttpWaitRegistration[] waiters;
        lock (Sync)
            waiters = [.. HttpWaiters];
        foreach (var waiter in waiters)
            waiter.Operation.TryComplete(WakeReason.IO);
    }

    public static IDisposable TrackHttpRequest(FiberTask task, IDisposable request)
    {
        if (task.TryEnterAsyncOperation(null, out var operation) && operation != null &&
            operation.TryAddRegistration(TaskAsyncRegistration.From(request)))
            return new HttpRequestRegistration(operation);
        request.Dispose();
        throw new OperationCanceledException();
    }

    public readonly struct HttpRpcWait(FiberTask task, int timeoutMs)
    {
        public Awaiter GetAwaiter() => new(task, timeoutMs);

        public readonly struct Awaiter(FiberTask task, int timeoutMs) : INotifyCompletion
        {
            private readonly FiberTask.WaitToken _token = task.BeginWaitToken();
            public bool IsCompleted => false;
            public WakeReason GetResult() => task.CompleteWaitToken(_token);

            public void OnCompleted(Action continuation)
            {
                if (!task.TryEnterAsyncOperation(_token, out var operation) || operation == null)
                    return;

                operation.TryInitialize(continuation);
                var waiter = new HttpWaitRegistration(operation);
                lock (Sync)
                    HttpWaiters.Add(waiter);

                if (!operation.TryAddRegistration(waiter)) return;
                if (timeoutMs >= 0)
                    operation.TryAddRegistration(TaskAsyncRegistration.From(
                        task.CommonKernel.ScheduleTimer(timeoutMs, () => operation.TryComplete(WakeReason.Timer))));
                task.ArmInterruptingSignalSafetyNet(_token, () => operation.TryComplete(WakeReason.Signal));
            }
        }
    }

    private sealed class HttpRequestRegistration(TaskAsyncOperationHandle operation) : IDisposable
    {
        public void Dispose() => operation.TryCompleteWithoutScheduling();
    }

    private sealed class HttpWaitRegistration(TaskAsyncOperationHandle operation) : ITaskAsyncRegistration
    {
        public TaskAsyncOperationHandle Operation => operation;
        public bool IsActive => operation.IsActive;
        public void Cancel() => Dispose();

        public void Dispose()
        {
            lock (Sync)
                HttpWaiters.Remove(this);
        }
    }

    private static bool ProcessInterrupts(
        int flags,
        int irqInputReady,
        int irqOutputDrained,
        int irqHttpRpc,
        Func<int, int> dispatchInput,
        Func<int, int> flushOutput)
    {
        if (flags == 0)
            return false;

        if ((flags & irqHttpRpc) != 0)
            WakeHttpWaiters();

        var anyWork = false;
        if ((flags & irqInputReady) != 0)
        {
            dispatchInput(128);
            anyWork = true;
        }

        if ((flags & irqOutputDrained) != 0)
        {
            flushOutput(128);
            anyWork = true;
        }

        return anyWork || flags != 0;
    }
}
