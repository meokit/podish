using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LibObjectFile.Elf;

namespace Podish.PerfTools;

internal static class Program
{
    private static readonly string[] GprNames = ["eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi"];
    private const uint CfBit = 1u << 0;
    private const uint PfBit = 1u << 2;
    private const uint AfBit = 1u << 4;
    private const uint ZfBit = 1u << 6;
    private const uint SfBit = 1u << 7;
    private const uint OfBit = 1u << 11;
    private const uint AllStatusFlagsMask = CfBit | PfBit | AfBit | ZfBit | SfBit | OfBit;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly Regex DispatchWrapperRegex =
        new(@"DispatchWrapper<&(?:[a-zA-Z0-9_]+::)*?(op::Op[A-Za-z0-9_]+)>", RegexOptions.Compiled);

    private static readonly Regex DirectLogicRegex =
        new(@"(?:^|::)(op::Op[A-Za-z0-9_]+)(?:\(|$)", RegexOptions.Compiled);

    private static readonly Regex MangledLogicRegex =
        new(@"(Op[A-Za-z0-9_]+?)EPNS_", RegexOptions.Compiled);

    private static readonly Regex OpPrefixRegex =
        new(@"^Op[A-Za-z0-9_]+$", RegexOptions.Compiled);

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0];
        var rest = args.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "analyze-blocks" => RunAnalyzeBlocks(rest),
                "analyze-superopcode-candidates" => RunAnalyzeSuperopcodeCandidates(rest),
                "gen-superopcodes" => RunGenerateSuperopcodes(rest),
                "pipeline" => RunPipeline(rest),
                "runner" => BenchmarkRunner.Run(rest),
                "profile" => RunProfile(rest),
                "help" or "--help" or "-h" => PrintUsage(),
                _ => throw new ArgumentException($"Unknown command: {command}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[perf-tools] {ex.Message}");
            return 1;
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine("""
Podish.PerfTools

Commands:
  analyze-blocks                 Parse a blocks.bin dump into blocks_analysis.json
  analyze-superopcode-candidates Aggregate candidate pairs from analysis files
  gen-superopcodes               Generate libfibercpu/generated/superopcodes.generated.cpp
  pipeline                       Run the full benchmark + analysis pipeline
  runner                         Run the benchmark harness and optional block analysis
  profile                        Record/analyze runtime profiles with auto-selected backend

Examples:
  dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- pipeline --candidate-top 256 --superopcode-top 256 --reuse-rootfs
  dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- runner --jit-handler-profile-block-dump --aggregate-superopcode-candidates --candidate-top 256
  dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- profile record-and-analyze --backend auto
""");
        return 0;
    }

    private static int RunAnalyzeBlocks(string[] args)
    {
        var input = RequireValue(args, "--input", positionalIndex: 0);
        var libPath = RequireValue(args, "--lib", positionalIndex: 1);
        var output = GetValue(args, "--output") ?? DefaultAnalysisOutput(input);
        var nGram = GetIntValue(args, "--n-gram", 2);
        var topNgrams = GetIntValue(args, "--top-ngrams", 100);

        var result = AnalyzeBlocks(input, libPath, nGram, topNgrams);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, JsonOptions), Utf8NoBom);
        Console.WriteLine($"Wrote analysis to {Path.GetFullPath(output)}");
        return 0;
    }

    private static int RunAnalyzeSuperopcodeCandidates(string[] args)
    {
        var inputs = GetMultiValue(args, "--input");
        if (inputs.Count == 0)
        {
            inputs = GetPositionalArgs(args);
        }

        var nGram = GetIntValue(args, "--n-gram", 2);
        if (nGram != 2)
            throw new InvalidOperationException("--n-gram must remain 2");

        var top = GetIntValue(args, "--top", 100);
        var scoreBasis = GetValue(args, "--score-basis") ?? "pair";
        if (scoreBasis is not ("anchor" or "pair"))
            throw new InvalidOperationException("--score-basis must be one of: anchor, pair");
        var rawWeight = GetIntValue(args, "--raw-weight", 2);
        var rarWeight = GetIntValue(args, "--rar-weight", 0);
        var wawWeight = GetIntValue(args, "--waw-weight", 0);
        var jccMultiplier = GetIntValue(args, "--jcc-multiplier", 1);
        var jccMode = GetValue(args, "--jcc-mode") ?? "none";
        if (jccMode is not ("none" or "pair" or "raw-only"))
            throw new InvalidOperationException("--jcc-mode must be one of: none, pair, raw-only");
        var anchorTop = GetIntValue(args, "--anchor-top", 64);
        var minSamples = GetIntValue(args, "--min-samples", 1);
        var minWeightedExec = GetIntValue(args, "--min-weighted-exec-count", 0);
        var outputJson = RequireValue(args, "--output-json");
        var outputMd = GetValue(args, "--output-md");

        var analysisFiles = DiscoverAnalysisFiles(inputs);
        if (analysisFiles.Count == 0)
            throw new InvalidOperationException("No blocks_analysis.json files found under the provided inputs");

        var aggregateAnchors = new Dictionary<string, AnchorAggregate>(StringComparer.Ordinal);
        var aggregatePairs = new Dictionary<(string, string), PairAggregate>();
        var includedSamples = new List<Dictionary<string, object?>>();
        var skippedSamples = new List<Dictionary<string, object?>>();

        foreach (var analysisFile in analysisFiles)
        {
            var data = JsonDocument.Parse(File.ReadAllText(analysisFile, Encoding.UTF8));
            var sampleMeta = InferSampleMetadata(analysisFile);
        if (ShouldSkipAnalysis(data.RootElement, out var skipReasons))
        {
                skippedSamples.Add(new Dictionary<string, object?>
                {
                    ["analysis_file"] = analysisFile.ToString(),
                    ["reasons"] = skipReasons
                });
                continue;
            }

            if (!data.RootElement.TryGetProperty("blocks", out var blocksNode) || blocksNode.ValueKind != JsonValueKind.Array)
            {
                skippedSamples.Add(new Dictionary<string, object?>
                {
                    ["analysis_file"] = analysisFile.ToString(),
                    ["reasons"] = new[] { "blocks list is empty" }
                });
                continue;
            }

            var sampleAnchors = new Dictionary<string, SampleAnchorStats>(StringComparer.Ordinal);
            var samplePairs = new Dictionary<(string, string), SamplePairStats>();
            AnalyzeSampleCandidates(blocksNode, sampleAnchors, samplePairs);

            if (samplePairs.Count == 0)
            {
                skippedSamples.Add(new Dictionary<string, object?>
                {
                    ["analysis_file"] = analysisFile.ToString(),
                    ["reasons"] = new[] { "no def-use-adjacent 2-op candidates found in blocks" }
                });
                continue;
            }

            includedSamples.Add(sampleMeta);
            foreach (var (anchor, sampleStats) in sampleAnchors)
                MergeAnchorStats(aggregateAnchors, anchor, sampleMeta, sampleStats);
            foreach (var (pair, sampleStats) in samplePairs)
                MergePairStats(aggregatePairs, pair, sampleMeta, sampleStats);
        }

        var anchors = aggregateAnchors.Values
            .Select(NormalizeAnchorEntry)
            .OrderByDescending(e => e["weighted_exec_count"])
            .ThenByDescending(e => e["sample_count"])
            .ThenByDescending(e => e["occurrences"])
            .ThenByDescending(e => e["unique_block_count"])
            .ToList();

        var anchorIndex = anchors.ToDictionary(a => (string)a["anchor"]!, a => a, StringComparer.Ordinal);
        var candidates = new List<Dictionary<string, object?>>();
        foreach (var entry in aggregatePairs.Values)
        {
            var anchorName = entry.AnchorHandler;
            if (!anchorIndex.TryGetValue(anchorName, out var anchorEntry))
                continue;

            if (entry.SampleCount < minSamples || entry.WeightedExecCount < minWeightedExec)
                continue;

            candidates.Add(NormalizeCandidateEntry(
                entry,
                anchorEntry,
                scoreBasis,
                rawWeight,
                rarWeight,
                wawWeight,
                jccMultiplier,
                jccMode));
        }

        candidates = candidates
            .OrderByDescending(CandidateSortKey)
            .ToList();

        if (candidates.Count > top)
            candidates = candidates.Take(top).ToList();

        var output = new Dictionary<string, object?>
        {
            ["metadata"] = new Dictionary<string, object?>
            {
                ["inputs"] = inputs.Select(Path.GetFullPath).ToArray(),
                ["strategy"] = "global-score-anchor-freq-times-dep-weight",
                ["analysis_file_count"] = analysisFiles.Count,
                ["included_sample_count"] = includedSamples.Count,
                ["skipped_sample_count"] = skippedSamples.Count,
                ["candidate_count"] = candidates.Count,
                ["anchor_count"] = anchors.Count,
                ["anchor_top_limit"] = anchorTop,
                ["score_basis"] = scoreBasis,
                ["raw_weight"] = rawWeight,
                ["rar_weight"] = rarWeight,
                ["waw_weight"] = wawWeight,
                ["jcc_multiplier"] = jccMultiplier,
                ["jcc_mode"] = jccMode,
                ["min_samples"] = minSamples,
                ["min_weighted_exec_count"] = minWeightedExec,
                ["top_limit"] = top,
                ["superopcode_width"] = 2,
                ["selected_relation_kind_counts"] = candidates
                    .GroupBy(c => Convert.ToString(c["relation_kind"], CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)
            },
            ["included_samples"] = includedSamples,
            ["skipped_samples"] = skippedSamples,
            ["anchors"] = anchors.Take(anchorTop).ToList(),
            ["candidates"] = candidates,
        };

        File.WriteAllText(outputJson, JsonSerializer.Serialize(output, JsonOptions), Utf8NoBom);
        Console.WriteLine($"Wrote {candidates.Count} candidates from {includedSamples.Count} samples to {Path.GetFullPath(outputJson)}");
        if (!string.IsNullOrWhiteSpace(outputMd))
        {
            File.WriteAllText(
                outputMd,
                BuildMarkdown(
                    inputs,
                    analysisFiles,
                    includedSamples,
                    skippedSamples,
                    anchors.Take(Math.Min(anchors.Count, Math.Max(20, anchorTop))).ToList(),
                    candidates,
                    anchorTop),
                Utf8NoBom);
            Console.WriteLine($"Wrote markdown summary to {Path.GetFullPath(outputMd)}");
        }

        return 0;
    }

    private static int RunGenerateSuperopcodes(string[] args)
    {
        var input = RequireValue(args, "--input", positionalIndex: 0);
        var output = RequireValue(args, "--output", positionalIndex: 1);
        var top = GetIntValue(args, "--top", 32);

        var generated = GenerateSuperopcodes(input, top);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, generated, Utf8NoBom);
        Console.WriteLine($"Wrote {top} superopcodes to {Path.GetFullPath(output)}");
        return 0;
    }

    private static int RunPipeline(string[] args)
    {
        var projectRoot = Path.GetFullPath(GetValue(args, "--project-root") ?? RepoRoot());
        var rootfs = Path.GetFullPath(GetValue(args, "--rootfs") ?? Path.Combine(projectRoot, "benchmark", "podish_perf", "rootfs", "coremark_i386_alpine"));
        var candidateTop = GetIntValue(args, "--candidate-top", 100);
        var superopcodeTop = GetIntValue(args, "--superopcode-top", 32);
        var iterations = GetIntValue(args, "--iterations", 3000);
        var repeat = GetIntValue(args, "--repeat", 1);
        var timeout = GetIntValue(args, "--timeout", 1800);
        var resultsDir = GetValue(args, "--results-dir");
        var workDir = GetValue(args, "--work-dir");
        var generatedOutput = GetValue(args, "--generated-output") ?? "libfibercpu/generated/superopcodes.generated.cpp";
        var reuseRootfs = HasFlag(args, "--reuse-rootfs");
        var keepWorkdirs = HasFlag(args, "--keep-workdirs");
        var skipVerifyBuild = HasFlag(args, "--skip-verify-build");

        var cases = GetMultiValue(args, "--case");
        if (cases.Count == 0)
            cases = new List<string> { "compress", "compile", "run" };

        if (string.IsNullOrWhiteSpace(resultsDir))
            resultsDir = Path.Combine(projectRoot, "benchmark", "podish_perf", "results", $"{DateTime.Now:yyyyMMdd-HHmmss}-superopcode");
        resultsDir = Path.GetFullPath(resultsDir);

        var runnerArgs = new List<string>
        {
            "--engine", "jit",
            "--jit-handler-profile-block-dump",
            "--disable-superopcodes",
            "--block-n-gram", "2",
            "--aggregate-superopcode-candidates",
            "--candidate-top", candidateTop.ToString(CultureInfo.InvariantCulture),
            "--rootfs", rootfs,
            "--repeat", repeat.ToString(CultureInfo.InvariantCulture),
            "--iterations", iterations.ToString(CultureInfo.InvariantCulture),
            "--timeout", timeout.ToString(CultureInfo.InvariantCulture),
            "--results-dir", resultsDir,
        };
        foreach (var c in cases)
            runnerArgs.AddRange(new[] { "--case", c });
        if (!string.IsNullOrWhiteSpace(workDir))
            runnerArgs.AddRange(new[] { "--work-dir", Path.GetFullPath(workDir) });
        if (reuseRootfs)
            runnerArgs.Add("--reuse-rootfs");
        if (keepWorkdirs)
            runnerArgs.Add("--keep-workdirs");

        var runnerExit = BenchmarkRunner.Run(runnerArgs.ToArray());
        if (runnerExit != 0)
            throw new InvalidOperationException($"runner failed with exit code {runnerExit}");

        var candidateJson = Path.Combine(resultsDir, "superopcode_candidates.json");
        var candidateMd = Path.Combine(resultsDir, "superopcode_candidates.md");

        var generatedOutputPath = Path.GetFullPath(Path.Combine(projectRoot, generatedOutput));
        RunGenerateSuperopcodes(new[]
        {
            "--input", candidateJson,
            "--output", generatedOutputPath,
            "--top", superopcodeTop.ToString(CultureInfo.InvariantCulture)
        });

        if (!skipVerifyBuild)
        {
            RunProcess("dotnet", new[]
            {
                "build",
                Path.Combine(projectRoot, "Podish.Cli", "Podish.Cli.csproj"),
                "-c", "Release",
                "-p:EnableHandlerProfile=true",
                "-p:EnableSuperOpcodes=true"
            }, projectRoot);
        }

        Console.WriteLine($"[superopcode] results_dir={resultsDir}");
        Console.WriteLine($"[superopcode] candidate_json={candidateJson}");
        Console.WriteLine($"[superopcode] generated_output={generatedOutputPath}");
        return 0;
    }

    private static int RunProfile(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("Missing profile subcommand. Expected one of: record, analyze, record-and-analyze, compare");

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
        var disassembly = topSymbols.Count > 0 ? DisassembleSymbols(options.BinaryPath, topSymbols, outDir) : new Dictionary<string, string>(StringComparer.Ordinal);
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

        var analyzeOptions = ParseProfileAnalyzeOptions(args, tracePathOverride: tracePath, binaryOverride: runBinary, outputDirOverride: outDir);
        var analysis = analyzeOptions.Backend.Analyze(analyzeOptions, outDir);
        var topHotspots = analysis.Hotspots.Take(analyzeOptions.Top).ToList();
        var topSymbols = topHotspots.Take(analyzeOptions.DisasmTop).Select(h => h.Symbol).ToList();
        var disassembly = topSymbols.Count > 0 ? DisassembleSymbols(runBinary, topSymbols, outDir) : new Dictionary<string, string>(StringComparer.Ordinal);
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
        var content = CompareProfileReports(reports.Select(Path.GetFullPath).ToList(), top, output is null ? null : Path.GetFullPath(output));
        Console.Write(content);
        return 0;
    }

    private static ProfileRecordOptions ParseProfileRecordOptions(string[] args)
    {
        var backendKind = ResolveProfileBackendKind(GetValue(args, "--backend") ?? "auto");
        var outputDir = Path.GetFullPath(GetValue(args, "--output-dir") ?? Path.Combine(RepoRoot(), "benchmark", "podish_perf", "results"));
        var name = GetValue(args, "--name") ?? "coremark-profile";
        var timeLimitSeconds = GetIntValue(args, "--time-limit", 18);
        var iterations = GetIntValue(args, "--iterations", 30000);
        var benchCase = GetValue(args, "--bench-case") ?? "run";
        var renamedBinary = GetValue(args, "--renamed-binary") ?? "PodishCliProfile";
        var rootfs = Path.GetFullPath(GetValue(args, "--rootfs") ?? Path.Combine(RepoRoot(), "benchmark", "podish_perf", "rootfs", "coremark_i386_alpine"));
        var binaryPath = Path.GetFullPath(GetValue(args, "--binary") ?? DefaultProfileBinary());
        var jitMapDir = GetValue(args, "--jit-map-dir");
        var backend = CreateProfileBackend(backendKind);

        if (string.IsNullOrWhiteSpace(benchCase) || !new HashSet<string>(new[] { "run", "compile", "compress", "gcc_compile" }, StringComparer.Ordinal).Contains(benchCase))
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
        var outputDir = Path.GetFullPath(outputDirOverride ?? GetValue(args, "--output-dir") ?? Path.Combine(RepoRoot(), "benchmark", "podish_perf", "results"));
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
        var defaultMacAot = Path.GetFullPath(Path.Combine(RepoRoot(), "build", "nativeaot", "podish-cli-static", "Podish.Cli"));
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || !string.Equals(resolved, defaultMacAot, StringComparison.Ordinal))
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
            "auto" => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ProfileBackendKind.XcTrace : ProfileBackendKind.Perf,
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
        => backendKind switch
        {
            ProfileBackendKind.XcTrace => new XcTraceProfileBackend(),
            ProfileBackendKind.Perf => new PerfProfileBackend(),
            _ => throw new ArgumentOutOfRangeException(nameof(backendKind), backendKind, null)
        };

    private static string MakeOutputDir(string baseDir, string name)
    {
        var output = Path.Combine(baseDir, name);
        Directory.CreateDirectory(output);
        return output;
    }

    private static string MakeUniqueBinary(string sourceBinary, string outDir, string renamedBinary)
    {
        var src = Path.GetFullPath(sourceBinary);
        if (!File.Exists(src))
            throw new FileNotFoundException($"Binary not found: {src}");

        foreach (var sibling in Directory.EnumerateFiles(Path.GetDirectoryName(src)!))
        {
            if (string.Equals(Path.GetFileName(sibling), Path.GetFileName(src), StringComparison.Ordinal))
                continue;
            File.Copy(sibling, Path.Combine(outDir, Path.GetFileName(sibling)), overwrite: true);
        }

        var dst = Path.Combine(outDir, renamedBinary);
        File.Copy(src, dst, overwrite: true);
        TryMarkExecutable(dst);
        return dst;
    }

    private static string GetTracePath(string outDir, string name, ProfileBackendKind backendKind)
        => Path.Combine(outDir, $"{name}.{(backendKind == ProfileBackendKind.XcTrace ? "trace" : "data")}");

    private static void WriteRecordCommand(string outDir, ProfileRecordOptions options, string runBinary, string tracePath)
    {
        var podishLaunch = BuildPodishLaunchCommand(runBinary, options.Rootfs, options.Iterations, options.BenchCase);
        var recordCommand = options.Backend.BuildRecordCommand(options, tracePath, podishLaunch);
        File.WriteAllText(Path.Combine(outDir, "record-command.txt"), ShellJoin(recordCommand) + Environment.NewLine, Utf8NoBom);
    }

    private static Dictionary<string, string> BuildProfileEnvironment(ProfileRecordOptions options)
    {
        var env = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "", entry => Convert.ToString(entry.Value, CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(options.JitMapDir))
        {
            Directory.CreateDirectory(options.JitMapDir);
            env["FIBERCPU_JIT_PROFILE_MAP_DIR"] = options.JitMapDir;
        }
        return env;
    }

    private static List<string> BuildPodishLaunchCommand(string binary, string rootfs, int iterations, string benchCase)
    {
        var guestCommand = DefaultGuestCommand(benchCase, iterations);
        return new List<string>
        {
            binary,
            "run",
            "--rm",
            "--rootfs",
            rootfs,
            "--"
        }.Concat(guestCommand).ToList();
    }

    private static List<string> DefaultGuestCommand(string benchCase, int iterations)
        => new() { "/bin/sh", "-lc", BuildGuestScript(benchCase, iterations) };

    private static string BuildGuestScript(string benchCase, int iterations)
    {
        var compileCommand = $"make PORT_DIR=linux ITERATIONS={iterations} XCFLAGS=\"-O3 -DPERFORMANCE_RUN=1\" REBUILD=1 compile";
        return benchCase switch
        {
            "compress" => """
set -eu
rm -rf /tmp/coremark.tar /tmp/coremark.tar.gz /tmp/coremark-restored.tar /tmp/coremark-unpack
mkdir -p /tmp/coremark-unpack
sync >/dev/null 2>&1 || true
tar -C / -cf /tmp/coremark.tar coremark
gzip -1 -c /tmp/coremark.tar > /tmp/coremark.tar.gz
gzip -dc /tmp/coremark.tar.gz > /tmp/coremark-restored.tar
tar -C /tmp/coremark-unpack -xf /tmp/coremark-restored.tar
test -f /tmp/coremark-unpack/coremark/Makefile
""",
            "compile" or "gcc_compile" => $"""
set -eu
cd /coremark
make clean >/dev/null 2>&1 || true
sync >/dev/null 2>&1 || true
{compileCommand}
test -x /coremark/coremark.exe
""",
            "run" => $"""
set -eu
cd /coremark
test -x ./coremark.exe || {compileCommand} >/dev/null
/coremark/coremark.exe 0x0 0x0 0x66 2000 >/dev/null 2>&1
./coremark.exe 0x0 0x0 0x66 {iterations}
""",
            _ => throw new ArgumentException($"unknown bench case: {benchCase}")
        };
    }

    private static string CompareProfileReports(List<string> reportPaths, int top, string? outPath)
    {
        var reports = reportPaths.Select(path => JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)).RootElement.Clone()).ToList();
        var labels = reportPaths.Select(path => Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path))).ToList();
        var perReport = new List<Dictionary<string, double>>(reports.Count);
        var allSymbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var report in reports)
        {
            var map = new Dictionary<string, double>(StringComparer.Ordinal);
            if (report.TryGetProperty("hotspots", out var hotspotsNode) && hotspotsNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var hotspot in hotspotsNode.EnumerateArray())
                {
                    var symbol = hotspot.TryGetProperty("symbol", out var symbolNode) ? symbolNode.GetString() ?? "" : "";
                    var selfMs = hotspot.TryGetProperty("self_ms", out var selfNode) ? selfNode.GetDouble() : 0.0;
                    if (string.IsNullOrWhiteSpace(symbol))
                        continue;
                    map[symbol] = selfMs;
                    allSymbols.Add(symbol);
                }
            }
            perReport.Add(map);
        }

        var rankedSymbols = allSymbols
            .OrderByDescending(symbol => perReport.Max(report => report.TryGetValue(symbol, out var value) ? value : 0.0))
            .Take(top)
            .ToList();

        var lines = new List<string>
        {
            "# hotspot comparison",
            "",
            "| Symbol | " + string.Join(" | ", labels.Select(label => $"{label} (ms)")) + " |",
            "|---|" + string.Join("", labels.Select(_ => "---:|"))
        };
        foreach (var symbol in rankedSymbols)
        {
            var values = perReport.Select(report => report.TryGetValue(symbol, out var value) ? value : 0.0);
            lines.Add($"| `{symbol}` | {string.Join(" | ", values.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)))} |");
        }

        var content = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        if (!string.IsNullOrWhiteSpace(outPath))
            File.WriteAllText(outPath, content, Utf8NoBom);
        return content;
    }

    private static ProfileReportPaths WriteProfileReport(
        string outDir,
        string name,
        ProfileAnalysisResult analysis,
        string binaryPath,
        Dictionary<string, string> disassembly)
    {
        var reportJsonPath = Path.Combine(outDir, $"{name}.report.json");
        var reportMarkdownPath = Path.Combine(outDir, $"{name}.report.md");
        var payload = new Dictionary<string, object?>
        {
            ["metadata"] = new Dictionary<string, object?>
            {
                ["backend"] = analysis.Backend,
                ["trace_path"] = analysis.TracePath,
                ["export_path"] = analysis.ExportPath,
                ["binary_path"] = binaryPath,
                ["warmup_seconds"] = analysis.WarmupSeconds,
                ["total_rows"] = analysis.TotalRows,
                ["kept_rows"] = analysis.KeptRows
            },
            ["hotspots"] = analysis.Hotspots.Select(h => h.ToDictionary()).ToList(),
            ["disassembly"] = disassembly
        };
        File.WriteAllText(reportJsonPath, JsonSerializer.Serialize(payload, JsonOptions), Utf8NoBom);

        var lines = new List<string>
        {
            $"# {name}",
            "",
            $"- backend: `{analysis.Backend}`",
            $"- trace: `{analysis.TracePath}`",
            $"- export: `{analysis.ExportPath}`",
            $"- binary: `{binaryPath}`",
            $"- warmup cutoff: `{analysis.WarmupSeconds:F1}s`",
            $"- kept samples: `{analysis.KeptRows}/{analysis.TotalRows}`",
            "",
            "| Rank | Self ms | Samples | Symbol | Binary |",
            "|---:|---:|---:|---|---|"
        };
        lines.AddRange(analysis.Hotspots.Select(hotspot =>
            $"| {hotspot.Rank} | {hotspot.SelfMs.ToString("F3", CultureInfo.InvariantCulture)} | {hotspot.SampleCount} | `{hotspot.Symbol}` | `{hotspot.BinaryName ?? ""}` |"));
        if (disassembly.Count > 0)
        {
            lines.Add("");
            lines.Add("## Disassembly");
            lines.Add("");
            foreach (var (symbol, path) in disassembly)
                lines.Add($"- `{symbol}`: `{path}`");
        }
        File.WriteAllText(reportMarkdownPath, string.Join(Environment.NewLine, lines) + Environment.NewLine, Utf8NoBom);
        return new ProfileReportPaths(reportJsonPath, reportMarkdownPath);
    }

    private static Dictionary<string, string> DisassembleSymbols(string binaryPath, List<string> symbols, string outDir)
    {
        var index = LoadSymbolIndex(binaryPath);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var objdump = ResolveObjdumpTool();
        foreach (var symbol in symbols)
        {
            if (!TryResolveSymbolName(symbol, index, out var entry))
                continue;

            var safeName = Regex.Replace(symbol, @"[^A-Za-z0-9_.-]+", "_");
            if (safeName.Length > 120)
                safeName = safeName[..120];
            var outPath = Path.Combine(outDir, $"disasm-{safeName}.txt");
            var args = new List<string> { "--demangle", "--disassemble", $"--start-address=0x{entry.Address}" };
            if (!string.IsNullOrWhiteSpace(entry.NextAddress))
                args.Add($"--stop-address=0x{entry.NextAddress}");
            args.Add(binaryPath);
            var text = RunResolvedToolCapture(objdump, args, RepoRoot());
            File.WriteAllText(outPath, text, Utf8NoBom);
            result[symbol] = outPath;
        }
        return result;
    }

    private static Dictionary<string, SymbolEntry> LoadSymbolIndex(string binaryPath)
    {
        var nmTool = ResolveNmTool();
        var mangledOutput = RunResolvedToolCapture(nmTool, new[] { "-n", binaryPath }, RepoRoot());
        var demangledOutput = RunResolvedToolCapture(nmTool, new[] { "-C", "-n", binaryPath }, RepoRoot());
        var mangled = ParseNmOutput(mangledOutput);
        var demangled = ParseNmOutput(demangledOutput);
        var orderedAddresses = demangled.Keys.OrderBy(address => Convert.ToUInt64(address, 16)).ToList();
        var index = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal);
        for (var i = 0; i < orderedAddresses.Count; i++)
        {
            var address = orderedAddresses[i];
            var demangledName = demangled[address].Name;
            var mangledName = mangled.TryGetValue(address, out var mangledEntry) ? mangledEntry.Name : demangledName;
            var nextAddress = i + 1 < orderedAddresses.Count ? orderedAddresses[i + 1] : null;
            index[demangledName] = new SymbolEntry(address, mangledName, demangledName, nextAddress);
        }
        return index;
    }

    private static Dictionary<string, (string Address, string Name)> ParseNmOutput(string text)
    {
        var map = new Dictionary<string, (string Address, string Name)>(StringComparer.Ordinal);
        var regex = new Regex(@"^([0-9a-fA-F]+)\s+\S\s+(.+)$", RegexOptions.Compiled);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = regex.Match(line.Trim());
            if (!match.Success)
                continue;
            map[match.Groups[1].Value] = (match.Groups[1].Value, match.Groups[2].Value);
        }
        return map;
    }

    private static bool TryResolveSymbolName(string symbol, Dictionary<string, SymbolEntry> index, out SymbolEntry entry)
    {
        if (index.TryGetValue(symbol, out entry!))
            return true;
        foreach (var (demangledName, candidate) in index)
        {
            if (demangledName.EndsWith(symbol, StringComparison.Ordinal) || symbol.EndsWith(demangledName, StringComparison.Ordinal))
            {
                entry = candidate;
                return true;
            }
        }
        entry = default!;
        return false;
    }

    private static ResolvedTool ResolveObjdumpTool()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && CommandExists("xcrun"))
            return new ResolvedTool("xcrun", new[] { "llvm-objdump" });
        if (CommandExists("llvm-objdump"))
            return new ResolvedTool("llvm-objdump", Array.Empty<string>());
        if (CommandExists("objdump"))
            return new ResolvedTool("objdump", Array.Empty<string>());
        throw new InvalidOperationException("Unable to find disassembler. Install llvm-objdump or objdump.");
    }

    private static ResolvedTool ResolveNmTool()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && CommandExists("xcrun"))
            return new ResolvedTool("xcrun", new[] { "llvm-nm" });
        if (CommandExists("llvm-nm"))
            return new ResolvedTool("llvm-nm", Array.Empty<string>());
        if (CommandExists("nm"))
            return new ResolvedTool("nm", Array.Empty<string>());
        throw new InvalidOperationException("Unable to find nm-compatible symbol tool. Install llvm-nm or nm.");
    }

    private static bool CommandExists(string command)
    {
        try
        {
            var search = RunCommand("bash", new[] { "-lc", $"command -v {EscapeShellSingleArgument(command)} >/dev/null 2>&1" }, RepoRoot(), captureOutput: false, okExitCodes: new HashSet<int> { 0, 1 });
            return search.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeShellSingleArgument(string value)
        => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string RunResolvedToolCapture(ResolvedTool tool, IEnumerable<string> arguments, string? workingDirectory = null)
        => RunCommandCheckedCapture(tool.FileName, tool.PrefixArgs.Concat(arguments).ToArray(), workingDirectory);

    private static string ShellJoin(IEnumerable<string> parts)
        => string.Join(" ", parts.Select(ShellQuote));

    private static string ShellQuote(string value)
    {
        if (value.Length == 0)
            return "''";
        if (value.All(ch => char.IsLetterOrDigit(ch) || "/._-:=+".Contains(ch, StringComparison.Ordinal)))
            return value;
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static void TryMarkExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            RunCommand("chmod", new[] { "+x", path }, RepoRoot(), captureOutput: false);
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static string DefaultAnalysisOutput(string input)
    {
        return Directory.Exists(input) ? Path.Combine(input, "blocks_analysis.json") : "blocks_analysis.json";
    }

    private static string RepoRoot()
    {
        foreach (var start in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory,
                     Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."))
                 })
        {
            var candidate = TryFindRepoRoot(start);
            if (candidate is not null)
                return candidate;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
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

    private static IEnumerable<string> EnumerateGuestStatsDirs(string guestStatsRoot)
    {
        if (!Directory.Exists(guestStatsRoot))
            yield break;

        foreach (var dir in Directory.EnumerateDirectories(guestStatsRoot, "*", SearchOption.AllDirectories))
        {
            if (File.Exists(Path.Combine(dir, "blocks.bin")))
                yield return dir;
        }
    }

    private static List<string> DiscoverAnalysisFiles(IEnumerable<string> inputs)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in inputs)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var path = Path.GetFullPath(raw);
            IEnumerable<string> candidates = Array.Empty<string>();
            if (File.Exists(path))
            {
                candidates = new[] { path };
            }
            else if (Directory.Exists(path))
            {
                var direct = Path.Combine(path, "blocks_analysis.json");
                candidates = File.Exists(direct)
                    ? new[] { direct }
                    : Directory.EnumerateFiles(path, "blocks_analysis.json", SearchOption.AllDirectories);
            }

            foreach (var candidate in candidates)
            {
                if (seen.Add(candidate))
                    files.Add(candidate);
            }
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static Dictionary<string, object?> AnalyzeBlocks(string inputPath, string libPath, int nGram, int topNgrams)
    {
        var (dumpFile, summaryFile, defaultOutput) = ResolveInputPaths(inputPath);
        var summary = LoadSummary(summaryFile);
        Console.Error.WriteLine($"[analyze-blocks] loading symbols from {libPath}");
        var symbols = LoadSymbols(libPath);
        Console.Error.WriteLine($"[analyze-blocks] loaded {symbols.Count} symbols");
        using var opIdResolver = HandlerOpIdResolver.TryCreate(libPath);
        Console.Error.WriteLine($"[analyze-blocks] parsing block dump {dumpFile}");
        var (baseAddr, count, blocks, warnings) = ParseBlocks(dumpFile, symbols, opIdResolver);
        Console.Error.WriteLine($"[analyze-blocks] parsed {blocks.Count}/{count} blocks");
        var validation = BuildValidation(summary, count, blocks.Count, warnings);

        var output = new Dictionary<string, object?>
        {
            ["metadata"] = new Dictionary<string, object?>
            {
                ["input_path"] = Path.GetFullPath(inputPath),
                ["lib_path"] = Path.GetFullPath(libPath),
                ["symbol_count"] = symbols.Count,
                ["n_gram"] = nGram,
                ["top_ngrams_limit"] = topNgrams,
                ["base_addr"] = baseAddr,
                ["declared_block_count"] = count,
                ["parsed_block_count"] = blocks.Count
            },
            ["validation"] = validation,
            ["blocks"] = blocks,
        };

        if (nGram > 0)
            output["ngrams"] = AnalyzeNgrams(blocks, nGram, topNgrams);

        Console.Error.WriteLine("[analyze-blocks] analysis object built");

        return output;
    }

    private static (string dumpFile, string? summaryFile, string defaultOutput) ResolveInputPaths(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var dumpFile = Path.Combine(inputPath, "blocks.bin");
            var summaryFile = Path.Combine(inputPath, "summary.json");
            if (!File.Exists(dumpFile))
                throw new FileNotFoundException($"Block dump file not found: {dumpFile}");
            return (dumpFile, File.Exists(summaryFile) ? summaryFile : null, Path.Combine(inputPath, "blocks_analysis.json"));
        }

        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Block dump file not found: {inputPath}");

        return (inputPath, null, "blocks_analysis.json");
    }

    private static JsonElement? LoadSummary(string? summaryFile)
    {
        if (string.IsNullOrWhiteSpace(summaryFile) || !File.Exists(summaryFile))
            return null;
        return JsonDocument.Parse(File.ReadAllText(summaryFile, Encoding.UTF8)).RootElement.Clone();
    }

    private static Dictionary<ulong, string> LoadSymbols(string libPath)
    {
        return TryLoadSymbolsWithObjectFile(libPath);
    }

    private static Dictionary<ulong, string> TryLoadSymbolsWithObjectFile(string libPath)
    {
        try
        {
            using var inStream = File.OpenRead(libPath);
            return DetectBinaryFormat(inStream) switch
            {
                BinaryFormat.Elf => TryLoadElfSymbols(inStream),
                BinaryFormat.Pe => TryLoadPeSymbols(inStream),
                BinaryFormat.MachO => TryLoadMachOSymbols(inStream),
                _ => new Dictionary<ulong, string>()
            };
        }
        catch
        {
            return new Dictionary<ulong, string>();
        }
    }

    private static Dictionary<ulong, string> TryLoadElfSymbols(Stream inStream)
    {
        var rawSymbols = new List<(ulong addr, string name)>();

        inStream.Position = 0;
        var elf = ElfFile.Read(inStream);
        foreach (var section in elf.Sections)
        {
            if (section is not ElfSymbolTable symtab)
                continue;

            foreach (var symbol in symtab.Entries)
            {
                var symbolName = symbol.Name.ToString();
                if (string.IsNullOrWhiteSpace(symbolName))
                    continue;

                ulong value;
                try
                {
                    value = Convert.ToUInt64(symbol.Value, CultureInfo.InvariantCulture);
                }
                catch
                {
                    continue;
                }

                rawSymbols.Add((value, symbolName));
            }
        }

        return BuildSymbolMap(rawSymbols);
    }

    private static Dictionary<ulong, string> BuildSymbolMap(List<(ulong addr, string name)> rawSymbols)
    {
        if (rawSymbols.Count == 0)
            return new Dictionary<ulong, string>();

        var demangled = DemangleSymbols(rawSymbols.Select(x => x.name).Distinct(StringComparer.Ordinal).ToArray());
        var symbols = new Dictionary<ulong, string>();
        foreach (var (addr, name) in rawSymbols)
        {
            symbols[addr] = demangled.TryGetValue(name, out var d) ? d : name;
        }

        return symbols;
    }

    private static Dictionary<ulong, string> TryLoadPeSymbols(Stream inStream)
    {
        inStream.Position = 0;
        using var reader = new BinaryReader(inStream, Encoding.UTF8, leaveOpen: true);
        var rawSymbols = new List<(ulong addr, string name)>();

        if (reader.ReadUInt16() != 0x5A4D)
            return new Dictionary<ulong, string>();

        inStream.Position = 0x3C;
        var peHeaderOffset = reader.ReadUInt32();
        if (peHeaderOffset >= inStream.Length || peHeaderOffset + 4 > inStream.Length)
            return new Dictionary<ulong, string>();

        inStream.Position = peHeaderOffset;
        if (reader.ReadUInt32() != 0x00004550)
            return new Dictionary<ulong, string>();

        _ = reader.ReadUInt16(); // Machine
        var numberOfSections = reader.ReadUInt16();
        _ = reader.ReadUInt32(); // TimeDateStamp
        var pointerToSymbolTable = reader.ReadUInt32();
        var numberOfSymbols = reader.ReadUInt32();
        var sizeOfOptionalHeader = reader.ReadUInt16();
        _ = reader.ReadUInt16(); // Characteristics

        var optionalHeaderStart = inStream.Position;
        var optionalMagic = sizeOfOptionalHeader >= 2 ? reader.ReadUInt16() : (ushort)0;
        uint exportTableRva = 0;
        uint exportTableSize = 0;

        if (sizeOfOptionalHeader > 0 && optionalHeaderStart + sizeOfOptionalHeader <= inStream.Length)
        {
            if (optionalMagic == 0x10B && sizeOfOptionalHeader >= 104)
            {
                inStream.Position = optionalHeaderStart + 96;
                exportTableRva = reader.ReadUInt32();
                exportTableSize = reader.ReadUInt32();
            }
            else if (optionalMagic == 0x20B && sizeOfOptionalHeader >= 120)
            {
                inStream.Position = optionalHeaderStart + 112;
                exportTableRva = reader.ReadUInt32();
                exportTableSize = reader.ReadUInt32();
            }
        }

        inStream.Position = optionalHeaderStart + sizeOfOptionalHeader;
        var sections = new List<PeSectionInfo>(numberOfSections);
        for (var i = 0; i < numberOfSections; i++)
        {
            if (inStream.Position + 40 > inStream.Length)
                break;

            _ = reader.ReadBytes(8); // Name
            var virtualSize = reader.ReadUInt32();
            var virtualAddress = reader.ReadUInt32();
            var sizeOfRawData = reader.ReadUInt32();
            var pointerToRawData = reader.ReadUInt32();
            _ = reader.ReadUInt32(); // PointerToRelocations
            _ = reader.ReadUInt32(); // PointerToLineNumbers
            _ = reader.ReadUInt16(); // NumberOfRelocations
            _ = reader.ReadUInt16(); // NumberOfLineNumbers
            _ = reader.ReadUInt32(); // Characteristics
            sections.Add(new PeSectionInfo(virtualAddress, virtualSize, sizeOfRawData, pointerToRawData));
        }

        if (pointerToSymbolTable != 0 &&
            numberOfSymbols != 0 &&
            pointerToSymbolTable < inStream.Length &&
            pointerToSymbolTable + (ulong)numberOfSymbols * 18 <= (ulong)inStream.Length)
        {
            var stringTableOffset = pointerToSymbolTable + numberOfSymbols * 18;
            uint stringTableSize = 0;
            if (stringTableOffset + 4 <= inStream.Length)
            {
                inStream.Position = stringTableOffset;
                stringTableSize = reader.ReadUInt32();
            }

            inStream.Position = pointerToSymbolTable;
            for (uint i = 0; i < numberOfSymbols; i++)
            {
                if (inStream.Position + 18 > inStream.Length)
                    break;

                var nameBytes = reader.ReadBytes(8);
                var value = reader.ReadUInt32();
                var sectionNumber = reader.ReadInt16();
                _ = reader.ReadUInt16(); // Type
                _ = reader.ReadByte(); // StorageClass
                var numberOfAuxSymbols = reader.ReadByte();

                var name = ReadPeSymbolName(nameBytes, inStream, stringTableOffset, stringTableSize);
                if (!string.IsNullOrWhiteSpace(name) && sectionNumber > 0 && sectionNumber <= sections.Count)
                {
                    var section = sections[sectionNumber - 1];
                    rawSymbols.Add((section.VirtualAddress + value, name));
                }

                if (numberOfAuxSymbols > 0)
                {
                    var skipBytes = (long)numberOfAuxSymbols * 18;
                    if (inStream.Position + skipBytes > inStream.Length)
                        break;
                    inStream.Position += skipBytes;
                    i += numberOfAuxSymbols;
                }
            }
        }

        if (exportTableRva != 0 &&
            exportTableSize >= 40 &&
            TryMapPeRvaToFileOffset(exportTableRva, sections, out var exportDirectoryOffset) &&
            exportDirectoryOffset + 40 <= (ulong)inStream.Length)
        {
            inStream.Position = (long)exportDirectoryOffset;
            _ = reader.ReadUInt32(); // Characteristics
            _ = reader.ReadUInt32(); // TimeDateStamp
            _ = reader.ReadUInt16(); // MajorVersion
            _ = reader.ReadUInt16(); // MinorVersion
            _ = reader.ReadUInt32(); // Name
            _ = reader.ReadUInt32(); // Base
            var numberOfFunctions = reader.ReadUInt32();
            var numberOfNames = reader.ReadUInt32();
            var addressOfFunctionsRva = reader.ReadUInt32();
            var addressOfNamesRva = reader.ReadUInt32();
            var addressOfNameOrdinalsRva = reader.ReadUInt32();

            if (numberOfFunctions > 0 &&
                numberOfNames > 0 &&
                TryMapPeRvaToFileOffset(addressOfFunctionsRva, sections, out var addressOfFunctionsOffset) &&
                TryMapPeRvaToFileOffset(addressOfNamesRva, sections, out var addressOfNamesOffset) &&
                TryMapPeRvaToFileOffset(addressOfNameOrdinalsRva, sections, out var addressOfNameOrdinalsOffset))
            {
                for (uint i = 0; i < numberOfNames; i++)
                {
                    var nameEntryOffset = addressOfNamesOffset + i * 4;
                    var ordinalEntryOffset = addressOfNameOrdinalsOffset + i * 2;
                    if (nameEntryOffset + 4 > (ulong)inStream.Length || ordinalEntryOffset + 2 > (ulong)inStream.Length)
                        break;

                    inStream.Position = (long)nameEntryOffset;
                    var nameRva = reader.ReadUInt32();
                    inStream.Position = (long)ordinalEntryOffset;
                    var ordinal = reader.ReadUInt16();
                    if (ordinal >= numberOfFunctions)
                        continue;

                    var functionEntryOffset = addressOfFunctionsOffset + (ulong)ordinal * 4;
                    if (functionEntryOffset + 4 > (ulong)inStream.Length)
                        continue;

                    inStream.Position = (long)functionEntryOffset;
                    var functionRva = reader.ReadUInt32();
                    if (functionRva >= exportTableRva && functionRva < exportTableRva + exportTableSize)
                        continue;

                    if (!TryMapPeRvaToFileOffset(nameRva, sections, out var nameOffset))
                        continue;

                    var name = ReadNullTerminatedAscii(inStream, nameOffset, int.MaxValue);
                    if (!string.IsNullOrWhiteSpace(name))
                        rawSymbols.Add((functionRva, name));
                }
            }
        }

        return BuildSymbolMap(rawSymbols);
    }

    private static Dictionary<ulong, string> TryLoadMachOSymbols(Stream inStream)
    {
        var rawSymbols = new List<(ulong addr, string name)>();
        if (!TryReadMachOSlice(inStream, out var sliceOffset, out var is64Bit, out var isLittleEndian))
            return new Dictionary<ulong, string>();

        uint numberOfCommands;
        ulong loadCommandsOffset;
        if (is64Bit)
        {
            if (sliceOffset + 32 > (ulong)inStream.Length)
                return new Dictionary<ulong, string>();
            numberOfCommands = ReadUInt32(inStream, sliceOffset + 16, isLittleEndian);
            loadCommandsOffset = sliceOffset + 32;
        }
        else
        {
            if (sliceOffset + 28 > (ulong)inStream.Length)
                return new Dictionary<ulong, string>();
            numberOfCommands = ReadUInt32(inStream, sliceOffset + 16, isLittleEndian);
            loadCommandsOffset = sliceOffset + 28;
        }

        ulong? symbolTableOffset = null;
        uint numberOfSymbols = 0;
        ulong? stringTableOffset = null;
        uint stringTableSize = 0;
        ulong? imageBase = null;
        var commandOffset = loadCommandsOffset;

        for (uint i = 0; i < numberOfCommands; i++)
        {
            if (commandOffset + 8 > (ulong)inStream.Length)
                break;

            var command = ReadUInt32(inStream, commandOffset, isLittleEndian);
            var commandSize = ReadUInt32(inStream, commandOffset + 4, isLittleEndian);
            if (commandSize < 8 || commandOffset + commandSize > (ulong)inStream.Length)
                break;

            const uint LcSymtab = 0x2;
            const uint LcSegment = 0x1;
            const uint LcSegment64 = 0x19;

            if (command == LcSymtab && commandSize >= 24)
            {
                symbolTableOffset = ReadUInt32(inStream, commandOffset + 8, isLittleEndian) + sliceOffset;
                numberOfSymbols = ReadUInt32(inStream, commandOffset + 12, isLittleEndian);
                stringTableOffset = ReadUInt32(inStream, commandOffset + 16, isLittleEndian) + sliceOffset;
                stringTableSize = ReadUInt32(inStream, commandOffset + 20, isLittleEndian);
            }
            else if (command == LcSegment64 && commandSize >= 72)
            {
                var vmaddr = ReadUInt64(inStream, commandOffset + 24, isLittleEndian);
                var filesize = ReadUInt64(inStream, commandOffset + 40, isLittleEndian);
                if (filesize != 0 && (!imageBase.HasValue || vmaddr < imageBase.Value))
                    imageBase = vmaddr;
            }
            else if (command == LcSegment && commandSize >= 56)
            {
                var vmaddr = ReadUInt32(inStream, commandOffset + 24, isLittleEndian);
                var filesize = ReadUInt32(inStream, commandOffset + 36, isLittleEndian);
                if (filesize != 0 && (!imageBase.HasValue || vmaddr < imageBase.Value))
                    imageBase = vmaddr;
            }

            commandOffset += commandSize;
        }

        if (!symbolTableOffset.HasValue ||
            !stringTableOffset.HasValue ||
            symbolTableOffset.Value >= (ulong)inStream.Length ||
            stringTableOffset.Value >= (ulong)inStream.Length)
            return new Dictionary<ulong, string>();

        var entrySize = is64Bit ? 16u : 12u;
        var baseAddress = imageBase ?? 0;
        for (uint i = 0; i < numberOfSymbols; i++)
        {
            var entryOffset = symbolTableOffset.Value + i * entrySize;
            if (entryOffset + entrySize > (ulong)inStream.Length)
                break;

            var stringIndex = ReadUInt32(inStream, entryOffset, isLittleEndian);
            var type = ReadByte(inStream, entryOffset + 4);
            ulong value = is64Bit
                ? ReadUInt64(inStream, entryOffset + 8, isLittleEndian)
                : ReadUInt32(inStream, entryOffset + 8, isLittleEndian);

            const byte NStabMask = 0xE0;
            const byte NTypeMask = 0x0E;
            const byte NSect = 0x0E;
            if ((type & NStabMask) != 0 || (type & NTypeMask) != NSect || stringIndex == 0 || value == 0)
                continue;

            var nameOffset = stringTableOffset.Value + stringIndex;
            if (nameOffset >= (ulong)inStream.Length || nameOffset >= stringTableOffset.Value + stringTableSize)
                continue;

            var maxLength = (int)Math.Min(int.MaxValue, stringTableOffset.Value + stringTableSize - nameOffset);
            var name = ReadNullTerminatedAscii(inStream, nameOffset, maxLength);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var normalizedValue = value >= baseAddress ? value - baseAddress : value;
            rawSymbols.Add((normalizedValue, name));
        }

        return BuildSymbolMap(rawSymbols);
    }

    private static BinaryFormat DetectBinaryFormat(Stream inStream)
    {
        if (inStream.Length < 4)
            return BinaryFormat.Unknown;

        inStream.Position = 0;
        Span<byte> magic = stackalloc byte[4];
        inStream.ReadExactly(magic);
        inStream.Position = 0;

        if (magic[0] == 0x7F && magic[1] == (byte)'E' && magic[2] == (byte)'L' && magic[3] == (byte)'F')
            return BinaryFormat.Elf;

        if (magic[0] == (byte)'M' && magic[1] == (byte)'Z')
            return BinaryFormat.Pe;

        var value = BinaryPrimitives.ReadUInt32LittleEndian(magic);
        return value switch
        {
            0xFEEDFACE or 0xCEFAEDFE or 0xFEEDFACF or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA or 0xCAFEBABF or 0xBFBAFECA => BinaryFormat.MachO,
            _ => BinaryFormat.Unknown
        };
    }

    private static bool TryMapPeRvaToFileOffset(uint rva, List<PeSectionInfo> sections, out ulong fileOffset)
    {
        foreach (var section in sections)
        {
            var span = Math.Max(section.VirtualSize, section.SizeOfRawData);
            if (rva < section.VirtualAddress || rva >= section.VirtualAddress + span)
                continue;

            fileOffset = section.PointerToRawData + (rva - section.VirtualAddress);
            return true;
        }

        fileOffset = 0;
        return false;
    }

    private static string ReadPeSymbolName(byte[] nameBytes, Stream inStream, long stringTableOffset, uint stringTableSize)
    {
        if (nameBytes.Length != 8)
            return string.Empty;

        if (BinaryPrimitives.ReadUInt32LittleEndian(nameBytes.AsSpan(0, 4)) == 0)
        {
            var stringOffset = BinaryPrimitives.ReadUInt32LittleEndian(nameBytes.AsSpan(4, 4));
            if (stringOffset < 4 || stringOffset >= stringTableSize)
                return string.Empty;

            return ReadNullTerminatedAscii(inStream, (ulong)(stringTableOffset + stringOffset), (int)(stringTableSize - stringOffset));
        }

        var terminator = Array.IndexOf(nameBytes, (byte)0);
        var length = terminator >= 0 ? terminator : nameBytes.Length;
        return Encoding.ASCII.GetString(nameBytes, 0, length);
    }

    private static bool TryReadMachOSlice(Stream inStream, out ulong sliceOffset, out bool is64Bit, out bool isLittleEndian)
    {
        sliceOffset = 0;
        is64Bit = false;
        isLittleEndian = true;

        if (inStream.Length < 4)
            return false;

        var magic = ReadUInt32(inStream, 0, isLittleEndian: true);
        switch (magic)
        {
            case 0xFEEDFACE:
                isLittleEndian = true;
                is64Bit = false;
                return true;
            case 0xCEFAEDFE:
                isLittleEndian = false;
                is64Bit = false;
                return true;
            case 0xFEEDFACF:
                isLittleEndian = true;
                is64Bit = true;
                return true;
            case 0xCFFAEDFE:
                isLittleEndian = false;
                is64Bit = true;
                return true;
            case 0xCAFEBABE:
            case 0xBEBAFECA:
            case 0xCAFEBABF:
            case 0xBFBAFECA:
                return TryReadFatMachOSlice(inStream, magic, out sliceOffset, out is64Bit, out isLittleEndian);
            default:
                return false;
        }
    }

    private static bool TryReadFatMachOSlice(Stream inStream, uint fatMagic, out ulong sliceOffset, out bool is64Bit, out bool isLittleEndian)
    {
        sliceOffset = 0;
        is64Bit = false;
        isLittleEndian = true;

        var fatIs64 = fatMagic is 0xCAFEBABF or 0xBFBAFECA;
        var archEntrySize = fatIs64 ? 32 : 20;
        var numberOfArchitectures = ReadUInt32(inStream, 4, isLittleEndian: false);
        if (numberOfArchitectures == 0)
            return false;

        var preferredCpuType = GetPreferredMachOCpuType();
        ulong? fallbackOffset = null;
        bool? fallbackIs64 = null;
        bool? fallbackLittleEndian = null;

        for (uint i = 0; i < numberOfArchitectures; i++)
        {
            var entryOffset = 8UL + i * (ulong)archEntrySize;
            if (entryOffset + (ulong)archEntrySize > (ulong)inStream.Length)
                break;

            var cpuType = ReadUInt32(inStream, entryOffset, isLittleEndian: false);
            var offset = fatIs64
                ? ReadUInt64(inStream, entryOffset + 8, isLittleEndian: false)
                : ReadUInt32(inStream, entryOffset + 8, isLittleEndian: false);
            if (offset + 4 > (ulong)inStream.Length)
                continue;

            var sliceMagic = ReadUInt32(inStream, offset, isLittleEndian: true);
            if (!TryDecodeThinMachOMagic(sliceMagic, out var sliceIs64, out var sliceLittleEndian))
                continue;

            fallbackOffset ??= offset;
            fallbackIs64 ??= sliceIs64;
            fallbackLittleEndian ??= sliceLittleEndian;

            if (preferredCpuType.HasValue && cpuType == preferredCpuType.Value)
            {
                sliceOffset = offset;
                is64Bit = sliceIs64;
                isLittleEndian = sliceLittleEndian;
                return true;
            }
        }

        if (fallbackOffset.HasValue && fallbackIs64.HasValue && fallbackLittleEndian.HasValue)
        {
            sliceOffset = fallbackOffset.Value;
            is64Bit = fallbackIs64.Value;
            isLittleEndian = fallbackLittleEndian.Value;
            return true;
        }

        return false;
    }

    private static bool TryDecodeThinMachOMagic(uint magic, out bool is64Bit, out bool isLittleEndian)
    {
        switch (magic)
        {
            case 0xFEEDFACE:
                is64Bit = false;
                isLittleEndian = true;
                return true;
            case 0xCEFAEDFE:
                is64Bit = false;
                isLittleEndian = false;
                return true;
            case 0xFEEDFACF:
                is64Bit = true;
                isLittleEndian = true;
                return true;
            case 0xCFFAEDFE:
                is64Bit = true;
                isLittleEndian = false;
                return true;
            default:
                is64Bit = false;
                isLittleEndian = true;
                return false;
        }
    }

    private static uint? GetPreferredMachOCpuType()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => 0x0100000C,
            Architecture.X64 => 0x01000007,
            Architecture.X86 => 7u,
            Architecture.Arm => 12u,
            _ => null
        };
    }

    private static byte ReadByte(Stream inStream, ulong offset)
    {
        inStream.Position = (long)offset;
        var value = inStream.ReadByte();
        if (value < 0)
            throw new EndOfStreamException();
        return (byte)value;
    }

    private static uint ReadUInt32(Stream inStream, ulong offset, bool isLittleEndian)
    {
        Span<byte> buffer = stackalloc byte[4];
        inStream.Position = (long)offset;
        inStream.ReadExactly(buffer);
        return isLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(buffer)
            : BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    private static ulong ReadUInt64(Stream inStream, ulong offset, bool isLittleEndian)
    {
        Span<byte> buffer = stackalloc byte[8];
        inStream.Position = (long)offset;
        inStream.ReadExactly(buffer);
        return isLittleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(buffer)
            : BinaryPrimitives.ReadUInt64BigEndian(buffer);
    }

    private static string ReadNullTerminatedAscii(Stream inStream, ulong offset, int maxLength)
    {
        if (maxLength <= 0)
            return string.Empty;

        inStream.Position = (long)offset;
        using var buffer = new MemoryStream();
        for (var i = 0; i < maxLength; i++)
        {
            var value = inStream.ReadByte();
            if (value <= 0)
                break;
            buffer.WriteByte((byte)value);
        }

        return Encoding.ASCII.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private enum BinaryFormat
    {
        Unknown,
        Elf,
        Pe,
        MachO
    }

    private sealed record PeSectionInfo(uint VirtualAddress, uint VirtualSize, uint SizeOfRawData, uint PointerToRawData);

    private sealed class HandlerOpIdResolver : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetOpIdForHandlerDelegate(IntPtr handler);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetLibAddressDelegate();

        private readonly IntPtr _libraryHandle;
        private readonly GetOpIdForHandlerDelegate? _getOpIdForHandler;
        private readonly ulong _libBase;
        private readonly Dictionary<ulong, int?> _cache = new();

        private HandlerOpIdResolver(
            IntPtr libraryHandle,
            GetOpIdForHandlerDelegate? getOpIdForHandler,
            ulong libBase)
        {
            _libraryHandle = libraryHandle;
            _getOpIdForHandler = getOpIdForHandler;
            _libBase = libBase;
        }

        public static HandlerOpIdResolver? TryCreate(string libPath)
        {
            try
            {
                var handle = NativeLibrary.Load(libPath);
                var getOpIdForHandler = Marshal.GetDelegateForFunctionPointer<GetOpIdForHandlerDelegate>(
                    NativeLibrary.GetExport(handle, "X86_GetOpIdForHandler"));
                var getLibAddress = Marshal.GetDelegateForFunctionPointer<GetLibAddressDelegate>(
                    NativeLibrary.GetExport(handle, "X86_GetLibAddress"));
                var libBase = (ulong)getLibAddress().ToInt64();
                return new HandlerOpIdResolver(handle, getOpIdForHandler, libBase);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[analyze-blocks] warning: failed to load X86_GetOpIdForHandler from {libPath}: {ex.Message}");
                return null;
            }
        }

        public int? Resolve(ulong handlerPtr, ulong handlerOffset)
        {
            if (_getOpIdForHandler is null)
                return null;

            ulong lookupPtr;
            if (_libBase != 0 && handlerOffset != 0)
                lookupPtr = _libBase + handlerOffset;
            else if (handlerPtr != 0)
                lookupPtr = handlerPtr;
            else
                return null;

            if (_cache.TryGetValue(lookupPtr, out var cached))
                return cached;

            var value = _getOpIdForHandler((IntPtr)unchecked((long)lookupPtr));
            int? result = value < 0 ? null : value;
            _cache[lookupPtr] = result;
            return result;
        }

        public void Dispose()
        {
            if (_libraryHandle != IntPtr.Zero)
                NativeLibrary.Free(_libraryHandle);
        }
    }

    private static Dictionary<string, string> DemangleSymbols(string[] names)
    {
        if (names.Length == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var psi = new ProcessStartInfo("c++filt", "-n")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            foreach (var name in names)
            {
                var toWrite = name.StartsWith("__Z", StringComparison.Ordinal) ? name[1..] : name;
                proc.StandardInput.WriteLine(toWrite);
            }

            proc.StandardInput.Close();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length != names.Length)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < names.Length; i++)
                map[names[i]] = lines[i];
            return map;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static (ulong baseAddr, int count, List<Dictionary<string, object?>> blocks, List<string> warnings) ParseBlocks(
        string dumpFile, Dictionary<ulong, string> symbols, HandlerOpIdResolver? opIdResolver)
    {
        var blocks = new List<Dictionary<string, object?>>();
        var warnings = new List<string>();

        using var stream = File.OpenRead(dumpFile);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var baseAddr = reader.ReadUInt64();
        var count = reader.ReadInt32();

        for (var blockIndex = 0; blockIndex < count; blockIndex++)
        {
            var header = reader.ReadBytes(20);
            if (header.Length != 20)
            {
                warnings.Add($"truncated block header at index {blockIndex}: expected 20 bytes, got {header.Length}");
                break;
            }

            var startEip = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
            var endEip = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            var instCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
            var execCount = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(12, 8));

            var ops = new List<Dictionary<string, object?>>();
            var truncatedBlock = false;
            for (var opIndex = 0; opIndex < (int)instCount; opIndex++)
            {
                var opBytes = reader.ReadBytes(32);
                if (opBytes.Length != 32)
                {
                    warnings.Add($"truncated op payload in block 0x{startEip:x} at op {opIndex}: expected 32 bytes, got {opBytes.Length}");
                    truncatedBlock = true;
                    break;
                }

                var memPacked = BinaryPrimitives.ReadUInt64LittleEndian(opBytes.AsSpan(0, 8));
                var nextEip = BinaryPrimitives.ReadUInt32LittleEndian(opBytes.AsSpan(8, 4));
                var len = opBytes[12];
                var modrm = opBytes[13];
                var prefixes = opBytes[14];
                var meta = opBytes[15];
                var imm = BinaryPrimitives.ReadUInt32LittleEndian(opBytes.AsSpan(16, 4));
                var handlerPtr = BinaryPrimitives.ReadUInt64LittleEndian(opBytes.AsSpan(24, 8));

                var memDisp = (uint)(memPacked & 0xFFFFFFFF);
                var eaDesc = (uint)(memPacked >> 32);
                var handlerOffset = handlerPtr >= baseAddr ? handlerPtr - baseAddr : 0UL;
                var symbolName = FindSymbol(handlerOffset, symbols);
                var logicFunc = NormalizeLogicFuncName(symbolName);
                var opId = opIdResolver?.Resolve(handlerPtr, handlerOffset);
                var defUse = AnalyzeDefUse(opId, modrm, meta, prefixes, (int)eaDesc);
                var defUseNode = defUse?.ToDictionary() ?? new Dictionary<string, object?>
                {
                    ["reads"] = Array.Empty<string>(),
                    ["writes"] = Array.Empty<string>(),
                    ["notes"] = Array.Empty<string>(),
                    ["control_flow"] = false,
                    ["memory_side_effect"] = false
                };

                ops.Add(new Dictionary<string, object?>
                {
                    ["index"] = opIndex,
                    ["next_eip"] = nextEip,
                    ["next_eip_hex"] = $"0x{nextEip:x}",
                    ["handler_ptr"] = handlerPtr,
                    ["handler_ptr_hex"] = $"0x{handlerPtr:x}",
                    ["handler_offset"] = handlerOffset,
                    ["handler_offset_hex"] = $"0x{handlerOffset:x}",
                    ["symbol_raw"] = symbolName,
                    ["logic_func"] = logicFunc,
                    ["symbol"] = logicFunc ?? symbolName,
                    ["op_id"] = opId,
                    ["op_id_hex"] = opId is null ? null : $"0x{opId.Value:x}",
                    ["imm"] = imm,
                    ["imm_hex"] = $"0x{imm:x}",
                    ["len"] = len,
                    ["len_hex"] = $"0x{len:x}",
                    ["prefixes"] = prefixes,
                    ["prefixes_hex"] = $"0x{prefixes:x}",
                    ["modrm"] = modrm,
                    ["modrm_hex"] = $"0x{modrm:x}",
                    ["meta"] = meta,
                    ["meta_hex"] = $"0x{meta:x}",
                    ["mem"] = new Dictionary<string, object?>
                    {
                        ["disp"] = memDisp,
                        ["disp_hex"] = $"0x{memDisp:x}",
                        ["ea_desc"] = eaDesc,
                        ["ea_desc_hex"] = $"0x{eaDesc:x}",
                        ["base_offset"] = eaDesc & 0x3F,
                        ["base_offset_hex"] = $"0x{(eaDesc & 0x3F):x}",
                        ["index_offset"] = (eaDesc >> 6) & 0x3F,
                        ["index_offset_hex"] = $"0x{((eaDesc >> 6) & 0x3F):x}",
                        ["scale"] = (eaDesc >> 12) & 0x3,
                        ["scale_hex"] = $"0x{((eaDesc >> 12) & 0x3):x}",
                        ["segment"] = (eaDesc >> 14) & 0x7,
                        ["segment_hex"] = $"0x{((eaDesc >> 14) & 0x7):x}",
                    },
                    ["def_use"] = defUseNode
                });
            }

            if (truncatedBlock)
                break;

            blocks.Add(new Dictionary<string, object?>
            {
                ["start_eip"] = startEip,
                ["start_eip_hex"] = $"0x{startEip:x}",
                ["end_eip"] = endEip,
                ["end_eip_hex"] = $"0x{endEip:x}",
                ["inst_count"] = instCount,
                ["exec_count"] = execCount,
                ["ops"] = ops
            });
        }

        return (baseAddr, count, blocks, warnings);
    }

    private static Dictionary<string, object?> BuildValidation(JsonElement? summary, int declaredBlockCount, int parsedBlocks, List<string> parseWarnings)
    {
        var validation = new Dictionary<string, object?>
        {
            ["warnings"] = parseWarnings.ToArray()
        };

        if (summary is { } summaryRoot)
        {
            if (summaryRoot.TryGetProperty("native_stats", out var nativeStatsRaw) && nativeStatsRaw.ValueKind == JsonValueKind.String)
            {
                try
                {
                    var nativeStats = JsonDocument.Parse(nativeStatsRaw.GetString() ?? "{}").RootElement;
                    if (nativeStats.TryGetProperty("all_blocks_count", out var allBlocksCount) && allBlocksCount.ValueKind == JsonValueKind.Number)
                    {
                        var nativeCount = allBlocksCount.GetInt32();
                        if (nativeCount != declaredBlockCount)
                        {
                            validation["warnings"] = ((string[])validation["warnings"]!).Append(
                                $"summary native_stats.all_blocks_count={nativeCount} but dump declared block count={declaredBlockCount}").ToArray();
                        }
                        if (nativeCount > 0 && parsedBlocks == 0)
                        {
                            validation["warnings"] = ((string[])validation["warnings"]!).Append(
                                "summary reports non-zero native all_blocks_count but parsed blocks are empty; dump/export format likely drifted").ToArray();
                        }
                    }
                }
                catch
                {
                    validation["warnings"] = ((string[])validation["warnings"]!).Append("summary native_stats is not valid JSON").ToArray();
                }
            }
        }

        return validation;
    }

    private static string FindSymbol(ulong offset, Dictionary<ulong, string> symbols)
        => symbols.TryGetValue(offset, out var name) ? name : $"func_{offset:x}";

    private static string? NormalizeLogicFuncName(string? symbolName)
    {
        if (string.IsNullOrWhiteSpace(symbolName))
            return null;
        if (symbolName.Contains("SuperOpcode_", StringComparison.Ordinal))
            return null;
        if (symbolName.StartsWith("op::Op", StringComparison.Ordinal))
            return symbolName;
        if (symbolName.StartsWith("Op", StringComparison.Ordinal) && OpPrefixRegex.IsMatch(symbolName))
            return "op::" + symbolName;

        var wrapperMatch = DispatchWrapperRegex.Match(symbolName);
        if (wrapperMatch.Success)
            return wrapperMatch.Groups[1].Value;

        var directMatch = DirectLogicRegex.Match(symbolName);
        if (directMatch.Success)
            return directMatch.Groups[1].Value;

        var mangledMatch = MangledLogicRegex.Match(symbolName);
        if (mangledMatch.Success)
            return "op::" + mangledMatch.Groups[1].Value;

        return null;
    }

    private static Dictionary<string, object?> AnalyzeNgrams(List<Dictionary<string, object?>> blocks, int n, int topNgrams)
    {
        var stats = new Dictionary<string, NgramAggregate>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            if (!block.TryGetValue("ops", out var opsObj) || opsObj is not List<Dictionary<string, object?>> ops)
                continue;
            var symbols = ops.Select(op => op.TryGetValue("logic_func", out var lf) ? lf as string : null).ToList();
            if (symbols.Count < n)
                continue;
            var startEip = Convert.ToUInt32(block["start_eip"], CultureInfo.InvariantCulture);
            var execCount = (long)Convert.ToUInt64(block["exec_count"], CultureInfo.InvariantCulture);
            for (var i = 0; i <= symbols.Count - n; i++)
            {
                var ngram = symbols.Skip(i).Take(n).ToArray();
                if (ngram.Any(string.IsNullOrWhiteSpace))
                    continue;
                var key = string.Join("\u001f", ngram!);
                if (!stats.TryGetValue(key, out var entry))
                {
                    entry = new NgramAggregate(ngram!);
                    stats[key] = entry;
                }
                entry.WeightedExecCount += execCount;
                entry.Occurrences += 1;
                entry.UniqueBlockStarts.Add(startEip);
                if (entry.ExampleBlocks.Count < 5)
                {
                    entry.ExampleBlocks.Add(new Dictionary<string, object?>
                    {
                        ["start_eip"] = startEip,
                        ["start_eip_hex"] = $"0x{startEip:x}",
                        ["exec_count"] = execCount,
                        ["start_op_index"] = i
                    });
                }
            }
        }

        var sorted = stats.Values
            .OrderByDescending(e => e.WeightedExecCount)
            .ThenByDescending(e => e.Occurrences)
            .Take(topNgrams)
            .Select(e => new Dictionary<string, object?>
            {
                ["ngram"] = e.Ngram,
                ["ngram_display"] = string.Join(" -> ", e.Ngram),
                ["weighted_exec_count"] = e.WeightedExecCount,
                ["occurrences"] = e.Occurrences,
                ["unique_block_count"] = e.UniqueBlockStarts.Count,
                ["example_blocks"] = e.ExampleBlocks
            })
            .ToList();

        return new Dictionary<string, object?>
        {
            ["n_gram_size"] = n,
            ["total_unique_ngrams"] = stats.Count,
            ["top_ngrams"] = sorted,
        };
    }

    private static void AnalyzeSampleCandidates(
        JsonElement blocksNode,
        Dictionary<string, SampleAnchorStats> anchors,
        Dictionary<(string, string), SamplePairStats> pairs)
    {
        foreach (var blockNode in blocksNode.EnumerateArray())
        {
            if (!blockNode.TryGetProperty("ops", out var opsNode) || opsNode.ValueKind != JsonValueKind.Array)
                continue;
            var blockExecCount = blockNode.TryGetProperty("exec_count", out var execNode) ? (long)execNode.GetUInt64() : 0L;
            var blockStart = blockNode.TryGetProperty("start_eip_hex", out var startHexNode)
                ? startHexNode.GetString() ?? ""
                : $"0x{blockNode.GetProperty("start_eip").GetUInt32():x}";
            var seq = new List<string?>();
            foreach (var opNode in opsNode.EnumerateArray())
            {
                var raw = opNode.TryGetProperty("logic_func", out var lf) ? lf.GetString() : null;
                var sym = opNode.TryGetProperty("symbol", out var symNode) ? symNode.GetString() : null;
                var normalized = NormalizeCandidateName(raw ?? sym);
                seq.Add(normalized);
            }

            for (var i = 0; i < seq.Count; i++)
            {
                var anchor = seq[i];
                if (string.IsNullOrWhiteSpace(anchor) || !anchor.StartsWith("op::Op", StringComparison.Ordinal))
                    continue;

                if (!anchors.TryGetValue(anchor!, out var anchorStats))
                {
                    anchorStats = new SampleAnchorStats(anchor!);
                    anchors[anchor!] = anchorStats;
                }
                anchorStats.WeightedExecCount += blockExecCount;
                anchorStats.Occurrences += 1;
                anchorStats.UniqueBlockStarts.Add(blockStart);
                anchorStats.Semantics ??= InferSemantics(anchor!);
                if (anchorStats.ExampleBlocks.Count < 5)
                {
                    anchorStats.ExampleBlocks.Add(new Dictionary<string, object?>
                    {
                        ["start_eip_hex"] = blockStart,
                        ["exec_count"] = blockExecCount,
                        ["anchor_op_index"] = i,
                    });
                }

                if (i > 0 && !string.IsNullOrWhiteSpace(seq[i - 1]))
                {
                    var first = seq[i - 1]!;
                    var relation = ClassifyPair(first, anchor!, opsNode[i - 1], opsNode[i]);
                    if (relation is not null)
                    {
                        var pair = (first, anchor!);
                        if (!pairs.TryGetValue(pair, out var pairStats))
                        {
                            pairStats = new SamplePairStats(pair.Item1, pair.Item2, anchor!, "predecessor", relation);
                            pairs[pair] = pairStats;
                        }
                        pairStats.Add(blockExecCount, blockStart, i - 1, relation);
                    }
                }

                if (i + 1 < seq.Count && !string.IsNullOrWhiteSpace(seq[i + 1]))
                {
                    var second = seq[i + 1]!;
                    var relation = ClassifyPair(anchor!, second, opsNode[i], opsNode[i + 1]);
                    if (relation is null)
                        continue;

                    var pair = (anchor!, second);
                    if (!pairs.TryGetValue(pair, out var pairStats))
                    {
                        pairStats = new SamplePairStats(pair.Item1, pair.Item2, anchor!, "successor", relation);
                        pairs[pair] = pairStats;
                    }
                    pairStats.Add(blockExecCount, blockStart, i, relation);
                }
            }
        }
    }

    private static void MergeAnchorStats(
        Dictionary<string, AnchorAggregate> aggregate,
        string anchor,
        Dictionary<string, object?> sampleMeta,
        SampleAnchorStats sampleStats)
    {
        if (!aggregate.TryGetValue(anchor, out var entry))
        {
            entry = new AnchorAggregate(anchor);
            aggregate[anchor] = entry;
        }

        entry.WeightedExecCount += sampleStats.WeightedExecCount;
        entry.Occurrences += sampleStats.Occurrences;
        entry.UniqueBlockCount += sampleStats.UniqueBlockStarts.Count;
        entry.SampleCount += 1;
        entry.EngineCounts.Add((string?)sampleMeta["engine"]);
        entry.CaseCounts.Add((string?)sampleMeta["case"]);
        entry.Semantics ??= sampleStats.Semantics;
        if (entry.ExampleSources.Count < 5)
        {
            entry.ExampleSources.Add(new Dictionary<string, object?>
            {
                ["analysis_file"] = sampleMeta["analysis_file"],
                ["result_name"] = sampleMeta["result_name"],
                ["sample_name"] = sampleMeta["sample_name"],
                ["engine"] = sampleMeta["engine"],
                ["case"] = sampleMeta["case"],
                ["iteration"] = sampleMeta["iteration"],
                ["weighted_exec_count"] = sampleStats.WeightedExecCount,
                ["occurrences"] = sampleStats.Occurrences,
                ["unique_block_count"] = sampleStats.UniqueBlockStarts.Count,
                ["example_blocks"] = sampleStats.ExampleBlocks,
            });
        }
    }

    private static void MergePairStats(
        Dictionary<(string, string), PairAggregate> aggregate,
        (string, string) pair,
        Dictionary<string, object?> sampleMeta,
        SamplePairStats sampleStats)
    {
        if (!aggregate.TryGetValue(pair, out var entry))
        {
            entry = new PairAggregate(pair.Item1, pair.Item2, sampleStats.AnchorHandler, sampleStats.Direction, sampleStats.RelationKind, sampleStats.RelationPriority, sampleStats.SharedResources, sampleStats.LegalityNotes);
            aggregate[pair] = entry;
        }

        entry.WeightedExecCount += sampleStats.WeightedExecCount;
        entry.Occurrences += sampleStats.Occurrences;
        entry.UniqueBlockCount += sampleStats.UniqueBlockStarts.Count;
        entry.SampleCount += 1;
        entry.EngineCounts.Add((string?)sampleMeta["engine"]);
        entry.CaseCounts.Add((string?)sampleMeta["case"]);
        entry.SharedResourceVariants.Add(string.Join(",", sampleStats.SharedResources));
        entry.RelationKindVariants.Add(sampleStats.RelationKind);
        if (entry.ExampleSources.Count < 5)
        {
            entry.ExampleSources.Add(new Dictionary<string, object?>
            {
                ["analysis_file"] = sampleMeta["analysis_file"],
                ["result_name"] = sampleMeta["result_name"],
                ["sample_name"] = sampleMeta["sample_name"],
                ["engine"] = sampleMeta["engine"],
                ["case"] = sampleMeta["case"],
                ["iteration"] = sampleMeta["iteration"],
                ["weighted_exec_count"] = sampleStats.WeightedExecCount,
                ["occurrences"] = sampleStats.Occurrences,
                ["unique_block_count"] = sampleStats.UniqueBlockStarts.Count,
                ["example_blocks"] = sampleStats.ExampleBlocks,
            });
        }
    }

    private static Dictionary<string, object?> NormalizeAnchorEntry(AnchorAggregate entry)
    {
        return new Dictionary<string, object?>
        {
            ["anchor"] = entry.Anchor,
            ["anchor_display"] = OpShortName(entry.Anchor),
            ["weighted_exec_count"] = entry.WeightedExecCount,
            ["occurrences"] = entry.Occurrences,
            ["unique_block_count"] = entry.UniqueBlockCount,
            ["sample_count"] = entry.SampleCount,
            ["engine_counts"] = entry.EngineCounts.ToSortedDictionary(),
            ["case_counts"] = entry.CaseCounts.ToSortedDictionary(),
            ["example_sources"] = entry.ExampleSources,
            ["semantics"] = entry.Semantics?.ToDictionary(),
        };
    }

    private static Dictionary<string, object?> NormalizeCandidateEntry(
        PairAggregate entry,
        Dictionary<string, object?> anchorEntry,
        string scoreBasis,
        int rawWeight,
        int rarWeight,
        int wawWeight,
        int jccMultiplier,
        string jccMode)
    {
        var dominantSharedVariant = entry.SharedResourceVariants
            .OrderByDescending(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        var dominantRelation = entry.RelationKindVariants.TryGetValue("RAW", out var rawVariantCount) ? new KeyValuePair<string, int>("RAW", rawVariantCount) :
            entry.RelationKindVariants.TryGetValue("RAR/WAW", out var rarWawVariantCount) ? new KeyValuePair<string, int>("RAR/WAW", rarWawVariantCount) :
            entry.RelationKindVariants.OrderByDescending(kvp => kvp.Value).ThenBy(kvp => kvp.Key, StringComparer.Ordinal).FirstOrDefault();
        var dominantRelationKind = string.IsNullOrWhiteSpace(dominantRelation.Key) ? entry.RelationKind : dominantRelation.Key;
        var dominantRelationCount = dominantRelation.Value;
        var dominantShared = string.IsNullOrWhiteSpace(dominantSharedVariant.Key)
            ? entry.SharedResources.ToArray()
            : dominantSharedVariant.Key.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var dominantDepWeight = RelationDepWeight(dominantRelationKind, rawWeight, rarWeight, wawWeight);
        var baseFrequency = scoreBasis == "anchor"
            ? Convert.ToInt64(anchorEntry["weighted_exec_count"], CultureInfo.InvariantCulture)
            : entry.WeightedExecCount;
        var jccWeight = RelationJccWeight(new[] { entry.FirstHandler, entry.SecondHandler }, dominantRelationKind, jccMultiplier, jccMode);

        return new Dictionary<string, object?>
        {
            ["pair"] = new[] { entry.FirstHandler, entry.SecondHandler },
            ["pair_display"] = $"{entry.FirstHandler} -> {entry.SecondHandler}",
            ["ngram"] = new[] { entry.FirstHandler, entry.SecondHandler },
            ["ngram_display"] = $"{entry.FirstHandler} -> {entry.SecondHandler}",
            ["first_handler"] = entry.FirstHandler,
            ["second_handler"] = entry.SecondHandler,
            ["anchor_handler"] = entry.AnchorHandler,
            ["anchor_display"] = OpShortName(entry.AnchorHandler),
            ["direction"] = entry.Direction,
            ["relation_kind"] = dominantRelationKind,
            ["relation_priority"] = dominantDepWeight,
            ["shared_resources"] = dominantShared,
            ["weighted_exec_count"] = entry.WeightedExecCount,
            ["occurrences"] = entry.Occurrences,
            ["unique_block_count"] = entry.UniqueBlockCount,
            ["sample_count"] = entry.SampleCount,
            ["engine_counts"] = entry.EngineCounts.ToSortedDictionary(),
            ["case_counts"] = entry.CaseCounts.ToSortedDictionary(),
            ["shared_resource_variants"] = entry.SharedResourceVariants.ToSortedDictionary(),
            ["dominant_shared_resource_count"] = dominantSharedVariant.Value,
            ["dominant_shared_resource_ratio"] = entry.Occurrences > 0 ? (double)dominantSharedVariant.Value / entry.Occurrences : 0.0,
            ["relation_kind_variants"] = entry.RelationKindVariants.ToSortedDictionary(),
            ["dominant_relation_kind_count"] = dominantRelationCount,
            ["example_sources"] = entry.ExampleSources,
            ["legality_notes"] = entry.LegalityNotes,
            ["score"] = baseFrequency * dominantDepWeight * jccWeight,
            ["anchor_weighted_exec_count"] = anchorEntry["weighted_exec_count"],
            ["anchor_sample_count"] = anchorEntry["sample_count"],
            ["anchor_unique_block_count"] = anchorEntry["unique_block_count"],
            ["anchor_semantics"] = anchorEntry["semantics"],
            ["score_basis"] = scoreBasis,
            ["base_frequency"] = baseFrequency,
            ["jcc_weight"] = jccWeight,
        };
    }

    private static object CandidateSortKey(Dictionary<string, object?> entry)
    {
        return (
            Convert.ToInt64(entry["score"], CultureInfo.InvariantCulture),
            Convert.ToInt64(entry["weighted_exec_count"], CultureInfo.InvariantCulture),
            Convert.ToInt64(entry["sample_count"], CultureInfo.InvariantCulture),
            Convert.ToInt64(entry["occurrences"], CultureInfo.InvariantCulture),
            Convert.ToInt64(entry["unique_block_count"], CultureInfo.InvariantCulture));
    }

    private static string BuildMarkdown(
        IEnumerable<string> inputs,
        List<string> analysisFiles,
        List<Dictionary<string, object?>> includedSamples,
        List<Dictionary<string, object?>> skippedSamples,
        List<Dictionary<string, object?>> anchors,
        List<Dictionary<string, object?>> candidates,
        int anchorTop)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SuperOpcode Candidates");
        sb.AppendLine();
        sb.AppendLine($"- Inputs: {string.Join(", ", inputs)}");
        sb.AppendLine("- Strategy: global 2-gram scoring with hot anchors and dependency weights");
        sb.AppendLine($"- Analysis files discovered: {analysisFiles.Count}");
        sb.AppendLine($"- Included samples: {includedSamples.Count}");
        sb.AppendLine($"- Skipped samples: {skippedSamples.Count}");
        sb.AppendLine($"- Anchor display limit: {anchorTop}");
        sb.AppendLine($"- Candidate count: {candidates.Count}");
        sb.AppendLine();
        sb.AppendLine("## Top Anchors");
        sb.AppendLine();
        sb.AppendLine("| Rank | Anchor | Weighted Exec | Samples | Occurrences | Unique Blocks |");
        sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: |");
        foreach (var (anchor, index) in anchors.Select((a, i) => (a, i + 1)))
        {
            sb.AppendLine($"| {index} | `{anchor["anchor_display"]}` | {anchor["weighted_exec_count"]} | {anchor["sample_count"]} | {anchor["occurrences"]} | {anchor["unique_block_count"]} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Top Candidates");
        sb.AppendLine();
        sb.AppendLine("| Rank | Pair | Relation | Score | Anchor | Dir | Weighted Exec | Samples | Occurrences | Shared |");
        sb.AppendLine("| --- | --- | --- | ---: | --- | --- | ---: | ---: | ---: | --- |");
        foreach (var (candidate, index) in candidates.Select((c, i) => (c, i + 1)))
        {
            var shared = candidate["shared_resources"] is IEnumerable<string> sharedSet ? string.Join(", ", sharedSet) : "-";
            if (string.IsNullOrWhiteSpace(shared))
                shared = "-";
            sb.AppendLine($"| {index} | `{candidate["pair_display"]}` | {candidate["relation_kind"]} | {candidate["score"]} | `{candidate["anchor_display"]}` | {candidate["direction"]} | {candidate["weighted_exec_count"]} | {candidate["sample_count"]} | {candidate["occurrences"]} | {shared} |");
        }

        if (skippedSamples.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Skipped Samples");
            sb.AppendLine();
            sb.AppendLine("| Sample | Reason |");
            sb.AppendLine("| --- | --- |");
            foreach (var sample in skippedSamples)
            {
                var reason = string.Join("; ", (IEnumerable<string>)sample["reasons"]!);
                sb.AppendLine($"| `{sample["analysis_file"]}` | {reason} |");
            }
        }

        return sb.ToString();
    }

    private static string GenerateSuperopcodes(string inputPath, int top)
    {
        var data = JsonDocument.Parse(File.ReadAllText(inputPath, Encoding.UTF8)).RootElement;
        var candidates = new List<(string Op0, string Op1, long Wec, long Occ, string Relation, string Anchor, string Direction)>();

        if (data.TryGetProperty("candidates", out var candidatesNode) && candidatesNode.ValueKind == JsonValueKind.Array)
        {
            var seen = new HashSet<(string, string)>();
            foreach (var candidate in candidatesNode.EnumerateArray())
            {
                var pair = candidate.TryGetProperty("pair", out var pairNode) && pairNode.ValueKind == JsonValueKind.Array
                    ? pairNode.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
                    : Array.Empty<string>();
                if (pair.Length != 2)
                    continue;

                var op0 = CanonicalLogicName(pair[0]);
                var op1 = CanonicalLogicName(pair[1]);
                if (op0 is null || op1 is null)
                    continue;

                if (!seen.Add((op0, op1)))
                    continue;

                candidates.Add((
                    op0,
                    op1,
                    candidate.TryGetProperty("weighted_exec_count", out var wec) ? wec.GetInt64() : 0,
                    candidate.TryGetProperty("occurrences", out var occ) ? occ.GetInt64() : 0,
                    candidate.TryGetProperty("relation_kind", out var rel) ? rel.GetString() ?? "" : "",
                    candidate.TryGetProperty("anchor_display", out var anchor) ? anchor.GetString() ?? "" : "",
                    candidate.TryGetProperty("direction", out var dir) ? dir.GetString() ?? "" : ""));

                if (candidates.Count >= top)
                    break;
            }
        }

        var implIncludes = Directory.EnumerateFiles(Path.Combine("libfibercpu", "ops"), "*_impl.h")
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(file => $"#include \"../ops/{Path.GetFileName(file)}\"");

        var sb = new StringBuilder();
        sb.AppendLine("#include \"../ops.h\"");
        sb.AppendLine("#include \"../superopcodes.h\"");
        foreach (var include in implIncludes)
            sb.AppendLine(include);
        sb.AppendLine();
        sb.AppendLine("namespace fiberish {");
        sb.AppendLine();

        for (var index = 0; index < candidates.Count; index++)
        {
            var c = candidates[index];
            var handlerName = $"SuperOpcode_{index:000}_{SanitizeName(c.Op0[4..])}__{SanitizeName(c.Op1[4..])}";
            sb.AppendLine($"// weighted_exec_count={c.Wec} occurrences={c.Occ} relation={c.Relation} anchor={c.Anchor} direction={c.Direction}");
            sb.AppendLine("ATTR_PRESERVE_NONE int64_t " + handlerName + "(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,");
            sb.AppendLine("                                          mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {");
            sb.AppendLine($"    RUN_SUPEROPCODE_OP({c.Op0}, state, op, instr_limit, utlb, branch, flags_cache);");
            sb.AppendLine();
            sb.AppendLine("    DecodedOp* second_op = NextOp(op);");
            sb.AppendLine($"    RUN_SUPEROPCODE_OP({c.Op1}, state, second_op, instr_limit, utlb, branch, flags_cache);");
            sb.AppendLine();
            sb.AppendLine("    if (auto* next_op = NextOp(second_op)) {");
            sb.AppendLine("        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);");
            sb.AppendLine("    }");
            sb.AppendLine("    __builtin_unreachable();");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        sb.AppendLine("__attribute__((used)) HandlerFunc GeneratedFindSuperOpcode(const DecodedOp* ops) {");
        sb.AppendLine("    if (!ops) return nullptr;");
        for (var index = 0; index < candidates.Count; index++)
        {
            var c = candidates[index];
            var handlerName = $"SuperOpcode_{index:000}_{SanitizeName(c.Op0[4..])}__{SanitizeName(c.Op1[4..])}";
            sb.AppendLine($"    if (ops[0].handler == (HandlerFunc)DispatchWrapper<{c.Op0}> && ops[1].handler == (HandlerFunc)DispatchWrapper<{c.Op1}>) return {handlerName};");
        }
        sb.AppendLine("    return nullptr;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("}  // namespace fiberish");

        return sb.ToString();
    }

    private static string CanonicalLogicName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        if (name.StartsWith("op::Op", StringComparison.Ordinal))
            return name;
        if (name.StartsWith("Op", StringComparison.Ordinal))
            return "op::" + name;
        return "";
    }

    private static string SanitizeName(string name)
        => Regex.Replace(name, "[^A-Za-z0-9_]", "_");

    private static string OpShortName(string name)
    {
        var shortName = name.Split("::").Last();
        return shortName.StartsWith("Op", StringComparison.Ordinal) ? shortName[2..] : shortName;
    }

    private static OpSemantics InferSemantics(string name)
    {
        var shortName = OpShortName(name);
        var lower = shortName.ToLowerInvariant();
        var reads = new HashSet<string>(StringComparer.Ordinal);
        var writes = new HashSet<string>(StringComparer.Ordinal);
        var notes = new List<string>();
        var controlFlow = false;
        var memorySideEffect = false;

        if (lower.StartsWith("jcc", StringComparison.Ordinal) ||
            lower.StartsWith("jmp", StringComparison.Ordinal) ||
            lower.StartsWith("call", StringComparison.Ordinal) ||
            lower.StartsWith("ret", StringComparison.Ordinal) ||
            lower.StartsWith("loop", StringComparison.Ordinal) ||
            lower.StartsWith("iret", StringComparison.Ordinal) ||
            lower.StartsWith("sys", StringComparison.Ordinal))
        {
            controlFlow = true;
        }

        if (lower.StartsWith("jcc", StringComparison.Ordinal))
        {
            reads.Add("flags");
            notes.Add("conditional branch consumes flags");
        }
        else if (lower.StartsWith("cmov", StringComparison.Ordinal))
        {
            reads.Add("flags");
            reads.Add("gpr");
            writes.Add("gpr");
            notes.Add("cmov consumes flags and forwards a register value");
        }
        else if (lower.StartsWith("setcc", StringComparison.Ordinal))
        {
            reads.Add("flags");
            writes.Add("gpr");
            notes.Add("setcc consumes flags and defines a byte register");
        }

        if (StartsWithAny(lower, "cmp", "test", "bt", "btc", "btr", "bts", "comis", "ucomis"))
        {
            reads.Add("gpr");
            writes.Add("flags");
            notes.Add("compare/test family defines flags");
        }
        else if (StartsWithAny(lower, "adc", "sbb"))
        {
            reads.Add("gpr");
            reads.Add("flags");
            writes.Add("gpr");
            writes.Add("flags");
            notes.Add("adc/sbb both consume and define flags");
        }
        else if (StartsWithAny(lower, "add", "sub", "and", "or", "xor", "inc", "dec", "neg", "shl", "shr", "sar", "sal", "rol", "ror", "shld", "shrd"))
        {
            reads.Add("gpr");
            writes.Add("gpr");
            writes.Add("flags");
            notes.Add("ALU op updates register and flags");
        }

        var isLoad = lower.Contains("load", StringComparison.Ordinal);
        var isStore = lower.Contains("store", StringComparison.Ordinal);

        if (isLoad)
        {
            reads.Add("mem");
            writes.Add("gpr");
            notes.Add("load defines a register from memory");
        }
        if (isStore)
        {
            reads.Add("gpr");
            writes.Add("mem");
            memorySideEffect = true;
            notes.Add("store consumes a register and writes memory");
        }

        if (!isStore && StartsWithAny(lower, "mov", "lea", "movzx", "movsx", "pop"))
            writes.Add("gpr");
        if (!isLoad && StartsWithAny(lower, "mov", "push", "xchg", "cmp", "test"))
            reads.Add("gpr");
        if (lower.StartsWith("push", StringComparison.Ordinal))
        {
            writes.Add("mem");
            memorySideEffect = true;
        }
        if (lower.StartsWith("xchg", StringComparison.Ordinal))
            writes.Add("gpr");
        if (lower.StartsWith("movs", StringComparison.Ordinal))
        {
            reads.Add("gpr");
            reads.Add("mem");
            writes.Add("mem");
            memorySideEffect = true;
        }
        if (StartsWithAny(lower, "cmps", "scas"))
        {
            reads.Add("gpr");
            reads.Add("mem");
            writes.Add("flags");
            notes.Add("string compare defines flags");
        }

        return new OpSemantics(reads, writes, notes, controlFlow, memorySideEffect);
    }

    private static OpSemantics GetOpSemantics(JsonElement opNode, string normalizedName)
    {
        var opId = TryGetNullableInt(opNode, "op_id") ?? TryGetNullableIntFromHex(opNode, "op_id_hex");
        var modrm = opNode.TryGetProperty("modrm", out var modrmNode) && modrmNode.ValueKind == JsonValueKind.Number ? modrmNode.GetInt32() : 0;
        var meta = opNode.TryGetProperty("meta", out var metaNode) && metaNode.ValueKind == JsonValueKind.Number ? metaNode.GetInt32() : 0;
        var prefixes = opNode.TryGetProperty("prefixes", out var prefixesNode) && prefixesNode.ValueKind == JsonValueKind.Number ? prefixesNode.GetInt32() : 0;
        var eaDesc = 0;
        if (opNode.TryGetProperty("mem", out var memNode) && memNode.ValueKind == JsonValueKind.Object &&
            memNode.TryGetProperty("ea_desc", out var eaDescNode) && eaDescNode.ValueKind == JsonValueKind.Number)
        {
            eaDesc = eaDescNode.GetInt32();
        }

        var analyzed = AnalyzeDefUse(opId, modrm, meta, prefixes, eaDesc);
        if (analyzed is not null)
            return analyzed;

        if (!opNode.TryGetProperty("def_use", out var defUse) || defUse.ValueKind != JsonValueKind.Object)
            return InferSemantics(normalizedName);

        var reads = new HashSet<string>(ReadStringArray(defUse, "reads_data_gpr"), StringComparer.Ordinal);
        if (reads.Count == 0)
            reads.UnionWith(ReadStringArray(defUse, "reads_gpr"));
        var writes = new HashSet<string>(ReadStringArray(defUse, "writes_gpr"), StringComparer.Ordinal);
        var addrReads = ReadStringArray(defUse, "reads_addr_gpr");

        foreach (var flag in ReadStringArray(defUse, "reads_flags"))
            reads.Add($"flag:{flag}");
        foreach (var flag in ReadStringArray(defUse, "writes_flags"))
            writes.Add($"flag:{flag}");

        if (GetBool(defUse, "reads_memory"))
            reads.Add("mem");
        if (GetBool(defUse, "writes_memory"))
            writes.Add("mem");

        var fallback = InferSemantics(normalizedName);
        var controlFlow = fallback.ControlFlow;
        var memorySideEffect = GetBool(defUse, "writes_memory") || fallback.MemorySideEffect;
        var notes = ReadStringArray(defUse, "notes");
        foreach (var note in fallback.Notes)
        {
            if (!notes.Contains(note, StringComparer.Ordinal))
                notes.Add(note);
        }
        if (addrReads.Count > 0)
            notes.Add($"address regs: {string.Join(", ", addrReads)}");

        if (reads.Count == 0 && writes.Count == 0 && notes.Count == 0 && !memorySideEffect)
            return fallback;

        return new OpSemantics(reads, writes, notes, controlFlow, memorySideEffect);
    }

    private static OpSemantics? AnalyzeDefUse(int? opId, int modrm, int meta, int prefixes, int eaDesc)
    {
        _ = prefixes;
        if (opId is null || opId < 0)
            return null;

        var id = opId.Value;
        var hasModrm = (meta & 0x01) != 0;
        var hasMem = (meta & 0x02) != 0;
        var state = new DefUseState();

        if ((id >= 0x70 && id <= 0x7F) || (id >= 0x180 && id <= 0x18F) || (id >= 0xE0 && id <= 0xE3))
        {
            state.ReadsFlagsMask |= AllStatusFlagsMask;
            state.ControlFlow = true;
            if (id >= 0xE0 && id <= 0xE2)
            {
                state.ReadsGprMask |= RegMask(1);
                state.WritesGprMask |= RegMask(1);
                state.Note("loop family decrements ECX");
            }
            else if (id == 0xE3)
            {
                state.ReadsGprMask |= RegMask(1);
                state.Note("jecxz reads ECX");
            }
            state.Note("conditional control flow consumes flags");
            return state.ToOpSemantics();
        }

        if (id is 0xE9 or 0xEB)
        {
            state.Note("unconditional jump");
            return state.ToOpSemantics();
        }

        if (id >= 0x140 && id <= 0x14F && hasModrm)
        {
            state.ReadsFlagsMask |= AllStatusFlagsMask;
            ApplyRegOperand(state, modrm, "readwrite");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            state.Note("cmov reads destination because the old value is kept on false predicate");
            return state.ToOpSemantics();
        }

        if (id >= 0x190 && id <= 0x19F && hasModrm)
        {
            state.ReadsFlagsMask |= AllStatusFlagsMask;
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "write");
            return state.ToOpSemantics();
        }

        if ((id is 0x38 or 0x39 or 0x3A or 0x3B or 0x84 or 0x85) && hasModrm)
        {
            ApplyRegOperand(state, modrm, "read");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            state.WritesFlagsMask |= AllStatusFlagsMask;
            return state.ToOpSemantics();
        }

        if ((id is 0x89 or 0x200 or 0x201) && hasModrm)
        {
            ApplyRegOperand(state, modrm, "read");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "write");
            return state.ToOpSemantics();
        }
        if ((id is 0x8B or 0x202 or 0x203) && hasModrm)
        {
            ApplyRegOperand(state, modrm, "write");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            return state.ToOpSemantics();
        }
        if (id == 0x88 && hasModrm)
        {
            ApplyRegOperand(state, modrm, "read");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "write");
            return state.ToOpSemantics();
        }
        if (id == 0x8A && hasModrm)
        {
            ApplyRegOperand(state, modrm, "write");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            return state.ToOpSemantics();
        }
        if ((id is 0xC6 or 0xC7) && hasModrm)
        {
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "write");
            return state.ToOpSemantics();
        }
        if (id == 0x87 && hasModrm)
        {
            ApplyRegOperand(state, modrm, "readwrite");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "readwrite");
            return state.ToOpSemantics();
        }

        if (id >= 0x91 && id <= 0x97)
        {
            var reg = id - 0x90;
            state.ReadsGprMask |= RegMask(0, reg);
            state.WritesGprMask |= RegMask(0, reg);
            return state.ToOpSemantics();
        }

        if (id == 0x8D && hasModrm)
        {
            ApplyRegOperand(state, modrm, "write");
            state.ReadsAddrGprMask |= DecodeMemoryAddressRegs(eaDesc);
            state.Note("lea reads effective-address registers without touching memory");
            return state.ToOpSemantics();
        }

        if ((id is 0x1B6 or 0x1B7 or 0x1BE or 0x1BF) && hasModrm)
        {
            ApplyRegOperand(state, modrm, "write");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            return state.ToOpSemantics();
        }

        if ((id is 0x00 or 0x01 or 0x08 or 0x09 or 0x10 or 0x11 or 0x18 or 0x19 or 0x20 or 0x21 or 0x28 or 0x29 or 0x30 or 0x31) && hasModrm)
        {
            ApplyRegOperand(state, modrm, "read");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "readwrite");
            state.WritesFlagsMask |= AllStatusFlagsMask;
            if (id is 0x10 or 0x11 or 0x18 or 0x19)
                state.ReadsFlagsMask |= CfBit;
            return state.ToOpSemantics();
        }
        if ((id is 0x02 or 0x03 or 0x0A or 0x0B or 0x12 or 0x13 or 0x1A or 0x1B or 0x22 or 0x23 or 0x2A or 0x2B or 0x32 or 0x33) && hasModrm)
        {
            ApplyRegOperand(state, modrm, "readwrite");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            state.WritesFlagsMask |= AllStatusFlagsMask;
            if (id is 0x12 or 0x13 or 0x1A or 0x1B)
                state.ReadsFlagsMask |= CfBit;
            return state.ToOpSemantics();
        }

        if ((id is 0x80 or 0x81 or 0x83) && hasModrm)
        {
            var subop = (modrm >> 3) & 7;
            if (subop == 7)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            }
            else
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "readwrite");
                if (subop is 2 or 3)
                    state.ReadsFlagsMask |= CfBit;
            }
            state.WritesFlagsMask |= AllStatusFlagsMask;
            return state.ToOpSemantics();
        }

        if ((id is 0xC0 or 0xC1 or 0xD0 or 0xD1 or 0xD2 or 0xD3) && hasModrm)
        {
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "readwrite");
            if (id is 0xD2 or 0xD3)
            {
                state.ReadsGprMask |= RegMask(1);
                state.Note("shift count comes from CL");
            }
            state.WritesFlagsMask |= AllStatusFlagsMask;
            return state.ToOpSemantics();
        }

        if ((id is 0xF6 or 0xF7) && hasModrm)
        {
            var subop = (modrm >> 3) & 7;
            if (subop is 0 or 1)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
                state.WritesFlagsMask |= AllStatusFlagsMask;
            }
            else if (subop == 2)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "readwrite");
            }
            else if (subop == 3)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "readwrite");
                state.WritesFlagsMask |= AllStatusFlagsMask;
            }
            else if (subop is 4 or 5)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
                state.ReadsGprMask |= RegMask(0);
                state.WritesGprMask |= RegMask(0, 2);
                state.WritesFlagsMask |= AllStatusFlagsMask;
            }
            else if (subop is 6 or 7)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
                state.ReadsGprMask |= RegMask(0, 2);
                state.WritesGprMask |= RegMask(0, 2);
            }
            return state.ToOpSemantics();
        }

        if ((id is 0x1A3 or 0x1AB or 0x1B3 or 0x1BB) && hasModrm)
        {
            ApplyRegOperand(state, modrm, "read");
            var rmRole = (id is 0x1AB or 0x1B3 or 0x1BB) ? "readwrite" : "read";
            ApplyRmOperand(state, modrm, hasMem, eaDesc, rmRole);
            state.WritesFlagsMask |= CfBit;
            return state.ToOpSemantics();
        }

        if (id == 0x1AF && hasModrm)
        {
            ApplyRegOperand(state, modrm, "readwrite");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            state.WritesFlagsMask |= CfBit | OfBit;
            return state.ToOpSemantics();
        }

        if (id >= 0x50 && id <= 0x57)
        {
            state.ReadsGprMask |= RegMask(id - 0x50, 4);
            state.WritesGprMask |= RegMask(4);
            state.WritesMemory = true;
            return state.ToOpSemantics();
        }
        if (id >= 0x58 && id <= 0x5F)
        {
            state.ReadsGprMask |= RegMask(4);
            state.WritesGprMask |= RegMask(id - 0x58, 4);
            state.ReadsMemory = true;
            return state.ToOpSemantics();
        }
        if (id is 0xE8 or 0xC2 or 0xC3)
        {
            state.ReadsGprMask |= RegMask(4);
            state.WritesGprMask |= RegMask(4);
            state.ReadsMemory = id is 0xC2 or 0xC3;
            state.WritesMemory = id == 0xE8;
            return state.ToOpSemantics();
        }
        if (id == 0x6A)
        {
            state.ReadsGprMask |= RegMask(4);
            state.WritesGprMask |= RegMask(4);
            state.WritesMemory = true;
            return state.ToOpSemantics();
        }

        if (id == 0xFF && hasModrm)
        {
            var subop = (modrm >> 3) & 7;
            if (subop is 0 or 1)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "readwrite");
                state.WritesFlagsMask |= AllStatusFlagsMask & ~CfBit;
            }
            else if (subop == 2)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
                state.ReadsGprMask |= RegMask(4);
                state.WritesGprMask |= RegMask(4);
                state.WritesMemory = true;
            }
            else if (subop == 4)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            }
            else if (subop == 6)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
                state.ReadsGprMask |= RegMask(4);
                state.WritesGprMask |= RegMask(4);
                state.WritesMemory = true;
            }
            return state.ToOpSemantics();
        }

        if (id >= 0xB8 && id <= 0xBF)
        {
            state.WritesGprMask |= RegMask(id - 0xB8);
            return state.ToOpSemantics();
        }
        if (id >= 0xB0 && id <= 0xB7)
        {
            state.WritesGprMask |= RegMask(id - 0xB0);
            return state.ToOpSemantics();
        }

        if (id is 0x05 or 0x0D or 0x15 or 0x1D or 0x25 or 0x2D or 0x35 or 0x3D or 0xA9)
        {
            state.ReadsGprMask |= RegMask(0);
            if (id is not 0x3D and not 0xA9)
                state.WritesGprMask |= RegMask(0);
            state.WritesFlagsMask |= AllStatusFlagsMask;
            if (id is 0x15 or 0x1D)
                state.ReadsFlagsMask |= CfBit;
            return state.ToOpSemantics();
        }
        if (id is 0x3C or 0xA8)
        {
            state.ReadsGprMask |= RegMask(0);
            state.WritesFlagsMask |= AllStatusFlagsMask;
            return state.ToOpSemantics();
        }

        if (id >= 0x40 && id <= 0x47)
        {
            var reg = id - 0x40;
            state.ReadsGprMask |= RegMask(reg);
            state.WritesGprMask |= RegMask(reg);
            state.WritesFlagsMask |= AllStatusFlagsMask & ~CfBit;
            return state.ToOpSemantics();
        }
        if (id >= 0x48 && id <= 0x4F)
        {
            var reg = id - 0x48;
            state.ReadsGprMask |= RegMask(reg);
            state.WritesGprMask |= RegMask(reg);
            state.WritesFlagsMask |= AllStatusFlagsMask & ~CfBit;
            return state.ToOpSemantics();
        }

        if (id == 0x99)
        {
            state.ReadsGprMask |= RegMask(0);
            state.WritesGprMask |= RegMask(2);
            return state.ToOpSemantics();
        }
        if (id == 0xFC)
        {
            state.ReadsFlagsMask &= 0;
            state.WritesFlagsMask |= 1u << 10;
            state.Note("cld clears direction flag");
            return state.ToOpSemantics();
        }

        if (id is 0xA0 or 0xA1)
        {
            state.WritesGprMask |= RegMask(0);
            state.ReadsMemory = true;
            return state.ToOpSemantics();
        }
        if (id is 0xA2 or 0xA3)
        {
            state.ReadsGprMask |= RegMask(0);
            state.WritesMemory = true;
            return state.ToOpSemantics();
        }

        return null;
    }

    private static uint RegMask(params int[] regs)
    {
        var mask = 0u;
        foreach (var reg in regs)
        {
            if (reg >= 0 && reg < GprNames.Length)
                mask |= 1u << reg;
        }
        return mask;
    }

    private static uint DecodeMemoryAddressRegs(int eaDesc)
    {
        var mask = 0u;
        var baseOffset = eaDesc & 0x3F;
        var indexOffset = (eaDesc >> 6) & 0x3F;

        if (baseOffset != 32)
            mask |= 1u << (baseOffset / 4);
        if (indexOffset != 32)
            mask |= 1u << (indexOffset / 4);
        return mask;
    }

    private static void ApplyRmOperand(DefUseState state, int modrm, bool hasMem, int eaDesc, string role)
    {
        if (role == "none")
            return;

        if (hasMem)
        {
            state.ReadsAddrGprMask |= DecodeMemoryAddressRegs(eaDesc);
            if (role is "read" or "readwrite")
                state.ReadsMemory = true;
            if (role is "write" or "readwrite")
                state.WritesMemory = true;
            return;
        }

        var rm = modrm & 7;
        if (role is "read" or "readwrite")
            state.ReadsGprMask |= RegMask(rm);
        if (role is "write" or "readwrite")
            state.WritesGprMask |= RegMask(rm);
    }

    private static void ApplyRegOperand(DefUseState state, int modrm, string role)
    {
        if (role == "none")
            return;

        var reg = (modrm >> 3) & 7;
        if (role is "read" or "readwrite")
            state.ReadsGprMask |= RegMask(reg);
        if (role is "write" or "readwrite")
            state.WritesGprMask |= RegMask(reg);
    }

    private static List<string> MaskToRegNames(uint mask)
    {
        var names = new List<string>();
        for (var i = 0; i < GprNames.Length; i++)
        {
            if ((mask & (1u << i)) != 0)
                names.Add(GprNames[i]);
        }
        return names;
    }

    private static List<string> MaskToFlagNames(uint mask)
    {
        var flags = new List<string>();
        if ((mask & CfBit) != 0) flags.Add("cf");
        if ((mask & PfBit) != 0) flags.Add("pf");
        if ((mask & AfBit) != 0) flags.Add("af");
        if ((mask & ZfBit) != 0) flags.Add("zf");
        if ((mask & SfBit) != 0) flags.Add("sf");
        if ((mask & OfBit) != 0) flags.Add("of");
        return flags;
    }

    private sealed class DefUseState
    {
        public uint ReadsGprMask { get; set; }
        public uint ReadsAddrGprMask { get; set; }
        public uint WritesGprMask { get; set; }
        public uint ReadsFlagsMask { get; set; }
        public uint WritesFlagsMask { get; set; }
        public bool ReadsMemory { get; set; }
        public bool WritesMemory { get; set; }
        public bool ControlFlow { get; set; }
        public List<string>? Notes { get; set; }

        public void Note(string message)
        {
            Notes ??= new List<string>();
            Notes.Add(message);
        }

        public OpSemantics? ToOpSemantics()
        {
            var combinedReads = ReadsGprMask | ReadsAddrGprMask;
            var reads = new HashSet<string>(MaskToRegNames(combinedReads), StringComparer.Ordinal);
            var writes = new HashSet<string>(MaskToRegNames(WritesGprMask), StringComparer.Ordinal);
            foreach (var flag in MaskToFlagNames(ReadsFlagsMask))
                reads.Add($"flag:{flag}");
            foreach (var flag in MaskToFlagNames(WritesFlagsMask))
                writes.Add($"flag:{flag}");
            if (ReadsMemory)
                reads.Add("mem");
            if (WritesMemory)
                writes.Add("mem");

            var notes = Notes ?? new List<string>();
            if (reads.Count == 0 && writes.Count == 0 && notes.Count == 0 && !WritesMemory && !ControlFlow)
                return null;

            var outputNotes = new List<string>(notes);
            return new OpSemantics(reads, writes, outputNotes, ControlFlow, WritesMemory);
        }
    }

    private static int? TryGetNullableInt(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var node))
            return null;
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var value))
            return value;
        if (node.ValueKind == JsonValueKind.String && int.TryParse(node.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return value;
        return null;
    }

    private static int? TryGetNullableIntFromHex(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.String)
            return null;
        var text = node.GetString();
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("0x", StringComparison.Ordinal))
            return null;
        return int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static PairRelation? ClassifyPair(string firstName, string secondName, JsonElement firstOp, JsonElement secondOp)
    {
        var firstInfo = GetOpSemantics(firstOp, firstName);
        var secondInfo = GetOpSemantics(secondOp, secondName);
        var sharedRaw = firstInfo.Writes.Intersect(secondInfo.Reads, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var sharedRar = firstInfo.Reads.Intersect(secondInfo.Reads, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var sharedWaw = firstInfo.Writes.Intersect(secondInfo.Writes, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (sharedRaw.Length == 0 && sharedRar.Length == 0 && sharedWaw.Length == 0)
            return null;

        string relationKind;
        string[] sharedResources;
        var depWeight = 1;
        if (sharedRaw.Length > 0)
        {
            relationKind = "RAW";
            sharedResources = sharedRaw;
            depWeight = 2;
        }
        else if (sharedRar.Length > 0 && sharedWaw.Length > 0)
        {
            relationKind = "RAR/WAW";
            sharedResources = sharedRar.Concat(sharedWaw).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }
        else if (sharedRar.Length > 0)
        {
            relationKind = "RAR";
            sharedResources = sharedRar;
        }
        else
        {
            relationKind = "WAW";
            sharedResources = sharedWaw;
        }

        var legalityNotes = new List<string>();
        if (secondInfo.ControlFlow)
            legalityNotes.Add("second op is control-flow and needs strict mid-exit handling");
        if (firstInfo.MemorySideEffect || secondInfo.MemorySideEffect)
            legalityNotes.Add("pair touches memory side effects and should keep restart semantics conservative");
        if (!string.Equals(relationKind, "RAW", StringComparison.Ordinal))
            legalityNotes.Add("pair is non-RAW and may only be profitable when repeated reads or write coalescing can be shared");

        return new PairRelation(relationKind, depWeight, sharedResources, firstInfo, secondInfo, legalityNotes.ToArray());
    }

    private static bool IsJccPair(IEnumerable<string> pair)
        => pair.Any(name => OpShortName(name).StartsWith("Jcc", StringComparison.Ordinal));

    private static int RelationDepWeight(string relationKind, int rawWeight, int rarWeight, int wawWeight)
        => relationKind switch
        {
            "RAW" => rawWeight,
            "RAR" => rarWeight,
            "WAW" => wawWeight,
            _ => Math.Max(rarWeight, wawWeight)
        };

    private static int RelationJccWeight(IEnumerable<string> pair, string relationKind, int jccMultiplier, string jccMode)
    {
        if (jccMultiplier <= 1 || !IsJccPair(pair))
            return 1;
        if (string.Equals(jccMode, "none", StringComparison.Ordinal))
            return 1;
        if (string.Equals(jccMode, "raw-only", StringComparison.Ordinal) && !string.Equals(relationKind, "RAW", StringComparison.Ordinal))
            return 1;
        return jccMultiplier;
    }

    private static bool StartsWithAny(string text, params string[] prefixes)
        => prefixes.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal));

    private static List<string> ReadStringArray(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.Array)
            return new List<string>();
        var values = new List<string>();
        foreach (var item in node.EnumerateArray())
        {
            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value);
        }
        return values;
    }

    private static bool GetBool(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var node) && node.ValueKind is JsonValueKind.True or JsonValueKind.False && node.GetBoolean();

    private static string? TryFindRepoRoot(string start)
    {
        if (string.IsNullOrWhiteSpace(start))
            return null;

        var directory = new DirectoryInfo(Path.GetFullPath(start));
        if (!directory.Exists && directory.Parent is not null)
            directory = directory.Parent;

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Podish.slnx")) ||
                File.Exists(Path.Combine(directory.FullName, "Podish.sln")) ||
                File.Exists(Path.Combine(directory.FullName, "Podish.Cli", "Podish.Cli.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static double GetDoubleValue(string[] args, string option, double defaultValue)
        => double.TryParse(GetValue(args, option), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) ? value : defaultValue;

    private static CommandResult RunCommand(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        bool captureOutput,
        IReadOnlySet<int>? okExitCodes = null,
        IDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start command: {fileName}");
        var stdout = captureOutput ? process.StandardOutput.ReadToEnd() : string.Empty;
        var stderr = captureOutput ? process.StandardError.ReadToEnd() : string.Empty;
        process.WaitForExit();

        var allowed = okExitCodes ?? new HashSet<int> { 0 };
        if (!allowed.Contains(process.ExitCode))
        {
            var rendered = ShellJoin(new[] { fileName }.Concat(arguments));
            var detail = captureOutput
                ? $"{rendered}{Environment.NewLine}{stdout}{stderr}".Trim()
                : rendered;
            throw new InvalidOperationException($"command failed with exit code {process.ExitCode}: {detail}");
        }

        return new CommandResult(process.ExitCode, stdout, stderr);
    }

    private static void RunCommandChecked(string fileName, IEnumerable<string> arguments, string? workingDirectory, IDictionary<string, string>? environment = null)
        => RunCommand(fileName, arguments, workingDirectory, captureOutput: false, environment: environment);

    private static string RunCommandCheckedCapture(string fileName, IEnumerable<string> arguments, string? workingDirectory, IDictionary<string, string>? environment = null)
        => RunCommand(fileName, arguments, workingDirectory, captureOutput: true, environment: environment).Stdout;

    private static List<ProfileJitBlockEntry> LoadProfileJitMaps(string? mapDir)
    {
        if (string.IsNullOrWhiteSpace(mapDir) || !Directory.Exists(mapDir))
            return new List<ProfileJitBlockEntry>();

        var entries = new List<ProfileJitBlockEntry>();
        foreach (var path in Directory.EnumerateFiles(mapDir, "jit_*.map.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = doc.RootElement;
            var ops = new List<ProfileJitOpEntry>();
            if (root.TryGetProperty("ops", out var opsNode) && opsNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var op in opsNode.EnumerateArray())
                {
                    ops.Add(new ProfileJitOpEntry(
                        op.TryGetProperty("index", out var indexNode) ? indexNode.GetInt32() : 0,
                        op.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "",
                        op.TryGetProperty("runtime_start", out var opRuntimeNode) ? opRuntimeNode.GetInt64() : 0,
                        op.TryGetProperty("offset", out var offsetNode) ? offsetNode.GetInt32() : 0));
                }
            }

            entries.Add(new ProfileJitBlockEntry(
                root.TryGetProperty("guest_block_start_eip", out var blockNode) ? blockNode.GetInt64() : 0,
                root.TryGetProperty("runtime_start", out var runtimeNode) ? runtimeNode.GetInt64() : 0,
                root.TryGetProperty("code_size", out var sizeNode) ? sizeNode.GetInt32() : 0,
                ops));
        }

        return entries.OrderBy(entry => entry.RuntimeStart).ToList();
    }

    private static (string Symbol, string? BinaryName) AnnotateJitSymbol(string symbol, List<ProfileJitBlockEntry> jitMaps)
    {
        if (!TryParseHexSymbol(symbol, out var address))
            return (symbol, null);

        foreach (var block in jitMaps)
        {
            if (address < block.RuntimeStart || address >= block.RuntimeStart + block.CodeSize)
                continue;

            var blockOffset = address - block.RuntimeStart;
            ProfileJitOpEntry? currentOp = null;
            foreach (var op in block.Ops)
            {
                if (op.RuntimeStart <= address)
                    currentOp = op;
                else
                    break;
            }

            if (currentOp is null)
                return ($"jit:block@{block.GuestBlockStartEip:x8}+0x{blockOffset:x}", "jit");

            var opOffset = address - currentOp.RuntimeStart;
            return ($"jit:block@{block.GuestBlockStartEip:x8}+0x{blockOffset:x} op[{currentOp.Index}] {currentOp.Name}+0x{opOffset:x}", "jit");
        }

        return (symbol, null);
    }

    private static bool TryParseHexSymbol(string symbol, out long value)
    {
        value = 0;
        if (!symbol.StartsWith("0x", StringComparison.Ordinal))
            return false;
        return long.TryParse(symbol[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static string NormalizeCandidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        var text = name.Trim();
        if (text.Contains("SuperOpcode_", StringComparison.Ordinal))
            return "";
        if (text.StartsWith("op::Op", StringComparison.Ordinal))
            return text;
        if (text.StartsWith("Op", StringComparison.Ordinal))
            return "op::" + text;
        var wrapper = DispatchWrapperRegex.Match(text);
        if (wrapper.Success)
            return wrapper.Groups[1].Value;
        var direct = DirectLogicRegex.Match(text);
        if (direct.Success)
            return direct.Groups[1].Value;
        var mangled = MangledLogicRegex.Match(text);
        if (mangled.Success)
            return "op::" + mangled.Groups[1].Value;
        return "";
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

    private static List<string> GetPositionalArgs(string[] args)
    {
        var positionals = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    i++;
                continue;
            }

            positionals.Add(args[i]);
        }

        return positionals;
    }

    private static string RequireValue(string[] args, string option, int positionalIndex = -1)
    {
        var value = GetValue(args, option);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        var positional = GetPositionalArgs(args).ToArray();
        if (positionalIndex >= 0 && positionalIndex < positional.Length)
            return positional[positionalIndex];

        throw new ArgumentException($"Missing required value for {option}");
    }

    private static (string dumpFile, string? summaryFile, string defaultOutput) ResolveInputPaths(string inputPath, bool _ = false)
        => ResolveInputPaths(inputPath);

    private static string DefaultOutputFromInput(string inputPath)
        => Directory.Exists(inputPath) ? Path.Combine(inputPath, "blocks_analysis.json") : "blocks_analysis.json";

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

    private static bool ShouldSkipAnalysis(JsonElement root, out List<string> reasons)
    {
        reasons = new List<string>();
        if (!root.TryGetProperty("validation", out var validation) || validation.ValueKind != JsonValueKind.Object)
        {
            reasons.Add("missing validation");
            return true;
        }

        if (validation.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
        {
            foreach (var warning in warnings.EnumerateArray().Select(x => x.GetString() ?? ""))
            {
                if (warning.Contains("parsed blocks are empty", StringComparison.Ordinal) ||
                    warning.Contains("dump/export format likely drifted", StringComparison.Ordinal) ||
                    warning.Contains("truncated", StringComparison.Ordinal))
                {
                    reasons.Add(warning);
                }
            }
        }

        if (!root.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array || blocks.GetArrayLength() == 0)
        {
            reasons.Add("blocks list is empty");
            return true;
        }

        var symbolCount = 0;
        var unknownCount = 0;
        foreach (var block in blocks.EnumerateArray().Take(200))
        {
            if (!block.TryGetProperty("ops", out var ops) || ops.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var op in ops.EnumerateArray().Take(32))
            {
                var symbol = op.TryGetProperty("symbol", out var sym) ? (sym.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(symbol))
                    continue;
                symbolCount++;
                if (symbol == "<unknown>" || symbol.StartsWith("func_", StringComparison.Ordinal))
                    unknownCount++;
            }
        }

        if (symbolCount > 0 && (double)unknownCount / symbolCount >= 0.5)
            reasons.Add($"symbol resolution looks invalid ({unknownCount}/{symbolCount} sampled ops unresolved)");

        return reasons.Count > 0;
    }

    private static Dictionary<string, object?> InferSampleMetadata(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sampleName = Path.GetFileName(Path.GetDirectoryName(fullPath) ?? "");
        var resultName = parts.Length >= 2 ? parts[^2] : "";
        var metadata = new Dictionary<string, object?>
        {
            ["analysis_file"] = fullPath,
            ["sample_name"] = sampleName,
            ["result_name"] = resultName,
            ["engine"] = null,
            ["case"] = null,
            ["iteration"] = null,
        };

        var guestIdx = Array.IndexOf(parts, "guest-stats");
        if (guestIdx >= 0 && guestIdx + 1 < parts.Length)
        {
            sampleName = parts[guestIdx + 1];
            metadata["sample_name"] = sampleName;
            var split = sampleName.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (split.Length >= 3)
            {
                metadata["engine"] = split[0];
                metadata["case"] = split[1];
                if (int.TryParse(split[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iteration))
                    metadata["iteration"] = iteration;
                else
                    metadata["iteration"] = split[2];
            }

            if (guestIdx >= 1)
                metadata["result_name"] = parts[guestIdx - 1];
        }

        return metadata;
    }

    private sealed class SampleAnchorStats
    {
        public SampleAnchorStats(string anchor) => Anchor = anchor;
        public string Anchor { get; }
        public long WeightedExecCount { get; set; }
        public long Occurrences { get; set; }
        public HashSet<string> UniqueBlockStarts { get; } = new(StringComparer.Ordinal);
        public List<Dictionary<string, object?>> ExampleBlocks { get; } = new();
        public OpSemantics? Semantics { get; set; }
    }

    private sealed class SamplePairStats
    {
        public SamplePairStats(string first, string second, string anchor, string direction, PairRelation relation)
        {
            FirstHandler = first;
            SecondHandler = second;
            AnchorHandler = anchor;
            Direction = direction;
            RelationKind = relation.RelationKind;
            RelationPriority = relation.RelationPriority;
            SharedResources = relation.SharedResources.ToArray();
            LegalityNotes = relation.LegalityNotes.ToArray();
        }

        public string FirstHandler { get; }
        public string SecondHandler { get; }
        public string AnchorHandler { get; }
        public string Direction { get; }
        public string RelationKind { get; }
        public int RelationPriority { get; }
        public string[] SharedResources { get; }
        public string[] LegalityNotes { get; }
        public long WeightedExecCount { get; set; }
        public long Occurrences { get; set; }
        public HashSet<string> UniqueBlockStarts { get; } = new(StringComparer.Ordinal);
        public CountingMap SharedResourceVariants { get; } = new();
        public CountingMap RelationKindVariants { get; } = new();
        public List<Dictionary<string, object?>> ExampleBlocks { get; } = new();

        public void Add(long blockExecCount, string blockStart, int startOpIndex, PairRelation relation)
        {
            WeightedExecCount += blockExecCount;
            Occurrences += 1;
            UniqueBlockStarts.Add(blockStart);
            SharedResourceVariants.Add(string.Join(",", relation.SharedResources));
            RelationKindVariants.Add(relation.RelationKind);
            if (ExampleBlocks.Count < 3)
            {
                ExampleBlocks.Add(new Dictionary<string, object?>
                {
                    ["start_eip_hex"] = blockStart,
                    ["exec_count"] = blockExecCount,
                    ["start_op_index"] = startOpIndex,
                    ["anchor_handler"] = AnchorHandler,
                    ["direction"] = Direction,
                });
            }
        }
    }

    private sealed class AnchorAggregate
    {
        public AnchorAggregate(string anchor) => Anchor = anchor;
        public string Anchor { get; }
        public long WeightedExecCount { get; set; }
        public long Occurrences { get; set; }
        public long UniqueBlockCount { get; set; }
        public long SampleCount { get; set; }
        public CountingMap EngineCounts { get; } = new();
        public CountingMap CaseCounts { get; } = new();
        public List<Dictionary<string, object?>> ExampleSources { get; } = new();
        public OpSemantics? Semantics { get; set; }
    }

    private sealed class PairAggregate
    {
        public PairAggregate(string first, string second, string anchor, string direction, string relationKind, int relationPriority, IEnumerable<string> sharedResources, IEnumerable<string> legalityNotes)
        {
            FirstHandler = first;
            SecondHandler = second;
            AnchorHandler = anchor;
            Direction = direction;
            RelationKind = relationKind;
            RelationPriority = relationPriority;
            SharedResources = sharedResources.ToArray();
            LegalityNotes = legalityNotes.ToArray();
        }

        public string FirstHandler { get; }
        public string SecondHandler { get; }
        public string AnchorHandler { get; }
        public string Direction { get; }
        public string RelationKind { get; }
        public int RelationPriority { get; }
        public string[] SharedResources { get; }
        public string[] LegalityNotes { get; }
        public long WeightedExecCount { get; set; }
        public long Occurrences { get; set; }
        public long UniqueBlockCount { get; set; }
        public long SampleCount { get; set; }
        public CountingMap EngineCounts { get; } = new();
        public CountingMap CaseCounts { get; } = new();
        public CountingMap SharedResourceVariants { get; } = new();
        public CountingMap RelationKindVariants { get; } = new();
        public List<Dictionary<string, object?>> ExampleSources { get; } = new();
    }

    private sealed class CountingMap : Dictionary<string, int>
    {
        public CountingMap() : base(StringComparer.Ordinal) { }

        public void Add(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;
            this[key] = TryGetValue(key, out var count) ? count + 1 : 1;
        }

        public Dictionary<string, int> ToSortedDictionary()
            => this.OrderBy(kvp => kvp.Key, StringComparer.Ordinal).ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
    }

    private sealed class NgramAggregate
    {
        public NgramAggregate(string[] ngram) => Ngram = ngram.ToArray();
        public string[] Ngram { get; }
        public long WeightedExecCount { get; set; }
        public long Occurrences { get; set; }
        public HashSet<uint> UniqueBlockStarts { get; } = new();
        public List<Dictionary<string, object?>> ExampleBlocks { get; } = new();
    }

    private sealed record OpSemantics(
        IReadOnlyCollection<string> Reads,
        IReadOnlyCollection<string> Writes,
        IReadOnlyList<string> Notes,
        bool ControlFlow,
        bool MemorySideEffect)
    {
        public Dictionary<string, object?> ToDictionary() => new()
        {
            ["reads"] = Reads.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            ["writes"] = Writes.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            ["notes"] = Notes.ToArray(),
            ["control_flow"] = ControlFlow,
            ["memory_side_effect"] = MemorySideEffect
        };
    }

    private sealed record PairRelation(
        string RelationKind,
        int RelationPriority,
        string[] SharedResources,
        OpSemantics FirstSemantics,
        OpSemantics SecondSemantics,
        string[] LegalityNotes);

    private enum ProfileBackendKind
    {
        XcTrace,
        Perf
    }

    private interface IProfileBackend
    {
        List<string> BuildRecordCommand(ProfileRecordOptions options, string tracePath, List<string> podishLaunch);
        void Record(ProfileRecordOptions options, string runBinary, string tracePath, Dictionary<string, string> environment);
        ProfileAnalysisResult Analyze(ProfileAnalyzeOptions options, string outDir);
    }

    private sealed class XcTraceProfileBackend : IProfileBackend
    {
        public List<string> BuildRecordCommand(ProfileRecordOptions options, string tracePath, List<string> podishLaunch)
        {
            if (!CommandExists("xcrun"))
                throw new InvalidOperationException("xctrace backend requires xcrun on macOS.");

            return new List<string>
            {
                "xcrun",
                "xctrace",
                "record",
                "--template",
                "Time Profiler",
                "--time-limit",
                $"{options.TimeLimitSeconds}s",
                "--output",
                tracePath,
                "--launch",
                "--"
            }.Concat(podishLaunch).ToList();
        }

        public void Record(ProfileRecordOptions options, string runBinary, string tracePath, Dictionary<string, string> environment)
        {
            var command = BuildRecordCommand(options, tracePath, BuildPodishLaunchCommand(runBinary, options.Rootfs, options.Iterations, options.BenchCase));
            var result = RunCommand(command[0], command.Skip(1), RepoRoot(), captureOutput: false, okExitCodes: new HashSet<int> { 0, 1 }, environment: environment);
            if (result.ExitCode != 0 && !Directory.Exists(tracePath))
                throw new InvalidOperationException($"xctrace recording failed and no trace was produced: {tracePath}");
        }

        public ProfileAnalysisResult Analyze(ProfileAnalyzeOptions options, string outDir)
        {
            var exportPath = Path.Combine(outDir, $"{options.Name}.xml");
            var xml = RunCommandCheckedCapture(
                "xcrun",
                new[]
                {
                    "xctrace",
                    "export",
                    "--input",
                    options.TracePath,
                    "--xpath",
                    "/trace-toc/run/data/table[@schema=\"time-profile\"]"
                },
                RepoRoot());
            File.WriteAllText(exportPath, xml, Utf8NoBom);

            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var idIndex = doc.Descendants()
                .Select(element => new { Element = element, Id = (string?)element.Attribute("id") })
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .ToDictionary(item => item.Id!, item => item.Element, StringComparer.Ordinal);

            System.Xml.Linq.XElement? Resolve(System.Xml.Linq.XElement? element)
            {
                if (element is null)
                    return null;
                var reference = (string?)element.Attribute("ref");
                return !string.IsNullOrWhiteSpace(reference) && idIndex.TryGetValue(reference, out var resolved) ? resolved : element;
            }

            string? ElementText(System.Xml.Linq.XElement? element) => Resolve(element)?.Value;

            var jitMaps = LoadProfileJitMaps(options.JitMapDir);
            var aggregate = new Dictionary<(string Symbol, string? Binary), double>();
            var counts = new Dictionary<(string Symbol, string? Binary), int>();
            var rows = doc.Descendants("row").ToList();
            var totalRows = rows.Count;
            var keptRows = 0;
            var warmupNs = (long)(options.WarmupSeconds * 1_000_000_000.0);

            foreach (var row in rows)
            {
                var sampleTimeText = ElementText(row.Element("sample-time"));
                var weightText = ElementText(row.Element("weight"));
                var backtrace = Resolve(row.Element("backtrace"));
                var frames = backtrace?.Elements("frame").Select(Resolve).Where(frame => frame is not null).Cast<System.Xml.Linq.XElement>().ToList() ?? new List<System.Xml.Linq.XElement>();
                var frame = frames.FirstOrDefault(candidate =>
                {
                    var name = (string?)candidate.Attribute("name");
                    return !string.IsNullOrWhiteSpace(name) && !string.Equals(name, "<deduplicated_symbol>", StringComparison.Ordinal);
                }) ?? frames.FirstOrDefault();

                if (!long.TryParse(sampleTimeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleTime) ||
                    !long.TryParse(weightText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var weight) ||
                    frame is null)
                {
                    continue;
                }

                if (sampleTime < warmupNs)
                    continue;

                var symbol = (string?)frame.Attribute("name") ?? "<unknown>";
                var binary = Resolve(frame.Element("binary"));
                var binaryName = (string?)binary?.Attribute("name");
                if (binaryName is null)
                {
                    var annotated = AnnotateJitSymbol(symbol, jitMaps);
                    symbol = annotated.Symbol;
                    binaryName = annotated.BinaryName;
                }

                var key = (symbol, binaryName);
                aggregate[key] = aggregate.TryGetValue(key, out var selfMs) ? selfMs + weight / 1_000_000.0 : weight / 1_000_000.0;
                counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
                keptRows++;
            }

            var hotspots = aggregate
                .OrderByDescending(item => item.Value)
                .Select((item, index) => new ProfileHotspot(index + 1, item.Key.Symbol, Math.Round(item.Value, 3), counts[item.Key], item.Key.Binary))
                .ToList();

            return new ProfileAnalysisResult("xctrace", options.TracePath, exportPath, options.WarmupSeconds, totalRows, keptRows, hotspots);
        }
    }

    private sealed class PerfProfileBackend : IProfileBackend
    {
        public List<string> BuildRecordCommand(ProfileRecordOptions options, string tracePath, List<string> podishLaunch)
        {
            if (!CommandExists("perf"))
                throw new InvalidOperationException("perf backend requires the `perf` tool to be installed.");

            var command = new List<string>
            {
                "perf",
                "record",
                "--call-graph",
                "dwarf",
                "-F",
                "999",
                "-o",
                tracePath,
                "--"
            }.Concat(podishLaunch).ToList();

            if (options.TimeLimitSeconds > 0 && CommandExists("timeout"))
            {
                command = new List<string> { "timeout", "--preserve-status", $"{options.TimeLimitSeconds}s" }
                    .Concat(command)
                    .ToList();
            }

            return command;
        }

        public void Record(ProfileRecordOptions options, string runBinary, string tracePath, Dictionary<string, string> environment)
        {
            var command = BuildRecordCommand(options, tracePath, BuildPodishLaunchCommand(runBinary, options.Rootfs, options.Iterations, options.BenchCase));
            RunCommandChecked(command[0], command.Skip(1), RepoRoot(), environment);
        }

        public ProfileAnalysisResult Analyze(ProfileAnalyzeOptions options, string outDir)
        {
            var exportPath = Path.Combine(outDir, $"{options.Name}.perf-script.txt");
            var script = RunCommandCheckedCapture("perf", new[] { "script", "-i", options.TracePath }, RepoRoot());
            File.WriteAllText(exportPath, script, Utf8NoBom);

            var jitMaps = LoadProfileJitMaps(options.JitMapDir);
            var aggregate = new Dictionary<(string Symbol, string? Binary), double>();
            var counts = new Dictionary<(string Symbol, string? Binary), int>();

            var sampleHeaderRegex = new Regex(@"^\s*(?<comm>\S+)\s+(?<pid>\d+)\s+(?<time>[0-9]+\.[0-9]+):", RegexOptions.Compiled);
            var lines = script.Split('\n');
            double? firstTimestamp = null;
            var totalRows = 0;
            var keptRows = 0;
            var inSample = false;
            var currentSampleKeep = false;
            string? currentSymbol = null;
            string? currentBinaryName = null;

            static bool TryParsePerfFrame(string line, out string symbol, out string? binaryName)
            {
                symbol = "";
                binaryName = null;
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                    return false;

                var openParen = trimmed.LastIndexOf('(');
                var closeParen = trimmed.LastIndexOf(')');
                if (openParen < 0 || closeParen < openParen)
                    return false;

                binaryName = trimmed[(openParen + 1)..closeParen].Trim();
                var left = trimmed[..openParen].Trim();
                if (left.Length == 0)
                    return false;

                    var firstSpace = left.IndexOf(' ');
                    if (firstSpace >= 0)
                    {
                        var maybeAddress = left[..firstSpace];
                        var rest = left[(firstSpace + 1)..].Trim();
                        if (!string.IsNullOrWhiteSpace(rest) && maybeAddress.All(Uri.IsHexDigit))
                        left = rest;
                    }

                symbol = left;
                return true;
            }

            void CommitSample()
            {
                if (!inSample || !currentSampleKeep || string.IsNullOrWhiteSpace(currentSymbol))
                    return;

                var symbol = currentSymbol!;
                var binaryName = currentBinaryName;
                if (binaryName is null)
                {
                    var annotated = AnnotateJitSymbol(symbol, jitMaps);
                    symbol = annotated.Symbol;
                    binaryName = annotated.BinaryName;
                }

                var key = (symbol, binaryName);
                aggregate[key] = aggregate.TryGetValue(key, out var selfCount) ? selfCount + 1.0 : 1.0;
                counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
                keptRows++;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var headerMatch = sampleHeaderRegex.Match(lines[i]);
                if (!headerMatch.Success)
                {
                    if (!inSample)
                        continue;

                    if (string.IsNullOrWhiteSpace(lines[i]))
                    {
                        CommitSample();
                        inSample = false;
                        currentSymbol = null;
                        currentBinaryName = null;
                    }
                    else if (TryParsePerfFrame(lines[i], out var symbol, out var binaryName))
                    {
                        if (currentSymbol is null &&
                            !string.IsNullOrWhiteSpace(symbol) &&
                            !string.Equals(symbol, "<deduplicated_symbol>", StringComparison.Ordinal))
                        {
                            currentSymbol = symbol;
                            currentBinaryName = binaryName;
                        }
                    }
                    continue;
                }

                totalRows++;
                if (!double.TryParse(headerMatch.Groups["time"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var timestamp))
                    continue;

                firstTimestamp ??= timestamp;
                inSample = true;
                currentSampleKeep = (timestamp - firstTimestamp.Value) >= options.WarmupSeconds;
                currentSymbol = null;
                currentBinaryName = null;
            }

            CommitSample();

            var hotspots = aggregate
                .OrderByDescending(item => item.Value)
                .Select((item, index) => new ProfileHotspot(index + 1, item.Key.Symbol, Math.Round(item.Value, 3), counts[item.Key], item.Key.Binary))
                .ToList();

            return new ProfileAnalysisResult("perf", options.TracePath, exportPath, options.WarmupSeconds, totalRows, keptRows, hotspots);
        }
    }

    private sealed record ProfileRecordOptions(
        ProfileBackendKind BackendKind,
        IProfileBackend Backend,
        string BinaryPath,
        string Rootfs,
        string OutputDir,
        string Name,
        int TimeLimitSeconds,
        int Iterations,
        string BenchCase,
        string RenamedBinary,
        string? JitMapDir);

    private sealed record ProfileAnalyzeOptions(
        ProfileBackendKind BackendKind,
        IProfileBackend Backend,
        string TracePath,
        string BinaryPath,
        string OutputDir,
        string Name,
        double WarmupSeconds,
        int Top,
        int DisasmTop,
        string? JitMapDir);

    private sealed record ProfileAnalysisResult(
        string Backend,
        string TracePath,
        string ExportPath,
        double WarmupSeconds,
        int TotalRows,
        int KeptRows,
        List<ProfileHotspot> Hotspots);

    private sealed record ProfileHotspot(int Rank, string Symbol, double SelfMs, int SampleCount, string? BinaryName)
    {
        public Dictionary<string, object?> ToDictionary() => new()
        {
            ["rank"] = Rank,
            ["symbol"] = Symbol,
            ["self_ms"] = SelfMs,
            ["sample_count"] = SampleCount,
            ["binary_name"] = BinaryName
        };
    }

    private sealed record ProfileReportPaths(string ReportJsonPath, string ReportMarkdownPath);

    private sealed record SymbolEntry(string Address, string Mangled, string Demangled, string? NextAddress);

    private sealed record ResolvedTool(string FileName, string[] PrefixArgs);

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);

    private sealed record ProfileJitOpEntry(int Index, string Name, long RuntimeStart, int Offset);

    private sealed record ProfileJitBlockEntry(long GuestBlockStartEip, long RuntimeStart, int CodeSize, List<ProfileJitOpEntry> Ops);

    private sealed record OpSnapshot(uint EaDesc, byte Modrm, byte Meta, byte Prefixes);
}
