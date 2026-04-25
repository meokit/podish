using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Podish.PerfTools;

internal static partial class Program
{
    private static double GetDoubleValue(string[] args, string option, double defaultValue)
    {
        return double.TryParse(GetValue(args, option), NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }

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
            UseShellExecute = false
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        if (environment is not null)
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;

        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException($"Failed to start command: {fileName}");
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

    private static void RunCommandChecked(string fileName, IEnumerable<string> arguments, string? workingDirectory,
        IDictionary<string, string>? environment = null)
    {
        RunCommand(fileName, arguments, workingDirectory, false, environment: environment);
    }

    private static string RunCommandCheckedCapture(string fileName, IEnumerable<string> arguments,
        string? workingDirectory, IDictionary<string, string>? environment = null)
    {
        return RunCommand(fileName, arguments, workingDirectory, true, environment: environment).Stdout;
    }

    private static bool CommandExists(string command)
    {
        try
        {
            var search = RunCommand("bash",
                new[] { "-lc", $"command -v {EscapeShellSingleArgument(command)} >/dev/null 2>&1" }, RepoRoot(), false,
                new HashSet<int> { 0, 1 });
            return search.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeShellSingleArgument(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static string RunResolvedToolCapture(ResolvedTool tool, IEnumerable<string> arguments,
        string? workingDirectory = null)
    {
        return RunCommandCheckedCapture(tool.FileName, tool.PrefixArgs.Concat(arguments).ToArray(), workingDirectory);
    }

    private static string ShellJoin(IEnumerable<string> parts)
    {
        return string.Join(" ", parts.Select(ShellQuote));
    }

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
            RunCommand("chmod", new[] { "+x", path }, RepoRoot(), false);
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
            if (File.Exists(Path.Combine(dir, "blocks.bin")))
                yield return dir;
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
                if (seen.Add(candidate))
                    files.Add(candidate);
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static List<ProfileJitBlockEntry> LoadProfileJitMaps(string? mapDir)
    {
        if (string.IsNullOrWhiteSpace(mapDir) || !Directory.Exists(mapDir))
            return new List<ProfileJitBlockEntry>();

        var entries = new List<ProfileJitBlockEntry>();
        foreach (var path in Directory.EnumerateFiles(mapDir, "jit_*.map.json")
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = doc.RootElement;
            var ops = new List<ProfileJitOpEntry>();
            if (root.TryGetProperty("ops", out var opsNode) && opsNode.ValueKind == JsonValueKind.Array)
                foreach (var op in opsNode.EnumerateArray())
                    ops.Add(new ProfileJitOpEntry(
                        op.TryGetProperty("index", out var indexNode) ? indexNode.GetInt32() : 0,
                        op.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "",
                        op.TryGetProperty("runtime_start", out var opRuntimeNode) ? opRuntimeNode.GetInt64() : 0,
                        op.TryGetProperty("offset", out var offsetNode) ? offsetNode.GetInt32() : 0));

            entries.Add(new ProfileJitBlockEntry(
                root.TryGetProperty("guest_block_start_eip", out var blockNode) ? blockNode.GetInt64() : 0,
                root.TryGetProperty("runtime_start", out var runtimeNode) ? runtimeNode.GetInt64() : 0,
                root.TryGetProperty("code_size", out var sizeNode) ? sizeNode.GetInt32() : 0,
                ops));
        }

        return entries.OrderBy(entry => entry.RuntimeStart).ToList();
    }

    private static (string Symbol, string? BinaryName) AnnotateJitSymbol(string symbol,
        List<ProfileJitBlockEntry> jitMaps)
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
                if (op.RuntimeStart <= address)
                    currentOp = op;
                else
                    break;

            if (currentOp is null)
                return ($"jit:block@{block.GuestBlockStartEip:x8}+0x{blockOffset:x}", "jit");

            var opOffset = address - currentOp.RuntimeStart;
            return (
                $"jit:block@{block.GuestBlockStartEip:x8}+0x{blockOffset:x} op[{currentOp.Index}] {currentOp.Name}+0x{opOffset:x}",
                "jit");
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
            if (args[i] == option)
                return args[i + 1];
        return null;
    }

    private static int GetIntValue(string[] args, string option, int defaultValue)
    {
        return int.TryParse(GetValue(args, option), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }

    private static bool HasFlag(string[] args, string flag)
    {
        return args.Contains(flag, StringComparer.Ordinal);
    }

    private static List<string> GetMultiValue(string[] args, string option)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == option)
                values.Add(args[i + 1]);
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

    private static (string dumpFile, string? summaryFile, string defaultOutput) ResolveInputPaths(string inputPath,
        bool _ = false)
    {
        return ResolveInputPaths(inputPath);
    }

    private static string DefaultOutputFromInput(string inputPath)
    {
        return Directory.Exists(inputPath) ? Path.Combine(inputPath, "blocks_analysis.json") : "blocks_analysis.json";
    }

    private static void RunProcess(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"command failed with exit code {proc.ExitCode}: {fileName} {string.Join(" ", arguments)}");
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
            foreach (var warning in warnings.EnumerateArray().Select(x => x.GetString() ?? ""))
                if (warning.Contains("parsed blocks are empty", StringComparison.Ordinal) ||
                    warning.Contains("dump/export format likely drifted", StringComparison.Ordinal) ||
                    warning.Contains("truncated", StringComparison.Ordinal))
                    reasons.Add(warning);

        if (!root.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array ||
            blocks.GetArrayLength() == 0)
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
                var symbol = op.TryGetProperty("symbol", out var sym) ? sym.GetString() ?? "" : "";
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
            ["iteration"] = null
        };

        if (sampleName.StartsWith("jit-", StringComparison.Ordinal))
            metadata["engine"] = "jit";
        else if (sampleName.StartsWith("aot-", StringComparison.Ordinal))
            metadata["engine"] = "aot";

        var caseMatch = Regex.Match(sampleName, @"-(run|compile|compress|gcc_compile)(?:-|$)", RegexOptions.IgnoreCase);
        if (caseMatch.Success)
            metadata["case"] = caseMatch.Groups[1].Value;

        var iterMatch = Regex.Match(sampleName, @"(?:^|-)iter(?:ation)?-(\d+)|-(\d+)$", RegexOptions.IgnoreCase);
        if (iterMatch.Success)
        {
            var value = iterMatch.Groups[1].Success ? iterMatch.Groups[1].Value : iterMatch.Groups[2].Value;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iter))
                metadata["iteration"] = iter;
        }

        return metadata;
    }
}