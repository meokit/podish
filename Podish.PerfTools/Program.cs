using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LibObjectFile.Elf;

namespace Podish.PerfTools;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Regex DispatchWrapperRegex =
        new(@"DispatchWrapper<&(?:[a-zA-Z0-9_]+::)*?(op::Op[A-Za-z0-9_]+)>", RegexOptions.Compiled);

    private static readonly Regex DirectLogicRegex =
        new(@"(?:^|::)(op::Op[A-Za-z0-9_]+)(?:\(|$)", RegexOptions.Compiled);

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

Examples:
  dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- pipeline --candidate-top 256 --superopcode-top 256 --reuse-rootfs
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
        File.WriteAllText(output, JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);
        Console.WriteLine($"Wrote analysis to {Path.GetFullPath(output)}");
        return 0;
    }

    private static int RunAnalyzeSuperopcodeCandidates(string[] args)
    {
        var inputs = GetMultiValue(args, "--input");
        if (inputs.Count == 0)
        {
            inputs = args.Where(x => !x.StartsWith("--", StringComparison.Ordinal)).ToList();
        }

        var nGram = GetIntValue(args, "--n-gram", 2);
        if (nGram != 2)
            throw new InvalidOperationException("--n-gram must remain 2");

        var top = GetIntValue(args, "--top", 100);
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
                    ["reasons"] = new[] { "no adjacent 2-op candidates found in blocks" }
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

            candidates.Add(NormalizeCandidateEntry(entry, anchorEntry));
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
                ["strategy"] = "sequential-2-gram-count",
                ["analysis_file_count"] = analysisFiles.Count,
                ["included_sample_count"] = includedSamples.Count,
                ["skipped_sample_count"] = skippedSamples.Count,
                ["candidate_count"] = candidates.Count,
                ["anchor_count"] = anchors.Count,
                ["anchor_top_limit"] = anchorTop,
                ["min_samples"] = minSamples,
                ["min_weighted_exec_count"] = minWeightedExec,
                ["top_limit"] = top,
                ["superopcode_width"] = 2
            },
            ["included_samples"] = includedSamples,
            ["skipped_samples"] = skippedSamples,
            ["anchors"] = anchors.Take(anchorTop).ToList(),
            ["candidates"] = candidates,
        };

        File.WriteAllText(outputJson, JsonSerializer.Serialize(output, JsonOptions), Encoding.UTF8);
        Console.WriteLine($"Wrote {candidates.Count} candidates from {includedSamples.Count} samples to {Path.GetFullPath(outputJson)}");
        if (!string.IsNullOrWhiteSpace(outputMd))
        {
            File.WriteAllText(outputMd, BuildMarkdown(inputs, analysisFiles, includedSamples, skippedSamples, anchors, candidates, anchorTop), Encoding.UTF8);
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
        File.WriteAllText(output, generated, Encoding.UTF8);
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

        var runnerScript = Path.Combine(projectRoot, "benchmark", "podish_perf", "runner.py");
        var runnerArgs = new List<string>
        {
            runnerScript,
            "--engine", "jit",
            "--jit-handler-profile-block-dump",
            "--disable-superopcodes",
            "--skip-auto-analyze-block-dump",
            "--block-n-gram", "0",
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

        RunProcess("python3", runnerArgs, projectRoot);

        var guestStatsRoot = Path.Combine(resultsDir, "guest-stats");
        var fibercpuLibrary = DefaultFibercpuLibrary(projectRoot);
        foreach (var dumpDir in EnumerateGuestStatsDirs(guestStatsRoot))
        {
            var output = Path.Combine(dumpDir, "blocks_analysis.json");
            var analysis = AnalyzeBlocks(Path.Combine(dumpDir, "blocks.bin"), fibercpuLibrary, 2, 100);
            File.WriteAllText(output, JsonSerializer.Serialize(analysis, JsonOptions), Encoding.UTF8);
        }

        var candidateJson = Path.Combine(resultsDir, "superopcode_candidates.json");
        var candidateMd = Path.Combine(resultsDir, "superopcode_candidates.md");
        RunAnalyzeSuperopcodeCandidates(new[]
        {
            "--input", guestStatsRoot,
            "--n-gram", "2",
            "--top", candidateTop.ToString(CultureInfo.InvariantCulture),
            "--output-json", candidateJson,
            "--output-md", candidateMd
        });

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

    private static string DefaultAnalysisOutput(string input)
    {
        return Directory.Exists(input) ? Path.Combine(input, "blocks_analysis.json") : "blocks_analysis.json";
    }

    private static string RepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

    private static string DefaultFibercpuLibrary(string projectRoot)
    {
        var hostDir = Path.Combine(projectRoot, "Fiberish.X86", "build_native", "host");
        foreach (var name in new[] { "libfibercpu.dylib", "libfibercpu.so", "fibercpu.dll" })
        {
            var candidate = Path.Combine(hostDir, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return Path.Combine(hostDir, "libfibercpu.so");
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
        Console.Error.WriteLine($"[analyze-blocks] parsing block dump {dumpFile}");
        var (baseAddr, count, blocks, warnings) = ParseBlocks(dumpFile, symbols);
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
        var rawSymbols = new List<(ulong addr, string name)>();

        using (var inStream = File.OpenRead(libPath))
        {
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
        }

        var demangled = DemangleSymbols(rawSymbols.Select(x => x.name).Distinct(StringComparer.Ordinal).ToArray());
        var symbols = new Dictionary<ulong, string>();
        foreach (var (addr, name) in rawSymbols)
        {
            symbols[addr] = demangled.TryGetValue(name, out var d) ? d : name;
        }

        return symbols;
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
        string dumpFile, Dictionary<ulong, string> symbols)
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
                    ["op_id"] = null,
                    ["op_id_hex"] = null,
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
                    ["def_use"] = new Dictionary<string, object?>
                    {
                        ["reads"] = Array.Empty<string>(),
                        ["writes"] = Array.Empty<string>(),
                        ["control_flow"] = false,
                        ["memory_side_effect"] = false
                    }
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

                if (i + 1 < seq.Count && !string.IsNullOrWhiteSpace(seq[i + 1]))
                {
                    var pair = (anchor!, seq[i + 1]!);
                    if (!pairs.TryGetValue(pair, out var pairStats))
                    {
                        pairStats = new SamplePairStats(pair.Item1, pair.Item2, anchor!);
                        pairs[pair] = pairStats;
                    }
                    pairStats.WeightedExecCount += blockExecCount;
                    pairStats.Occurrences += 1;
                    pairStats.UniqueBlockStarts.Add(blockStart);
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
    }

    private static void MergePairStats(
        Dictionary<(string, string), PairAggregate> aggregate,
        (string, string) pair,
        Dictionary<string, object?> sampleMeta,
        SamplePairStats sampleStats)
    {
        if (!aggregate.TryGetValue(pair, out var entry))
        {
            entry = new PairAggregate(pair.Item1, pair.Item2, sampleStats.AnchorHandler);
            aggregate[pair] = entry;
        }

        entry.WeightedExecCount += sampleStats.WeightedExecCount;
        entry.Occurrences += sampleStats.Occurrences;
        entry.UniqueBlockCount += sampleStats.UniqueBlockStarts.Count;
        entry.SampleCount += 1;
        entry.EngineCounts.Add((string?)sampleMeta["engine"]);
        entry.CaseCounts.Add((string?)sampleMeta["case"]);
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
        };
    }

    private static Dictionary<string, object?> NormalizeCandidateEntry(PairAggregate entry, Dictionary<string, object?> anchorEntry)
    {
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
            ["direction"] = "sequential",
            ["relation_kind"] = "SEQUENTIAL",
            ["relation_priority"] = 1,
            ["shared_resources"] = Array.Empty<string>(),
            ["weighted_exec_count"] = entry.WeightedExecCount,
            ["occurrences"] = entry.Occurrences,
            ["unique_block_count"] = entry.UniqueBlockCount,
            ["sample_count"] = entry.SampleCount,
            ["engine_counts"] = entry.EngineCounts.ToSortedDictionary(),
            ["case_counts"] = entry.CaseCounts.ToSortedDictionary(),
            ["score"] = entry.WeightedExecCount,
            ["anchor_weighted_exec_count"] = anchorEntry["weighted_exec_count"],
            ["anchor_sample_count"] = anchorEntry["sample_count"],
            ["anchor_unique_block_count"] = anchorEntry["unique_block_count"],
            ["score_basis"] = "pair",
            ["base_frequency"] = entry.WeightedExecCount,
            ["jcc_weight"] = 1,
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
        sb.AppendLine("- Strategy: sequential 2-gram scoring");
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
        foreach (var (anchor, index) in anchors.Take(anchorTop).Select((a, i) => (a, i + 1)))
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
            sb.AppendLine($"| {index} | `{candidate["pair_display"]}` | {candidate["relation_kind"]} | {candidate["score"]} | `{candidate["anchor_display"]}` | {candidate["direction"]} | {candidate["weighted_exec_count"]} | {candidate["sample_count"]} | {candidate["occurrences"]} | - |");
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
            var handlerName = $"SuperOpcode_{index:03d}_{SanitizeName(c.Op0[4..])}__{SanitizeName(c.Op1[4..])}";
            sb.AppendLine($"// weighted_exec_count={c.Wec} occurrences={c.Occ} relation={c.Relation} anchor={c.Anchor} direction={c.Direction}");
            sb.AppendLine("ATTR_PRESERVE_NONE int64_t " + handlerName + "(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,");
            sb.AppendLine("                                          mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {");
            sb.AppendLine($"    auto flow0 = {c.Op0}(state, op, &utlb, GetImm(op), &branch, flags_cache);");
            sb.AppendLine("    if (flow0 != LogicFlow::Continue) [[unlikely]] {");
            sb.AppendLine("        HANDLE_SUPEROPCODE_FLOW(flow0, state, op, instr_limit, utlb, branch, flags_cache);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    DecodedOp* second_op = NextOp(op);");
            sb.AppendLine($"    auto flow1 = {c.Op1}(state, second_op, &utlb, GetImm(second_op), &branch, flags_cache);");
            sb.AppendLine("    if (flow1 != LogicFlow::Continue) [[unlikely]] {");
            sb.AppendLine("        HANDLE_SUPEROPCODE_FLOW(flow1, state, second_op, instr_limit, utlb, branch, flags_cache);");
            sb.AppendLine("    }");
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
            var handlerName = $"SuperOpcode_{index:03d}_{SanitizeName(c.Op0[4..])}__{SanitizeName(c.Op1[4..])}";
            sb.AppendLine($"    if (ops[0].handler == (HandlerFunc)DispatchWrapper<{c.Op0}> && ops[1].handler == (HandlerFunc)DispatchWrapper<{c.Op1}>) return {handlerName};");
        }
        sb.AppendLine("    return nullptr;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("}  // namespace fiberish");
        sb.AppendLine();

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

    private static string RequireValue(string[] args, string option, int positionalIndex = -1)
    {
        var value = GetValue(args, option);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        var positional = args.Where(x => !x.StartsWith("--", StringComparison.Ordinal)).ToArray();
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
    }

    private sealed class SamplePairStats
    {
        public SamplePairStats(string first, string second, string anchor)
        {
            FirstHandler = first;
            SecondHandler = second;
            AnchorHandler = anchor;
        }

        public string FirstHandler { get; }
        public string SecondHandler { get; }
        public string AnchorHandler { get; }
        public long WeightedExecCount { get; set; }
        public long Occurrences { get; set; }
        public HashSet<string> UniqueBlockStarts { get; } = new(StringComparer.Ordinal);
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
    }

    private sealed class PairAggregate
    {
        public PairAggregate(string first, string second, string anchor)
        {
            FirstHandler = first;
            SecondHandler = second;
            AnchorHandler = anchor;
        }

        public string FirstHandler { get; }
        public string SecondHandler { get; }
        public string AnchorHandler { get; }
        public long WeightedExecCount { get; set; }
        public long Occurrences { get; set; }
        public long UniqueBlockCount { get; set; }
        public long SampleCount { get; set; }
        public CountingMap EngineCounts { get; } = new();
        public CountingMap CaseCounts { get; } = new();
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

    private sealed record OpSnapshot(uint EaDesc, byte Modrm, byte Meta, byte Prefixes);
}
