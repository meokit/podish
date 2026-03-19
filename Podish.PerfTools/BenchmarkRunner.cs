using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Podish.PerfTools;

internal static class BenchmarkRunner
{
    private const string MarkerBegin = "__PODISH_BENCH_BEGIN__";
    private const string MarkerEnd = "__PODISH_BENCH_END__";
    private const string DefaultEngine = "jit";
    private static readonly string[] DefaultCases = ["compress", "compile", "run"];
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
    private static readonly Regex IterationsPerSecRegex = new(@"Iterations/Sec\s*:\s*([0-9.]+)", RegexOptions.Compiled);
    private static readonly Regex CoreMarkScoreRegex = new(@"CoreMark 1\.0\s*:\s*([0-9.]+)", RegexOptions.Compiled);

    public static int Run(string[] args)
    {
        var options = ParseArgs(args);
        var projectRoot = Path.GetFullPath(options.ProjectRoot);
        var baseRootfs = Path.GetFullPath(options.Rootfs);
        var workDir = Path.GetFullPath(options.WorkDir);
        var jitConfiguration = DefaultJitConfiguration(projectRoot);
        var fibercpuLibrary = DefaultFibercpuLibrary(projectRoot, jitConfiguration);
        var aotBinary = Path.GetFullPath(options.AotBinary ?? DefaultAotBinary(projectRoot));

        if (!Directory.Exists(baseRootfs))
        {
            Console.Error.WriteLine(
                $"rootfs not found: {baseRootfs}\n" +
                $"run benchmark/podish_perf/prepare_coremark_env.sh first");
            return 1;
        }

        if (options.Engine == "aot" && !File.Exists(aotBinary))
        {
            Console.Error.WriteLine(
                $"aot binary not found: {aotBinary}\n" +
                $"build it first with: dotnet publish Podish.Cli/Podish.Cli.csproj -c Release -r osx-arm64 -p:PublishAot=true");
            return 1;
        }

        if (options.JitHandlerProfileBlockDump || options.AggregateSuperopcodeCandidates)
            EnsurePerfToolsBuild(projectRoot);

        var timestamp = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
        var resultsDir = Path.GetFullPath(options.ResultsDir ?? Path.Combine(RepoRoot(), "benchmark", "podish_perf", "results", timestamp));
        Directory.CreateDirectory(resultsDir);
        Directory.CreateDirectory(workDir);

        var selectedCases = options.Cases.Count > 0 ? options.Cases : DefaultCases.ToList();
        var allResults = new List<SampleResult>();

        Console.WriteLine($"[runner] project_root={projectRoot}");
        Console.WriteLine($"[runner] rootfs={baseRootfs}");
        Console.WriteLine($"[runner] results_dir={resultsDir}");
        Console.WriteLine($"[runner] engine={options.Engine}");
        if (options.Engine == "jit")
            Console.WriteLine($"[runner] jit_configuration={jitConfiguration}");
        if (options.Engine == "aot")
            Console.WriteLine($"[runner] aot_binary={aotBinary}");

        var disableSuperopcodesForRun = options.DisableSuperopcodes;
        if (options.JitHandlerProfileBlockDump && !options.AllowSuperopcodesInBlockAnalysis)
            disableSuperopcodesForRun = true;

        if (options.JitHandlerProfileBlockDump)
        {
            Console.WriteLine("[runner] jit_handler_profile_block_dump=enabled");
            Console.WriteLine($"[runner] fibercpu_library={fibercpuLibrary}");
            if (disableSuperopcodesForRun)
                Console.WriteLine("[runner] disable_superopcodes=enabled");
            if (!options.DisableSuperopcodes && disableSuperopcodesForRun)
                Console.WriteLine("[runner] auto_disable_superopcodes_for_block_analysis=enabled");
            if (options.BlockNGram > 0)
                Console.WriteLine($"[runner] block_n_gram={options.BlockNGram} top_ngrams={options.BlockTopNGrams}");
            if (options.AggregateSuperopcodeCandidates)
                Console.WriteLine($"[runner] aggregate_superopcode_candidates=enabled top={options.CandidateTop}");
        }

        Console.WriteLine($"[runner] cases={string.Join(',', selectedCases)} repeat={options.Repeat} iterations={options.Iterations}");

        if (options.JitHandlerProfileBlockDump && options.Engine != "jit")
        {
            Console.Error.WriteLine("--jit-handler-profile-block-dump requires --engine=jit");
            return 1;
        }

        for (var i = 0; i < selectedCases.Count; i++)
        {
            var caseName = selectedCases[i];
            for (var iteration = 1; iteration <= options.Repeat; iteration++)
            {
                Console.WriteLine($"[runner] case={caseName} sample={iteration}/{options.Repeat}");
                var sample = RunSample(
                    projectRoot,
                    options.Engine,
                    aotBinary,
                    baseRootfs,
                    caseName,
                    iteration,
                    options.Timeout,
                    options.Iterations,
                    workDir,
                    resultsDir,
                    options.ReuseRootfs,
                    options.KeepWorkdirs,
                    options.JitHandlerProfileBlockDump,
                    options.JitHandlerProfileBlockDump && !options.SkipAutoAnalyzeBlockDump,
                    fibercpuLibrary,
                    options.BlockNGram,
                    options.BlockTopNGrams,
                    jitConfiguration);
                allResults.Add(sample);
                var extra = sample.CoremarkScore is null ? "" : $" iterations/sec={sample.CoremarkScore.Value:F2}";
                Console.WriteLine($"[runner]   {sample.Seconds:F3}s{extra}");
            }
        }

        var summaryPath = Path.Combine(resultsDir, "summary.json");
        var payload = new Dictionary<string, object?>
        {
            ["engine"] = options.Engine,
            ["rootfs"] = baseRootfs,
            ["aot_binary"] = options.Engine == "aot" ? aotBinary : null,
            ["repeat"] = options.Repeat,
            ["iterations"] = options.Iterations,
            ["cases"] = selectedCases,
            ["results"] = allResults.Select(result => result.ToDictionary()).ToList(),
        };
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(payload, JsonOptions), Utf8NoBom);

        if (options.AggregateSuperopcodeCandidates)
        {
            if (!options.JitHandlerProfileBlockDump)
            {
                Console.Error.WriteLine("--aggregate-superopcode-candidates requires --jit-handler-profile-block-dump");
                return 1;
            }

            if (options.BlockNGram <= 0)
            {
                Console.Error.WriteLine("--aggregate-superopcode-candidates requires --block-n-gram > 0");
                return 1;
            }

            var guestStatsRoot = Path.Combine(resultsDir, "guest-stats");
            var outputJson = Path.Combine(resultsDir, "superopcode_candidates.json");
            var outputMd = Path.Combine(resultsDir, "superopcode_candidates.md");
            RunSuperopcodeAggregation(
                projectRoot,
                guestStatsRoot,
                outputJson,
                outputMd,
                options.BlockNGram,
                options.CandidateTop);
        }

        PrintSummary(allResults);
        Console.WriteLine();
        Console.WriteLine($"[runner] summary={summaryPath}");
        return 0;
    }

    private static RunnerOptions ParseArgs(string[] args)
    {
        var options = new RunnerOptions
        {
            Rootfs = GetValue(args, "--rootfs") ?? DefaultRootfs().ToString(),
            ProjectRoot = GetValue(args, "--project-root") ?? RepoRoot(),
            Engine = GetValue(args, "--engine") ?? DefaultEngine,
            AotBinary = GetValue(args, "--aot-binary"),
            ResultsDir = GetValue(args, "--results-dir"),
            WorkDir = GetValue(args, "--work-dir") ?? Path.Combine(RepoRoot(), "benchmark", "podish_perf", "work"),
            Repeat = GetIntValue(args, "--repeat", 3),
            Iterations = GetIntValue(args, "--iterations", 3000),
            Timeout = GetIntValue(args, "--timeout", 1800),
            ReuseRootfs = HasFlag(args, "--reuse-rootfs"),
            KeepWorkdirs = HasFlag(args, "--keep-workdirs"),
            JitHandlerProfileBlockDump = HasFlag(args, "--jit-handler-profile-block-dump"),
            BlockNGram = GetIntValue(args, "--block-n-gram", 0),
            BlockTopNGrams = GetIntValue(args, "--block-top-ngrams", 100),
            AggregateSuperopcodeCandidates = HasFlag(args, "--aggregate-superopcode-candidates"),
            DisableSuperopcodes = HasFlag(args, "--disable-superopcodes"),
            AllowSuperopcodesInBlockAnalysis = HasFlag(args, "--allow-superopcodes-in-block-analysis"),
            SkipAutoAnalyzeBlockDump = HasFlag(args, "--skip-auto-analyze-block-dump"),
            CandidateTop = GetIntValue(args, "--candidate-top", 100),
        };

        var cases = GetMultiValue(args, "--case");
        if (cases.Count == 0)
            cases = new List<string>(DefaultCases);
        options.Cases = cases;

        if (!new HashSet<string>(["jit", "aot"], StringComparer.Ordinal).Contains(options.Engine))
            throw new ArgumentException("--engine must be one of: jit, aot");

        if (options.Cases.Any(caseName => !new HashSet<string>(DefaultCases, StringComparer.Ordinal).Contains(caseName)))
            throw new ArgumentException("--case must be one of: compress, compile, run");

        return options;
    }

    private static SampleResult RunSample(
        string projectRoot,
        string engine,
        string aotBinary,
        string baseRootfs,
        string caseName,
        int iteration,
        int timeoutSeconds,
        int iterations,
        string workDir,
        string resultsDir,
        bool reuseRootfs,
        bool keepWorkdirs,
        bool exportBlockDump,
        bool autoAnalyzeBlockDump,
        string fibercpuLibrary,
        int blockNGram,
        int blockTopNGrams,
        string jitConfiguration)
    {
        var workRootfs = CreateWorkRootfs(baseRootfs, caseName, iteration, workDir, reuseRootfs);
        var transcript = Path.Combine(resultsDir, $"{engine}-{caseName}-{iteration:02d}.log");
        string? guestStatsDir = null;
        if (exportBlockDump)
        {
            guestStatsDir = Path.Combine(resultsDir, "guest-stats", $"{engine}-{caseName}-{iteration:02d}");
            Directory.CreateDirectory(guestStatsDir);
        }

        var script = BuildGuestScript(caseName, iterations);
        var (program, arguments) = BuildEngineCommand(
            projectRoot,
            engine,
            aotBinary,
            workRootfs,
            script,
            jitConfiguration,
            guestStatsDir);

        var psi = new ProcessStartInfo(program)
        {
            WorkingDirectory = projectRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        foreach (var (key, value) in CleanEnv())
            psi.Environment[key] = value;

        var beginSeen = false;
        var endSeen = false;
        long beginStamp = 0;
        long endStamp = 0;
        var timedOutput = new StringBuilder();
        var logLock = new object();
        using var logWriter = new StreamWriter(File.Open(transcript, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };

        void WriteTranscript(string? line)
        {
            if (line is null)
                return;
            lock (logLock)
                logWriter.WriteLine(line);
        }

        void ProcessOutputLine(string? line, bool fromStdErr)
        {
            if (line is null)
                return;
            WriteTranscript(line);

            var trimmed = line.Trim();
            if (trimmed == MarkerBegin)
            {
                beginSeen = true;
                beginStamp = Stopwatch.GetTimestamp();
                return;
            }

            if (trimmed == MarkerEnd)
            {
                endSeen = true;
                endStamp = Stopwatch.GetTimestamp();
                return;
            }

            if (!fromStdErr && beginSeen && !endSeen)
                timedOutput.AppendLine(line);
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => ProcessOutputLine(e.Data, fromStdErr: false);
        process.ErrorDataReceived += (_, e) => ProcessOutputLine(e.Data, fromStdErr: true);

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start command: {program}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            TryKillProcessTree(process);
            throw new TimeoutException($"{caseName} iteration {iteration} timed out after {timeoutSeconds}s; see {transcript}");
        }

        process.WaitForExit();

        if (!beginSeen || !endSeen)
        {
            throw new InvalidOperationException(
                $"{caseName} iteration {iteration} did not emit benchmark markers; see {transcript}");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{caseName} iteration {iteration} failed with exit={process.ExitCode}; see {transcript}");
        }

        if (!keepWorkdirs && !reuseRootfs)
            Directory.Delete(workRootfs, recursive: true);

        string? blocksAnalysisJson = null;
        if (autoAnalyzeBlockDump && guestStatsDir is not null)
        {
            blocksAnalysisJson = RunBlockAnalysisWithOptions(
                projectRoot,
                guestStatsDir,
                fibercpuLibrary,
                nGram: blockNGram,
                topNgrams: blockTopNGrams);
        }

        var elapsedSeconds = (endStamp - beginStamp) / (double)Stopwatch.Frequency;
        return new SampleResult(
            engine,
            caseName,
            iteration,
            elapsedSeconds,
            transcript,
            workRootfs,
            ExtractCoremarkScore(timedOutput.ToString()),
            guestStatsDir,
            blocksAnalysisJson);
    }

    private static void PrintSummary(List<SampleResult> results)
    {
        var grouped = new Dictionary<(string Engine, string Case), List<SampleResult>>();
        foreach (var sample in results)
        {
            var key = (sample.Engine, sample.Case);
            if (!grouped.TryGetValue(key, out var list))
            {
                list = new List<SampleResult>();
                grouped[key] = list;
            }

            list.Add(sample);
        }

        Console.WriteLine();
        Console.WriteLine("Engine  Case      Samples  Min(s)  Median(s)  Mean(s)  Notes");
        Console.WriteLine("------  --------  -------  ------  ---------  -------  -----");
        foreach (var engine in new[] { "jit", "aot" })
        {
            foreach (var caseName in DefaultCases)
            {
                if (!grouped.TryGetValue((engine, caseName), out var samples) || samples.Count == 0)
                    continue;

                var durations = samples.Select(sample => sample.Seconds).ToList();
                var notes = "";
                var scores = samples.Where(sample => sample.CoremarkScore.HasValue).Select(sample => sample.CoremarkScore!.Value).ToList();
                if (scores.Count > 0)
                    notes = $"Iterations/Sec median={Median(scores):F2}";

                Console.WriteLine(
                    $"{engine,-6}  {caseName,-8}  {samples.Count,7}  {durations.Min(),6:F3}  {Median(durations),9:F3}  {durations.Average(),7:F3}  {notes}");
            }
        }
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0.0;

        var sorted = values.OrderBy(value => value).ToArray();
        var middle = sorted.Length / 2;
        if ((sorted.Length & 1) == 1)
            return sorted[middle];
        return (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static string BuildGuestScript(string caseName, int iterations)
    {
        var compileCommand = $"make PORT_DIR=linux ITERATIONS={iterations} XCFLAGS=\"-O3 -DPERFORMANCE_RUN=1\" REBUILD=1 compile";
        return caseName switch
        {
            "compress" => $"""
set -eu
rm -rf /tmp/coremark.tar /tmp/coremark.tar.gz /tmp/coremark-restored.tar /tmp/coremark-unpack
mkdir -p /tmp/coremark-unpack
sync >/dev/null 2>&1 || true
echo {MarkerBegin}
tar -C / -cf /tmp/coremark.tar coremark
gzip -1 -c /tmp/coremark.tar > /tmp/coremark.tar.gz
gzip -dc /tmp/coremark.tar.gz > /tmp/coremark-restored.tar
tar -C /tmp/coremark-unpack -xf /tmp/coremark-restored.tar
test -f /tmp/coremark-unpack/coremark/Makefile
echo {MarkerEnd}
""",
            "compile" => $"""
set -eu
cd /coremark
make clean >/dev/null 2>&1 || true
sync >/dev/null 2>&1 || true
echo {MarkerBegin}
{compileCommand}
test -x /coremark/coremark.exe
echo {MarkerEnd}
""",
            "run" => $"""
set -eu
cd /coremark
test -x ./coremark.exe || {compileCommand} >/dev/null
sync >/dev/null 2>&1 || true
echo {MarkerBegin}
./coremark.exe 0x0 0x0 0x66 {iterations}
echo {MarkerEnd}
""",
            _ => throw new ArgumentException($"unknown case: {caseName}")
        };
    }

    private static (string Program, List<string> Args) BuildEngineCommand(
        string projectRoot,
        string engine,
        string aotBinary,
        string rootfs,
        string script,
        string jitConfiguration = "Release",
        string? guestStatsDir = null)
    {
        var podishArgs = new List<string>
        {
            "run",
            "--rm",
            "--rootfs",
            rootfs,
            "--",
            "/bin/sh",
            "-lc",
            script,
        };
        if (!string.IsNullOrWhiteSpace(guestStatsDir))
            podishArgs.InsertRange(1, new[] { "--guest-stats-dir", guestStatsDir! });

        if (engine == "jit")
        {
            return (
                "dotnet",
                new List<string>
                {
                    "run",
                    "--project",
                    Path.Combine(projectRoot, "Podish.Cli", "Podish.Cli.csproj"),
                    "-c",
                    jitConfiguration,
                    "--no-build",
                    "--",
                }.Concat(podishArgs).ToList());
        }

        if (engine == "aot")
            return (aotBinary, podishArgs);

        throw new ArgumentException($"unknown engine: {engine}");
    }

    private static string DefaultAotBinary(string projectRoot)
        => Path.Combine(projectRoot, "build", "nativeaot", "podish-cli-static", "Podish.Cli");

    private static string DefaultJitConfiguration(string projectRoot)
    {
        var releaseBinary = Path.Combine(projectRoot, "Podish.Cli", "bin", "Release", "net10.0", "Podish.Cli");
        var debugBinary = Path.Combine(projectRoot, "Podish.Cli", "bin", "Debug", "net10.0", "Podish.Cli");
        if (File.Exists(releaseBinary))
            return "Release";
        if (File.Exists(debugBinary))
            return "Debug";
        return "Release";
    }

    private static string DefaultFibercpuLibrary(string projectRoot, string? jitConfiguration = null)
    {
        if (!string.IsNullOrWhiteSpace(jitConfiguration))
        {
            var cliDir = Path.Combine(projectRoot, "Podish.Cli", "bin", jitConfiguration, "net10.0");
            foreach (var name in new[] { "libfibercpu.dylib", "libfibercpu.so", "fibercpu.dll" })
            {
                var candidate = Path.Combine(cliDir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        var hostDir = Path.Combine(projectRoot, "Fiberish.X86", "build_native", "host");
        foreach (var name in new[] { "libfibercpu.dylib", "libfibercpu.so", "fibercpu.dll" })
        {
            var candidate = Path.Combine(hostDir, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(hostDir, "libfibercpu.dylib");
    }

    private static string DefaultRootfs()
        => Path.Combine(RepoRoot(), "benchmark", "podish_perf", "rootfs", "coremark_i386_alpine");

    private static void EnsurePerfToolsBuild(string projectRoot)
    {
        var cmd = new[]
        {
            "dotnet",
            "build",
            Path.Combine(projectRoot, "Podish.PerfTools", "Podish.PerfTools.csproj"),
            "-c",
            "Release",
            "--no-restore",
        };
        Console.WriteLine($"[runner] building perf tools: {string.Join(" ", cmd.Select(ShellQuote))}");
        RunProcess(cmd[0], cmd.Skip(1), projectRoot);
    }

    private static void RunBlockAnalysis(
        string projectRoot,
        string guestStatsDir,
        string fibercpuLibrary,
        int nGram,
        int topNgrams)
        => RunBlockAnalysisWithOptions(projectRoot, guestStatsDir, fibercpuLibrary, nGram, topNgrams);

    private static string RunBlockAnalysisWithOptions(
        string projectRoot,
        string guestStatsDir,
        string fibercpuLibrary,
        int nGram,
        int topNgrams)
    {
        var outputPath = Path.Combine(guestStatsDir, "blocks_analysis.json");
        var cmd = new List<string>
        {
            "analyze-blocks",
            "--input",
            guestStatsDir,
            "--lib",
            fibercpuLibrary,
            "--output",
            outputPath,
        };
        if (nGram > 0)
        {
            cmd.Add("--n-gram");
            cmd.Add(nGram.ToString(CultureInfo.InvariantCulture));
            cmd.Add("--top-ngrams");
            cmd.Add(topNgrams.ToString(CultureInfo.InvariantCulture));
        }

        var result = PerfToolsRun(projectRoot, cmd[0], cmd.Skip(1).ToArray());
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"block analysis failed with exit code {result.ExitCode}");
        return outputPath;
    }

    private static (string OutputJson, string OutputMd) RunSuperopcodeAggregation(
        string projectRoot,
        string inputDir,
        string outputJson,
        string outputMd,
        int nGram,
        int topCandidates)
    {
        var cmd = new List<string>
        {
            "analyze-superopcode-candidates",
            inputDir,
            "--n-gram",
            nGram.ToString(CultureInfo.InvariantCulture),
            "--top",
            topCandidates.ToString(CultureInfo.InvariantCulture),
            "--output-json",
            outputJson,
            "--output-md",
            outputMd,
        };
        var result = PerfToolsRun(projectRoot, cmd[0], cmd.Skip(1).ToArray());
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"superopcode aggregation failed with exit code {result.ExitCode}");
        return (outputJson, outputMd);
    }

    private static void RunProcess(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"command failed with exit code {proc.ExitCode}: {fileName} {string.Join(" ", arguments)}");
    }

    private static ProcessResult PerfToolsRun(string projectRoot, string command, IReadOnlyList<string> extraArgs)
    {
        var cmd = new List<string>
        {
            "dotnet",
            "run",
            "--project",
            Path.Combine(projectRoot, "Podish.PerfTools", "Podish.PerfTools.csproj"),
            "-c",
            "Release",
            "--no-build",
            "--no-restore",
            "--",
            command,
        };
        cmd.AddRange(extraArgs);
        Console.WriteLine($"[runner] perf tools: {string.Join(" ", cmd.Select(ShellQuote))}");
        return RunProcessWithResult(cmd[0], cmd.Skip(1).ToArray(), projectRoot);
    }

    private static ProcessResult RunProcessWithResult(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        proc.WaitForExit();
        return new ProcessResult(proc.ExitCode);
    }

    private static bool CommandExists(string command)
    {
        try
        {
            var result = RunProcessWithResult("bash", new[] { "-lc", $"command -v {EscapeShellSingleArgument(command)} >/dev/null 2>&1" }, RepoRoot());
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeShellSingleArgument(string value)
        => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best effort
        }
    }

    private static Dictionary<string, string> CleanEnv()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TERM"] = Environment.GetEnvironmentVariable("TERM") ?? "xterm",
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["DOTNET_CLI_HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "true",
            ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
            ["DOTNET_NOLOGO"] = "true",
        };
        var debug = Environment.GetEnvironmentVariable("PODISH_GUEST_STATS_DEBUG");
        if (!string.IsNullOrWhiteSpace(debug))
            env["PODISH_GUEST_STATS_DEBUG"] = debug;
        return env;
    }

    private static string CreateWorkRootfs(string baseRootfs, string caseName, int iteration, string workDir, bool reuseRootfs)
    {
        if (reuseRootfs)
            return baseRootfs;

        var prefix = $"{caseName}-{iteration}-";
        var workRootfs = Path.Combine(workDir, prefix + Path.GetRandomFileName());
        Directory.CreateDirectory(workRootfs);
        Directory.Delete(workRootfs);
        if (OperatingSystem.IsWindows())
        {
            CopyDirectory(baseRootfs, workRootfs);
        }
        else
        {
            RunProcess("cp", new[] { "-a", $"{baseRootfs}/.", workRootfs }, RepoRoot());
        }
        return workRootfs;
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, directory);
            Directory.CreateDirectory(Path.Combine(targetDir, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var targetFile = Path.Combine(targetDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: true);
        }
    }

    private static double? ExtractCoremarkScore(string output)
    {
        var match = IterationsPerSecRegex.Match(output);
        if (match.Success)
            return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        match = CoreMarkScoreRegex.Match(output);
        if (match.Success)
            return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return null;
    }

    private static string RepoRoot()
    {
        var start = Path.GetDirectoryName(Path.GetFullPath(typeof(BenchmarkRunner).Assembly.Location)) ?? AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(start))
        {
            if (File.Exists(Path.Combine(start, "Podish.slnx")) ||
                File.Exists(Path.Combine(start, "Podish.sln")) ||
                File.Exists(Path.Combine(start, "Podish.Cli", "Podish.Cli.csproj")))
            {
                return start;
            }
            var parent = Directory.GetParent(start);
            if (parent is null)
                break;
            start = parent.FullName;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
    }

    private static string ShellQuote(string value)
    {
        if (value.Length == 0)
            return "''";
        if (value.All(ch => char.IsLetterOrDigit(ch) || "/._-:=+".Contains(ch, StringComparison.Ordinal)))
            return value;
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static string? GetValue(string[] args, string option)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == option)
                return args[i + 1];
        }
        return null;
    }

    private static int GetIntValue(string[] args, string option, int defaultValue)
        => int.TryParse(GetValue(args, option), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : defaultValue;

    private static bool HasFlag(string[] args, string flag)
        => args.Contains(flag, StringComparer.Ordinal);

    private static List<string> GetMultiValue(string[] args, string option)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == option)
                values.Add(args[i + 1]);
        }
        return values;
    }

    private sealed class RunnerOptions
    {
        public string ProjectRoot { get; set; } = RepoRoot();
        public string Rootfs { get; set; } = DefaultRootfs();
        public string Engine { get; set; } = DefaultEngine;
        public string? AotBinary { get; set; }
        public string? ResultsDir { get; set; }
        public string WorkDir { get; set; } = Path.Combine(RepoRoot(), "benchmark", "podish_perf", "work");
        public List<string> Cases { get; set; } = [];
        public int Repeat { get; set; } = 3;
        public int Iterations { get; set; } = 3000;
        public int Timeout { get; set; } = 1800;
        public bool ReuseRootfs { get; set; }
        public bool KeepWorkdirs { get; set; }
        public bool JitHandlerProfileBlockDump { get; set; }
        public int BlockNGram { get; set; }
        public int BlockTopNGrams { get; set; } = 100;
        public bool AggregateSuperopcodeCandidates { get; set; }
        public bool DisableSuperopcodes { get; set; }
        public bool AllowSuperopcodesInBlockAnalysis { get; set; }
        public bool SkipAutoAnalyzeBlockDump { get; set; }
        public int CandidateTop { get; set; } = 100;
    }

    private sealed record SampleResult(
        string Engine,
        string Case,
        int Iteration,
        double Seconds,
        string Transcript,
        string WorkRootfs,
        double? CoremarkScore,
        string? GuestStatsDir,
        string? BlocksAnalysisJson)
    {
        public Dictionary<string, object?> ToDictionary() => new()
        {
            ["engine"] = Engine,
            ["case"] = Case,
            ["iteration"] = Iteration,
            ["seconds"] = Seconds,
            ["transcript"] = Transcript,
            ["work_rootfs"] = WorkRootfs,
            ["coremark_score"] = CoremarkScore,
            ["guest_stats_dir"] = GuestStatsDir,
            ["blocks_analysis_json"] = BlocksAnalysisJson,
        };
    }

    private sealed record ProcessResult(int ExitCode);
}
