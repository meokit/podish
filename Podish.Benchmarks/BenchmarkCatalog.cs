namespace Podish.Benchmarks;

internal static class BenchmarkCatalog
{
    public static (List<IThroughputBenchmark> Throughput, List<ILatencyBenchmark> Latency) Create()
    {
        var throughput = new List<IThroughputBenchmark>();
        var latency = new List<ILatencyBenchmark>();
        throughput.AddRange(InotifyBenchmarks.Create());
        throughput.AddRange(SchedulerBenchmarks.CreateThroughput());
        throughput.AddRange(CpuBenchmarks.Create());
        throughput.AddRange(MemoryBenchmarks.Create());
        throughput.AddRange(ExecBenchmarks.Create());
        latency.AddRange(SchedulerBenchmarks.CreateLatency());
        return (throughput, latency);
    }
}
