using System.Diagnostics;
using System.Text;

namespace Podish.Benchmarks;

internal interface IThroughputBenchmark
{
    string Name { get; }
    string Category { get; }
    ThroughputResult RunCase(int sampleMs);
}

internal interface ILatencyBenchmark
{
    string Name { get; }
    string Category { get; }
    LatencyResult RunCase();
}

/// <summary>A throughput benchmark: one scope, repeated batches, ns/op and B/op.</summary>
internal sealed class ThroughputBenchmark<TScope> : IThroughputBenchmark where TScope : class, IDisposable
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required Func<TScope> CreateScope { get; init; }
    public required Func<TScope, long, long> Run { get; init; }
    public int Samples { get; init; } = 9;

    public ThroughputResult RunCase(int sampleMs) => BenchmarkHarness.Run(this, sampleMs);
}

/// <summary>A latency benchmark: each sample returns microseconds for one operation.</summary>
internal sealed class LatencyBenchmark<TScope> : ILatencyBenchmark where TScope : class, IDisposable
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required Func<TScope> CreateScope { get; init; }
    public required Func<TScope, double> RunOneMicroseconds { get; init; }
    public int Samples { get; init; } = 120;

    public LatencyResult RunCase() => BenchmarkHarness.Run(this);
}

internal sealed record ThroughputResult(string Name, string Category, double MedianNs, double P95Ns,
    double AllocBytesPerOp, long Ops, double TotalMs);

internal sealed record LatencyResult(string Name, string Category, double MedianUs, double P95Us, double P99Us,
    double MeanUs, double MaxUs, int Samples);

internal static class BenchmarkHarness
{
    private const long WarmupMs = 150;
    private const int MinSamples = 5;

    public static ThroughputResult Run<TScope>(ThroughputBenchmark<TScope> bench, int sampleMs)
        where TScope : class, IDisposable
    {
        using var scope = bench.CreateScope();

        var warmup = Stopwatch.StartNew();
        _ = bench.Run(scope, 1);
        while (warmup.ElapsedMilliseconds < WarmupMs)
            _ = bench.Run(scope, 1);
        GC.KeepAlive(scope);

        // Choose a batch that makes one sample at least sampleMs long.
        long batch = 1;
        while (true)
        {
            var elapsedMs = Measure(bench, scope, batch, out var ops, out _);
            if (elapsedMs >= sampleMs || batch >= 1L << 20)
                break;
            var factor = Math.Clamp((long)(sampleMs / Math.Max(elapsedMs, 0.05)), 2, 16);
            batch = checked(batch * factor);
            if (batch <= 0) batch = 1L << 20;
        }

        var samples = Math.Max(MinSamples, bench.Samples);
        var ns = new double[samples];
        var alloc = new double[samples];
        long totalOps = 0;
        double totalMs = 0;
        for (var i = 0; i < samples; i++)
        {
            var elapsedMs = Measure(bench, scope, batch, out var ops, out var bytes);
            totalOps += ops;
            totalMs += elapsedMs;
            ns[i] = elapsedMs * 1_000_000.0 / ops;
            alloc[i] = (double)bytes / ops;
        }

        Array.Sort(ns);
        Array.Sort(alloc);
        return new ThroughputResult(bench.Name, bench.Category, Percentile(ns, 0.50), Percentile(ns, 0.95),
            Percentile(alloc, 0.50), totalOps, totalMs);
    }

    public static LatencyResult Run<TScope>(LatencyBenchmark<TScope> bench) where TScope : class, IDisposable
    {
        using var scope = bench.CreateScope();
        for (var i = 0; i < 12; i++) _ = bench.RunOneMicroseconds(scope);

        var samples = new double[Math.Max(12, bench.Samples)];
        for (var i = 0; i < samples.Length; i++) samples[i] = bench.RunOneMicroseconds(scope);
        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        var mean = samples.Average();
        return new LatencyResult(bench.Name, bench.Category, Percentile(sorted, 0.50), Percentile(sorted, 0.95),
            Percentile(sorted, 0.99), mean, sorted[^1], samples.Length);
    }

    private static double Measure<TScope>(ThroughputBenchmark<TScope> bench, TScope scope, long iterations,
        out long ops, out long allocatedBytes) where TScope : class, IDisposable
    {
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        ops = bench.Run(scope, iterations);
        sw.Stop();
        allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        return sw.Elapsed.TotalMilliseconds;
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0) return 0;
        var index = (int)Math.Ceiling(percentile * sortedValues.Length) - 1;
        return sortedValues[Math.Clamp(index, 0, sortedValues.Length - 1)];
    }

    public static string FormatTable(IReadOnlyList<ThroughputResult> throughput, IReadOnlyList<LatencyResult> latency)
    {
        var sb = new StringBuilder();
        if (throughput.Count > 0)
        {
            sb.AppendLine("| Benchmark | Median | p95 | Allocated |");
            sb.AppendLine("| --- | ---: | ---: | ---: |");
            foreach (var result in throughput)
                sb.AppendLine(
                    $"| `{result.Name}` | {FormatNs(result.MedianNs)} | {FormatNs(result.P95Ns)} | {FormatBytes(result.AllocBytesPerOp)}/op |");
        }

        if (latency.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("| Latency benchmark | p50 | p95 | p99 | mean | max |");
            sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
            foreach (var result in latency)
                sb.AppendLine(
                    $"| `{result.Name}` | {FormatUs(result.MedianUs)} | {FormatUs(result.P95Us)} | {FormatUs(result.P99Us)} | {FormatUs(result.MeanUs)} | {FormatUs(result.MaxUs)} |");
        }

        return sb.ToString();
    }

    private static string FormatNs(double ns) => ns switch
    {
        < 1_000 => $"{ns:F1} ns",
        < 1_000_000 => $"{ns / 1_000:F2} µs",
        _ => $"{ns / 1_000_000:F2} ms"
    };

    private static string FormatUs(double us) => us switch
    {
        < 1_000 => $"{us:F1} µs",
        _ => $"{us / 1_000:F2} ms"
    };

    private static string FormatBytes(double bytes) => bytes switch
    {
        < 1024 => $"{bytes:F0} B",
        < 1024 * 1024 => $"{bytes / 1024:F1} KiB",
        _ => $"{bytes / (1024 * 1024):F2} MiB"
    };
}
