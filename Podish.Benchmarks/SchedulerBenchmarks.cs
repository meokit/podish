using System.Diagnostics;
using System.Reflection;
using Fiberish.Core;

namespace Podish.Benchmarks;

internal static class SchedulerBenchmarks
{
    private static readonly MethodInfo WaitForEventMethod =
        typeof(KernelScheduler).GetMethod("WaitForEvent", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo TryWaitPreciseMethod =
        typeof(KernelScheduler).GetMethod("TryWaitPrecise", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo EventWaitersField =
        typeof(KernelScheduler).GetField("_eventWaiters", BindingFlags.NonPublic | BindingFlags.Instance)!;

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
                    if (!scope.Wait(captured)) throw new InvalidOperationException("precise wait failed");
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
        var wait = WaitForEventMethod.CreateDelegate<Action<int>>(scheduler);
        var thread = new Thread(() => wait(5000)) { IsBackground = true };
        thread.Start();
        try
        {
            if (!SpinWait.SpinUntil(() => (int)EventWaitersField.GetValue(scheduler)! != 0, 5000))
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
        private readonly Func<int, bool> _wait;

        public PreciseScope(int _)
        {
            Scheduler = new KernelScheduler();
            _wait = TryWaitPreciseMethod.CreateDelegate<Func<int, bool>>(Scheduler);
        }

        public KernelScheduler Scheduler { get; }

        public bool Wait(int timeoutMs) => _wait(timeoutMs);

        public void Dispose() => Scheduler.Dispose();
    }
}
