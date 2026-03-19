using System.Runtime.InteropServices;

namespace Podish.PerfTools;

internal static partial class Program
{
    private static int RunProfile(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException(
                "Missing profile subcommand. Expected one of: record, analyze, record-and-analyze, compare");

        return args[0] switch
        {
            "record" => RunProfileRecord(args[1..]),
            "analyze" => RunProfileAnalyze(args[1..]),
            "record-and-analyze" => RunProfileRecordAndAnalyze(args[1..]),
            "compare" => RunProfileCompare(args[1..]),
            _ => throw new ArgumentException($"Unknown profile subcommand: {args[0]}")
        };
    }

    private static int RunProfileRecord(string[] args)
    {
        var options = ParseProfileRecordOptions(args);
        var outDir = MakeOutputDir(options.OutputDir, options.Name);
        var runBinary = MakeUniqueBinary(options.BinaryPath, outDir, options.RenamedBinary);
        var tracePath = GetTracePath(outDir, options.Name, options.BackendKind);
        WriteRecordCommand(outDir, options, runBinary, tracePath);

        var env = BuildProfileEnvironment(options);
        options.Backend.Record(options, runBinary, tracePath, env);
        Console.WriteLine(tracePath);
        return 0;
    }

    private static int RunProfileAnalyze(string[] args)
    {
        var options = ParseProfileAnalyzeOptions(args);
        var outDir = MakeOutputDir(options.OutputDir, options.Name);
        var analysis = options.Backend.Analyze(options, outDir);
        var topHotspots = analysis.Hotspots.Take(options.Top).ToList();
        var topSymbols = topHotspots.Take(options.DisasmTop).Select(h => h.Symbol).ToList();
        var disassembly = topSymbols.Count > 0
            ? DisassembleSymbols(options.BinaryPath, topSymbols, outDir)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var written = WriteProfileReport(outDir, options.Name, analysis, options.BinaryPath, disassembly);
        Console.WriteLine(written.ReportJsonPath);
        Console.WriteLine(written.ReportMarkdownPath);
        return 0;
    }

    private static int RunProfileRecordAndAnalyze(string[] args)
    {
        var recordOptions = ParseProfileRecordOptions(args);
        var outDir = MakeOutputDir(recordOptions.OutputDir, recordOptions.Name);
        var runBinary = MakeUniqueBinary(recordOptions.BinaryPath, outDir, recordOptions.RenamedBinary);
        var tracePath = GetTracePath(outDir, recordOptions.Name, recordOptions.BackendKind);
        WriteRecordCommand(outDir, recordOptions, runBinary, tracePath);

        var env = BuildProfileEnvironment(recordOptions);
        recordOptions.Backend.Record(recordOptions, runBinary, tracePath, env);

        var analyzeOptions = ParseProfileAnalyzeOptions(args, tracePath, runBinary, outDir);
        var analysis = analyzeOptions.Backend.Analyze(analyzeOptions, outDir);
        var topHotspots = analysis.Hotspots.Take(analyzeOptions.Top).ToList();
        var topSymbols = topHotspots.Take(analyzeOptions.DisasmTop).Select(h => h.Symbol).ToList();
        var disassembly = topSymbols.Count > 0
            ? DisassembleSymbols(runBinary, topSymbols, outDir)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var written = WriteProfileReport(outDir, analyzeOptions.Name, analysis, runBinary, disassembly);
        Console.WriteLine(written.ReportJsonPath);
        Console.WriteLine(written.ReportMarkdownPath);
        return 0;
    }

    private static int RunProfileCompare(string[] args)
    {
        var reports = GetMultiValue(args, "--report");
        if (reports.Count == 0)
            reports = GetPositionalArgs(args);
        if (reports.Count == 0)
            throw new ArgumentException("Missing --report values for profile compare");

        var top = GetIntValue(args, "--top", 25);
        var output = GetValue(args, "--output");
        var content = CompareProfileReports(reports.Select(Path.GetFullPath).ToList(), top,
            output is null ? null : Path.GetFullPath(output));
        Console.Write(content);
        return 0;
    }

    private static ProfileRecordOptions ParseProfileRecordOptions(string[] args)
    {
        var backendKind = ResolveProfileBackendKind(GetValue(args, "--backend") ?? "auto");
        var outputDir = Path.GetFullPath(GetValue(args, "--output-dir") ??
                                         Path.Combine(RepoRoot(), "benchmark", "podish_perf", "results"));
        var name = GetValue(args, "--name") ?? "coremark-profile";
        var timeLimitSeconds = GetIntValue(args, "--time-limit", 18);
        var iterations = GetIntValue(args, "--iterations", 30000);
        var benchCase = GetValue(args, "--bench-case") ?? "run";
        var renamedBinary = GetValue(args, "--renamed-binary") ?? "PodishCliProfile";
        var rootfs = Path.GetFullPath(GetValue(args, "--rootfs") ??
                                      Path.Combine(RepoRoot(), "benchmark", "podish_perf", "rootfs",
                                          "coremark_i386_alpine"));
        var binaryPath = Path.GetFullPath(GetValue(args, "--binary") ?? DefaultProfileBinary());
        var jitMapDir = GetValue(args, "--jit-map-dir");
        var backend = CreateProfileBackend(backendKind);

        if (string.IsNullOrWhiteSpace(benchCase) ||
            !new HashSet<string>(new[] { "run", "compile", "compress", "gcc_compile" }, StringComparer.Ordinal)
                .Contains(benchCase))
            throw new ArgumentException("--bench-case must be one of: run, compile, compress, gcc_compile");

        RefreshDefaultProfileBinary(binaryPath);
        return new ProfileRecordOptions(
            backendKind,
            backend,
            binaryPath,
            rootfs,
            outputDir,
            name,
            timeLimitSeconds,
            iterations,
            benchCase,
            renamedBinary,
            jitMapDir is null ? null : Path.GetFullPath(jitMapDir));
    }

    private static ProfileAnalyzeOptions ParseProfileAnalyzeOptions(
        string[] args,
        string? tracePathOverride = null,
        string? binaryOverride = null,
        string? outputDirOverride = null)
    {
        var tracePath = Path.GetFullPath(tracePathOverride ?? RequireValue(args, "--trace"));
        var backendKind = ResolveAnalyzeBackendKind(GetValue(args, "--backend") ?? "auto", tracePath);
        var outputDir = Path.GetFullPath(outputDirOverride ?? GetValue(args, "--output-dir") ??
            Path.Combine(RepoRoot(), "benchmark", "podish_perf", "results"));
        var name = GetValue(args, "--name") ?? Path.GetFileNameWithoutExtension(tracePath);
        if (name.EndsWith(".trace", StringComparison.OrdinalIgnoreCase))
            name = Path.GetFileNameWithoutExtension(name);
        var warmupSeconds = GetDoubleValue(args, "--warmup-seconds", 4.0);
        var top = GetIntValue(args, "--top", 25);
        var disasmTop = GetIntValue(args, "--disasm-top", 8);
        var jitMapDir = GetValue(args, "--jit-map-dir");
        var binaryPath = Path.GetFullPath(binaryOverride ?? GetValue(args, "--binary") ?? DefaultProfileBinary());
        var backend = CreateProfileBackend(backendKind);
        return new ProfileAnalyzeOptions(
            backendKind,
            backend,
            tracePath,
            binaryPath,
            outputDir,
            name,
            warmupSeconds,
            top,
            disasmTop,
            jitMapDir is null ? null : Path.GetFullPath(jitMapDir));
    }

    private static string DefaultProfileBinary()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Path.Combine(RepoRoot(), "build", "nativeaot", "podish-cli-static", "Podish.Cli");
        return Path.Combine(RepoRoot(), "Podish.Cli", "bin", "Debug", "net10.0", "Podish.Cli");
    }

    private static void RefreshDefaultProfileBinary(string binaryPath)
    {
        var resolved = Path.GetFullPath(binaryPath);
        var defaultMacAot =
            Path.GetFullPath(Path.Combine(RepoRoot(), "build", "nativeaot", "podish-cli-static", "Podish.Cli"));
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
            !string.Equals(resolved, defaultMacAot, StringComparison.Ordinal))
            return;

        RunCommandChecked(
            "dotnet",
            new[]
            {
                "publish",
                "Podish.Cli/Podish.Cli.csproj",
                "-c",
                "Release",
                "-r",
                "osx-arm64",
                "-p:PublishAot=true",
                "-o",
                Path.GetDirectoryName(defaultMacAot)!
            },
            RepoRoot());
    }

    private static ProfileBackendKind ResolveProfileBackendKind(string backend)
    {
        return backend switch
        {
            "auto" => RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? ProfileBackendKind.XcTrace
                : ProfileBackendKind.Perf,
            "xctrace" => ProfileBackendKind.XcTrace,
            "perf" => ProfileBackendKind.Perf,
            _ => throw new ArgumentException("--backend must be one of: auto, xctrace, perf")
        };
    }

    private static ProfileBackendKind ResolveAnalyzeBackendKind(string backend, string tracePath)
    {
        if (!string.Equals(backend, "auto", StringComparison.Ordinal))
            return ResolveProfileBackendKind(backend);
        if (tracePath.EndsWith(".trace", StringComparison.OrdinalIgnoreCase))
            return ProfileBackendKind.XcTrace;
        if (tracePath.EndsWith(".data", StringComparison.OrdinalIgnoreCase))
            return ProfileBackendKind.Perf;
        return ResolveProfileBackendKind("auto");
    }

    private static IProfileBackend CreateProfileBackend(ProfileBackendKind backendKind)
    {
        return backendKind switch
        {
            ProfileBackendKind.XcTrace => new XcTraceProfileBackend(),
            ProfileBackendKind.Perf => new PerfProfileBackend(),
            _ => throw new ArgumentOutOfRangeException(nameof(backendKind), backendKind, null)
        };
    }
}