using Fiberish.Core;

namespace Podish.Benchmarks;

internal static class CpuBenchmarks
{
    public static IEnumerable<IThroughputBenchmark> Create()
    {
        foreach (var threadCount in new[] { 1, 16 })
        {
            var captured = threadCount;
            yield return new ThroughputBenchmark<CpuSnapshotScope>
            {
                Name = $"cpu.snapshot.{captured}-threads",
                Category = "cpu",
                CreateScope = () => new CpuSnapshotScope(captured, running: false),
                Run = static (scope, iterations) => Snapshot(scope, iterations)
            };
        }

        yield return new ThroughputBenchmark<CpuSnapshotScope>
        {
            Name = "cpu.snapshot.running-task",
            Category = "cpu",
            CreateScope = () => new CpuSnapshotScope(1, running: true),
            Run = static (scope, iterations) => Snapshot(scope, iterations)
        };
    }

    private static long Snapshot(CpuSnapshotScope scope, long iterations)
    {
        long checksum = 0;
        for (var i = 0; i < iterations; i++)
        {
            var snapshot = scope.Process.GetSelfCpuTimeSnapshot();
            checksum += snapshot.UserNs + snapshot.SystemNs;
        }

        // Keep the reads observable so the JIT cannot remove the snapshot calls.
        GC.KeepAlive(checksum);
        return iterations;
    }

    private sealed class CpuSnapshotScope : IDisposable
    {
        private readonly GuestBenchmarkScope _env;
        private readonly List<Engine> _extraEngines = [];
        private readonly bool _running;

        public CpuSnapshotScope(int threadCount, bool running)
        {
            _env = new GuestBenchmarkScope();
            _running = running;
            for (var i = 1; i < threadCount; i++)
            {
                var engine = new Engine(_env.Mem.MemoryContext);
                _extraEngines.Add(engine);
                _ = new FiberTask(200 + i, _env.Process, engine, _env.Scheduler);
            }

            if (running)
            {
                _env.Process.RunningTask = _env.Task;
                _env.Task.BeginGuestCpuAccounting();
            }
        }

        public Fiberish.Core.Process Process => _env.Process;

        public void Dispose()
        {
            if (_running)
            {
                _env.Task.EndGuestCpuAccounting();
                _env.Process.RunningTask = null;
            }

            foreach (var engine in _extraEngines)
                engine.Dispose();
            _env.Dispose();
        }
    }
}
