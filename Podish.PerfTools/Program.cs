using System.Collections;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Podish.PerfTools;

internal static partial class Program
{
    private const uint CfBit = 1u << 0;
    private const uint PfBit = 1u << 2;
    private const uint AfBit = 1u << 4;
    private const uint ZfBit = 1u << 6;
    private const uint SfBit = 1u << 7;
    private const uint OfBit = 1u << 11;
    private const uint AllStatusFlagsMask = CfBit | PfBit | AfBit | ZfBit | SfBit | OfBit;
    private static readonly string[] GprNames = ["eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi"];

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
        var input = RequireValue(args, "--input", 0);
        var libPath = RequireValue(args, "--lib", 1);
        var output = GetValue(args, "--output") ?? DefaultAnalysisOutput(input);
        var nGram = GetIntValue(args, "--n-gram", 2);
        var topNgrams = GetIntValue(args, "--top-ngrams", 100);

        var result = AnalyzeBlocks(input, libPath, nGram, topNgrams);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, JsonOptions), Utf8NoBom);
        Console.WriteLine($"Wrote analysis to {Path.GetFullPath(output)}");
        return 0;
    }

    private static int RunGenerateSuperopcodes(string[] args)
    {
        var input = RequireValue(args, "--input", 0);
        var output = RequireValue(args, "--output", 1);
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
        var rootfs = Path.GetFullPath(GetValue(args, "--rootfs") ??
                                      Path.Combine(projectRoot, "benchmark", "podish_perf", "rootfs",
                                          "coremark_i386_alpine"));
        var candidateTop = GetIntValue(args, "--candidate-top", 100);
        var superopcodeTop = GetIntValue(args, "--superopcode-top", 32);
        var iterations = GetIntValue(args, "--iterations", 3000);
        var repeat = GetIntValue(args, "--repeat", 1);
        var timeout = GetIntValue(args, "--timeout", 1800);
        var resultsDir = GetValue(args, "--results-dir");
        var workDir = GetValue(args, "--work-dir");
        var generatedOutput =
            GetValue(args, "--generated-output") ?? "libfibercpu/generated/superopcodes.generated.cpp";
        var reuseRootfs = HasFlag(args, "--reuse-rootfs");
        var keepWorkdirs = HasFlag(args, "--keep-workdirs");
        var skipVerifyBuild = HasFlag(args, "--skip-verify-build");

        var cases = GetMultiValue(args, "--case");
        if (cases.Count == 0)
            cases = new List<string> { "compress", "compile", "run" };

        if (string.IsNullOrWhiteSpace(resultsDir))
            resultsDir = Path.Combine(projectRoot, "benchmark", "podish_perf", "results",
                $"{DateTime.Now:yyyyMMdd-HHmmss}-superopcode");
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
            "--results-dir", resultsDir
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
            RunProcess("dotnet", new[]
            {
                "build",
                Path.Combine(projectRoot, "Podish.Cli", "Podish.Cli.csproj"),
                "-c", "Release",
                "-p:EnableHandlerProfile=true",
                "-p:EnableSuperOpcodes=true"
            }, projectRoot);

        Console.WriteLine($"[superopcode] results_dir={resultsDir}");
        Console.WriteLine($"[superopcode] candidate_json={candidateJson}");
        Console.WriteLine($"[superopcode] generated_output={generatedOutputPath}");
        return 0;
    }


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
            File.Copy(sibling, Path.Combine(outDir, Path.GetFileName(sibling)), true);
        }

        var dst = Path.Combine(outDir, renamedBinary);
        File.Copy(src, dst, true);
        TryMarkExecutable(dst);
        return dst;
    }

    private static string GetTracePath(string outDir, string name, ProfileBackendKind backendKind)
    {
        return Path.Combine(outDir, $"{name}.{(backendKind == ProfileBackendKind.XcTrace ? "trace" : "data")}");
    }

    private static void WriteRecordCommand(string outDir, ProfileRecordOptions options, string runBinary,
        string tracePath)
    {
        var podishLaunch = BuildPodishLaunchCommand(runBinary, options.Rootfs, options.Iterations, options.BenchCase);
        var recordCommand = options.Backend.BuildRecordCommand(options, tracePath, podishLaunch);
        File.WriteAllText(Path.Combine(outDir, "record-command.txt"), ShellJoin(recordCommand) + Environment.NewLine,
            Utf8NoBom);
    }

    private static Dictionary<string, string> BuildProfileEnvironment(ProfileRecordOptions options)
    {
        var env = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .ToDictionary(entry => Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "",
                entry => Convert.ToString(entry.Value, CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal);
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
    {
        return new List<string> { "/bin/sh", "-lc", BuildGuestScript(benchCase, iterations) };
    }

    private static string BuildGuestScript(string benchCase, int iterations)
    {
        var compileCommand =
            $"make PORT_DIR=linux ITERATIONS={iterations} XCFLAGS=\"-O3 -DPERFORMANCE_RUN=1\" REBUILD=1 compile";
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
        var reports = reportPaths
            .Select(path => JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)).RootElement.Clone()).ToList();
        var labels = reportPaths
            .Select(path => Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path))).ToList();
        var perReport = new List<Dictionary<string, double>>(reports.Count);
        var allSymbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var report in reports)
        {
            var map = new Dictionary<string, double>(StringComparer.Ordinal);
            if (report.TryGetProperty("hotspots", out var hotspotsNode) &&
                hotspotsNode.ValueKind == JsonValueKind.Array)
                foreach (var hotspot in hotspotsNode.EnumerateArray())
                {
                    var symbol = hotspot.TryGetProperty("symbol", out var symbolNode)
                        ? symbolNode.GetString() ?? ""
                        : "";
                    var selfMs = hotspot.TryGetProperty("self_ms", out var selfNode) ? selfNode.GetDouble() : 0.0;
                    if (string.IsNullOrWhiteSpace(symbol))
                        continue;
                    map[symbol] = selfMs;
                    allSymbols.Add(symbol);
                }

            perReport.Add(map);
        }

        var rankedSymbols = allSymbols
            .OrderByDescending(symbol =>
                perReport.Max(report => report.TryGetValue(symbol, out var value) ? value : 0.0))
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
            lines.Add(
                $"| `{symbol}` | {string.Join(" | ", values.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)))} |");
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
        var rawSymbols = TryLoadRawSymbolsWithObjectFile(binaryPath);
        var orderedSymbols = rawSymbols
            .Where(x => x.addr != 0 && !string.IsNullOrWhiteSpace(x.name))
            .OrderBy(x => x.addr)
            .ToList();

        if (orderedSymbols.Count == 0)
            return new Dictionary<string, SymbolEntry>(StringComparer.Ordinal);

        var demangled = DemangleSymbols(orderedSymbols.Select(x => x.name).Distinct(StringComparer.Ordinal).ToArray());
        var index = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal);
        for (var i = 0; i < orderedSymbols.Count; i++)
        {
            var (addressValue, mangledName) = orderedSymbols[i];
            var address = addressValue.ToString("x", CultureInfo.InvariantCulture);
            var demangledName = demangled.TryGetValue(mangledName, out var demangledValue)
                ? demangledValue
                : mangledName;
            var nextAddress = i + 1 < orderedSymbols.Count
                ? orderedSymbols[i + 1].addr.ToString("x", CultureInfo.InvariantCulture)
                : null;
            index[demangledName] = new SymbolEntry(address, mangledName, demangledName, nextAddress);
        }

        return index;
    }

    private static bool TryResolveSymbolName(string symbol, Dictionary<string, SymbolEntry> index,
        out SymbolEntry entry)
    {
        if (index.TryGetValue(symbol, out entry!))
            return true;
        foreach (var (demangledName, candidate) in index)
            if (demangledName.EndsWith(symbol, StringComparison.Ordinal) ||
                symbol.EndsWith(demangledName, StringComparison.Ordinal))
            {
                entry = candidate;
                return true;
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

    private static void AnalyzeSampleCandidates(
        JsonElement blocksNode,
        Dictionary<string, SampleAnchorStats> anchors,
        Dictionary<(string, string), SamplePairStats> pairs)
    {
        foreach (var blockNode in blocksNode.EnumerateArray())
        {
            if (!blockNode.TryGetProperty("ops", out var opsNode) || opsNode.ValueKind != JsonValueKind.Array)
                continue;
            var blockExecCount = blockNode.TryGetProperty("exec_count", out var execNode)
                ? (long)execNode.GetUInt64()
                : 0L;
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
                    anchorStats.ExampleBlocks.Add(new Dictionary<string, object?>
                    {
                        ["start_eip_hex"] = blockStart,
                        ["exec_count"] = blockExecCount,
                        ["anchor_op_index"] = i
                    });

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
                ["example_blocks"] = sampleStats.ExampleBlocks
            });
    }

    private static void MergePairStats(
        Dictionary<(string, string), PairAggregate> aggregate,
        (string, string) pair,
        Dictionary<string, object?> sampleMeta,
        SamplePairStats sampleStats)
    {
        if (!aggregate.TryGetValue(pair, out var entry))
        {
            entry = new PairAggregate(pair.Item1, pair.Item2, sampleStats.AnchorHandler, sampleStats.Direction,
                sampleStats.RelationKind, sampleStats.RelationPriority, sampleStats.SharedResources,
                sampleStats.LegalityNotes);
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
                ["example_blocks"] = sampleStats.ExampleBlocks
            });
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
            ["semantics"] = entry.Semantics?.ToDictionary()
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
        var dominantRelation = entry.RelationKindVariants.TryGetValue("RAW", out var rawVariantCount)
            ?
            new KeyValuePair<string, int>("RAW", rawVariantCount)
            :
            entry.RelationKindVariants.TryGetValue("RAR/WAW", out var rarWawVariantCount)
                ? new KeyValuePair<string, int>("RAR/WAW", rarWawVariantCount)
                :
                entry.RelationKindVariants.OrderByDescending(kvp => kvp.Value)
                    .ThenBy(kvp => kvp.Key, StringComparer.Ordinal).FirstOrDefault();
        var dominantRelationKind =
            string.IsNullOrWhiteSpace(dominantRelation.Key) ? entry.RelationKind : dominantRelation.Key;
        var dominantRelationCount = dominantRelation.Value;
        var dominantShared = string.IsNullOrWhiteSpace(dominantSharedVariant.Key)
            ? entry.SharedResources.ToArray()
            : dominantSharedVariant.Key.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var dominantDepWeight = RelationDepWeight(dominantRelationKind, rawWeight, rarWeight, wawWeight);
        var baseFrequency = scoreBasis == "anchor"
            ? Convert.ToInt64(anchorEntry["weighted_exec_count"], CultureInfo.InvariantCulture)
            : entry.WeightedExecCount;
        var jccWeight = RelationJccWeight(new[] { entry.FirstHandler, entry.SecondHandler }, dominantRelationKind,
            jccMultiplier, jccMode);

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
            ["dominant_shared_resource_ratio"] =
                entry.Occurrences > 0 ? (double)dominantSharedVariant.Value / entry.Occurrences : 0.0,
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
            ["jcc_weight"] = jccWeight
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
            sb.AppendLine(
                $"| {index} | `{anchor["anchor_display"]}` | {anchor["weighted_exec_count"]} | {anchor["sample_count"]} | {anchor["occurrences"]} | {anchor["unique_block_count"]} |");

        sb.AppendLine();
        sb.AppendLine("## Top Candidates");
        sb.AppendLine();
        sb.AppendLine(
            "| Rank | Pair | Relation | Score | Anchor | Dir | Weighted Exec | Samples | Occurrences | Shared |");
        sb.AppendLine("| --- | --- | --- | ---: | --- | --- | ---: | ---: | ---: | --- |");
        foreach (var (candidate, index) in candidates.Select((c, i) => (c, i + 1)))
        {
            var shared = candidate["shared_resources"] is IEnumerable<string> sharedSet
                ? string.Join(", ", sharedSet)
                : "-";
            if (string.IsNullOrWhiteSpace(shared))
                shared = "-";
            sb.AppendLine(
                $"| {index} | `{candidate["pair_display"]}` | {candidate["relation_kind"]} | {candidate["score"]} | `{candidate["anchor_display"]}` | {candidate["direction"]} | {candidate["weighted_exec_count"]} | {candidate["sample_count"]} | {candidate["occurrences"]} | {shared} |");
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
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    private sealed class SampleAnchorStats
    {
        public SampleAnchorStats(string anchor)
        {
            Anchor = anchor;
        }

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
                ExampleBlocks.Add(new Dictionary<string, object?>
                {
                    ["start_eip_hex"] = blockStart,
                    ["exec_count"] = blockExecCount,
                    ["start_op_index"] = startOpIndex,
                    ["anchor_handler"] = AnchorHandler,
                    ["direction"] = Direction
                });
        }
    }

    private sealed class AnchorAggregate
    {
        public AnchorAggregate(string anchor)
        {
            Anchor = anchor;
        }

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
        public PairAggregate(string first, string second, string anchor, string direction, string relationKind,
            int relationPriority, IEnumerable<string> sharedResources, IEnumerable<string> legalityNotes)
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
        public CountingMap() : base(StringComparer.Ordinal)
        {
        }

        public void Add(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;
            this[key] = TryGetValue(key, out var count) ? count + 1 : 1;
        }

        public Dictionary<string, int> ToSortedDictionary()
        {
            return this.OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
        }
    }

    private sealed class NgramAggregate
    {
        public NgramAggregate(string[] ngram)
        {
            Ngram = ngram.ToArray();
        }

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
        public Dictionary<string, object?> ToDictionary()
        {
            return new Dictionary<string, object?>
            {
                ["reads"] = Reads.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                ["writes"] = Writes.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                ["notes"] = Notes.ToArray(),
                ["control_flow"] = ControlFlow,
                ["memory_side_effect"] = MemorySideEffect
            };
        }
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

        void Record(ProfileRecordOptions options, string runBinary, string tracePath,
            Dictionary<string, string> environment);

        ProfileAnalysisResult Analyze(ProfileAnalyzeOptions options, string outDir);
    }

    private sealed class XcTraceProfileBackend : IProfileBackend
    {
        public List<string> BuildRecordCommand(ProfileRecordOptions options, string tracePath,
            List<string> podishLaunch)
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

        public void Record(ProfileRecordOptions options, string runBinary, string tracePath,
            Dictionary<string, string> environment)
        {
            var command = BuildRecordCommand(options, tracePath,
                BuildPodishLaunchCommand(runBinary, options.Rootfs, options.Iterations, options.BenchCase));
            var result = RunCommand(command[0], command.Skip(1), RepoRoot(), false, new HashSet<int> { 0, 1 },
                environment);
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

            var doc = XDocument.Parse(xml);
            var idIndex = doc.Descendants()
                .Select(element => new { Element = element, Id = (string?)element.Attribute("id") })
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .ToDictionary(item => item.Id!, item => item.Element, StringComparer.Ordinal);

            XElement? Resolve(XElement? element)
            {
                if (element is null)
                    return null;
                var reference = (string?)element.Attribute("ref");
                return !string.IsNullOrWhiteSpace(reference) && idIndex.TryGetValue(reference, out var resolved)
                    ? resolved
                    : element;
            }

            string? ElementText(XElement? element)
            {
                return Resolve(element)?.Value;
            }

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
                var frames =
                    backtrace?.Elements("frame").Select(Resolve).Where(frame => frame is not null).Cast<XElement>()
                        .ToList() ?? new List<XElement>();
                var frame = frames.FirstOrDefault(candidate =>
                {
                    var name = (string?)candidate.Attribute("name");
                    return !string.IsNullOrWhiteSpace(name) &&
                           !string.Equals(name, "<deduplicated_symbol>", StringComparison.Ordinal);
                }) ?? frames.FirstOrDefault();

                if (!long.TryParse(sampleTimeText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var sampleTime) ||
                    !long.TryParse(weightText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var weight) ||
                    frame is null)
                    continue;

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
                aggregate[key] = aggregate.TryGetValue(key, out var selfMs)
                    ? selfMs + weight / 1_000_000.0
                    : weight / 1_000_000.0;
                counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
                keptRows++;
            }

            var hotspots = aggregate
                .OrderByDescending(item => item.Value)
                .Select((item, index) => new ProfileHotspot(index + 1, item.Key.Symbol, Math.Round(item.Value, 3),
                    counts[item.Key], item.Key.Binary))
                .ToList();

            return new ProfileAnalysisResult("xctrace", options.TracePath, exportPath, options.WarmupSeconds, totalRows,
                keptRows, hotspots);
        }
    }

    private sealed class PerfProfileBackend : IProfileBackend
    {
        public List<string> BuildRecordCommand(ProfileRecordOptions options, string tracePath,
            List<string> podishLaunch)
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
                command = new List<string> { "timeout", "--preserve-status", $"{options.TimeLimitSeconds}s" }
                    .Concat(command)
                    .ToList();

            return command;
        }

        public void Record(ProfileRecordOptions options, string runBinary, string tracePath,
            Dictionary<string, string> environment)
        {
            var command = BuildRecordCommand(options, tracePath,
                BuildPodishLaunchCommand(runBinary, options.Rootfs, options.Iterations, options.BenchCase));
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

            var sampleHeaderRegex = new Regex(@"^\s*(?<comm>\S+)\s+(?<pid>\d+)\s+(?<time>[0-9]+\.[0-9]+):",
                RegexOptions.Compiled);
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
                if (!double.TryParse(headerMatch.Groups["time"].Value, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var timestamp))
                    continue;

                firstTimestamp ??= timestamp;
                inSample = true;
                currentSampleKeep = timestamp - firstTimestamp.Value >= options.WarmupSeconds;
                currentSymbol = null;
                currentBinaryName = null;
            }

            CommitSample();

            var hotspots = aggregate
                .OrderByDescending(item => item.Value)
                .Select((item, index) => new ProfileHotspot(index + 1, item.Key.Symbol, Math.Round(item.Value, 3),
                    counts[item.Key], item.Key.Binary))
                .ToList();

            return new ProfileAnalysisResult("perf", options.TracePath, exportPath, options.WarmupSeconds, totalRows,
                keptRows, hotspots);
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
        public Dictionary<string, object?> ToDictionary()
        {
            return new Dictionary<string, object?>
            {
                ["rank"] = Rank,
                ["symbol"] = Symbol,
                ["self_ms"] = SelfMs,
                ["sample_count"] = SampleCount,
                ["binary_name"] = BinaryName
            };
        }
    }

    private sealed record ProfileReportPaths(string ReportJsonPath, string ReportMarkdownPath);

    private sealed record SymbolEntry(string Address, string Mangled, string Demangled, string? NextAddress);

    private sealed record ResolvedTool(string FileName, string[] PrefixArgs);

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);

    private sealed record ProfileJitOpEntry(int Index, string Name, long RuntimeStart, int Offset);

    private sealed record ProfileJitBlockEntry(
        long GuestBlockStartEip,
        long RuntimeStart,
        int CodeSize,
        List<ProfileJitOpEntry> Ops);

    private sealed record OpSnapshot(uint EaDesc, byte Modrm, byte Meta, byte Prefixes);
}