using System.Diagnostics;
using System.Reflection;
using Fiberish.Core;

namespace Podish.Benchmarks;

internal static class SchedulerBenchmarks
{
    private static readonly MethodInfo WaitForEventMethod =
        typeof(KernelScheduler).GetMethod("WaitForEvent", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo WaitingForWakeField =
        typeof(KernelScheduler).GetField("_waitingForWake", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo WakeSignalField =
        typeof(KernelScheduler).GetField("_wakeSignal", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo PreciseWaitField =
        typeof(KernelScheduler).GetField("_preciseWait", BindingFlags.NonPublic | BindingFlags.Instance)!;

    public static IEnumerable<IThroughputBenchmark> CreateThroughput()
    {
        yield return new ThroughputBenchmark<SchedulerScope>
        {
            Name = "scheduler.wakeup.no-waiter",
            Category = "scheduler",
            CreateScope = static () => new SchedulerScope(),
            Run = static (scope, iterations) =>
            {
                for (var i = 0; i < iterations; i++) scope.Scheduler.WakeUp();
                return iterations;
            }
        };
    }

    public static IEnumerable<ILatencyBenchmark> CreateLatency()
    {
        yield return new LatencyBenchmark<EmptyScope>
        {
            Name = "scheduler.wakeup.latency",
            Category = "scheduler",
            CreateScope = static () => new EmptyScope(),
            RunOneMicroseconds = static _ => MeasureWakeLatency(),
            Samples = 120
        };

        foreach (var timeoutMs in new[] { 1, 5, 10 })
        {
            var captured = timeoutMs;
            yield return new LatencyBenchmark<PreciseScope>
            {
                Name = $"scheduler.precise-sleep.{captured}ms",
                Category = "scheduler",
                CreateScope = () => new PreciseScope(captured),
                RunOneMicroseconds = scope =>
                {
                    var sw = Stopwatch.StartNew();
                    scope.Wait(captured);
                    sw.Stop();
                    return sw.Elapsed.TotalMicroseconds;
                },
                Samples = captured == 10 ? 80 : 120
            };
        }
    }

    private static double MeasureWakeLatency()
    {
        using var scheduler = new KernelScheduler();
        var wait = WaitForEventMethod.CreateDelegate<Action<double, bool>>(scheduler);
        var thread = new Thread(() => wait(5000, false)) { IsBackground = true };
        thread.Start();
        try
        {
            if (!SpinWait.SpinUntil(() => (int)WaitingForWakeField.GetValue(scheduler)! != 0, 5000))
                throw new TimeoutException("scheduler waiter did not start");

            var sw = Stopwatch.StartNew();
            scheduler.WakeUp();
            if (!thread.Join(5000)) throw new TimeoutException("scheduler waiter did not wake");
            sw.Stop();
            return sw.Elapsed.TotalMicroseconds;
        }
        finally
        {
            if (thread.IsAlive) thread.Join(1000);
        }
    }

    private sealed class SchedulerScope : IDisposable
    {
        public KernelScheduler Scheduler { get; } = new();
        public void Dispose() => Scheduler.Dispose();
    }

    private sealed class EmptyScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class PreciseScope : IDisposable
    {
        private readonly AutoResetEvent _wake;
        private readonly HighResolutionWait _preciseWait;

        public PreciseScope(int _)
        {
            Scheduler = new KernelScheduler();
            _wake = (AutoResetEvent)WakeSignalField.GetValue(Scheduler)!;
            _preciseWait = (HighResolutionWait)PreciseWaitField.GetValue(Scheduler)!;
        }

        public KernelScheduler Scheduler { get; }

        public void Wait(int timeoutMs) => _preciseWait.Wait(_wake, timeoutMs, false);

        public void Dispose() => Scheduler.Dispose();
    }
}
