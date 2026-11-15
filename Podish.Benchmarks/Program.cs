using System.Globalization;
using System.Text.Json;

namespace Podish.Benchmarks;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (HasFlag(args, "--help") || HasFlag(args, "-h"))
        {
            PrintUsage();
            return 0;
        }

        var filter = GetValue(args, "--filter");
        var category = GetValue(args, "--category");
        var sampleMs = GetIntValue(args, "--sample-ms", 30);
        var includeLatency = !HasFlag(args, "--no-latency");
        var (throughput, latency) = BenchmarkCatalog.Create();

        bool Matches(string name, string cat) =>
            (filter == null || name.Contains(filter, StringComparison.OrdinalIgnoreCase)) &&
            (category == null || cat.Equals(category, StringComparison.OrdinalIgnoreCase));

        throughput = throughput.Where(x => Matches(x.Name, x.Category)).ToList();
        latency = includeLatency ? latency.Where(x => Matches(x.Name, x.Category)).ToList() : [];

        if (HasFlag(args, "--list"))
        {
            foreach (var bench in throughput) Console.WriteLine($"throughput  {bench.Category,-10} {bench.Name}");
            foreach (var bench in latency) Console.WriteLine($"latency     {bench.Category,-10} {bench.Name}");
            return 0;
        }

        Console.WriteLine($"Podish.Benchmarks  runtime={Environment.Version}  os={Environment.OSVersion}  " +
                          $"cores={Environment.ProcessorCount}  sampleMs={sampleMs}");
        Console.WriteLine($"cases={throughput.Count + latency.Count} (throughput={throughput.Count}, latency={latency.Count})");
        Console.WriteLine();

        var throughputResults = new List<ThroughputResult>();
        var latencyResults = new List<LatencyResult>();
        var errors = new List<string>();
        foreach (var bench in throughput)
        {
            try
            {
                Console.Write($"[run] {bench.Name} ... ");
                var result = bench.RunCase(sampleMs);
                throughputResults.Add(result);
                Console.WriteLine($"{result.MedianNs / 1000.0:F2} us/op, {result.AllocBytesPerOp:F0} B/op");
            }
            catch (Exception ex)
            {
                errors.Add($"{bench.Name}: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine("FAILED");
            }
        }

        foreach (var bench in latency)
        {
            try
            {
                Console.Write($"[run] {bench.Name} ... ");
                var result = bench.RunCase();
                latencyResults.Add(result);
                Console.WriteLine($"p50={result.MedianUs:F1} us, p95={result.P95Us:F1} us");
            }
            catch (Exception ex)
            {
                errors.Add($"{bench.Name}: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine("FAILED");
            }
        }

        Console.WriteLine();
        Console.WriteLine(BenchmarkHarness.FormatTable(throughputResults, latencyResults));

        var jsonPath = GetValue(args, "--json") ??
                       Path.Combine("artifacts", "benchmarks",
                           DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), "results.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!);
        var payload = new
        {
            generatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            runtime = Environment.Version.ToString(),
            os = Environment.OSVersion.ToString(),
            processorCount = Environment.ProcessorCount,
            sampleMs,
            throughput = throughputResults,
            latency = latencyResults,
            errors
        };
        File.WriteAllText(jsonPath,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"json: {Path.GetFullPath(jsonPath)}");

        if (errors.Count > 0)
        {
            Console.WriteLine();
            foreach (var error in errors) Console.Error.WriteLine($"[bench] {error}");
            return 1;
        }

        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
                          Podish.Benchmarks

                          Microbenchmarks for the guest runtime hot paths introduced by the
                          LTP fix branches: inotify delivery, VFS event hooks, scheduler
                          wakeups, CPU accounting, and file-backed mappings.

                          Usage:
                            dotnet run --project Podish.Benchmarks -c Release -- [options]

                          Options:
                            --filter <text>    Only benchmarks whose name contains text.
                            --category <name>  Only one category (inotify, vfs, scheduler, cpu, mmap).
                            --sample-ms <n>    Target duration of one throughput sample (default 30).
                            --no-latency       Skip latency benchmarks.
                            --json <path>      Write results here (default artifacts/benchmarks/<utc>/results.json).
                            --list             List benchmark names without running them.
                          """);
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? GetValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static int GetIntValue(string[] args, string name, int fallback)
    {
        var raw = GetValue(args, name);
        return raw != null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }
}
