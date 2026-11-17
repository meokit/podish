using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Podish.PerfTools;

internal static partial class Program
{
    internal sealed record TrainingSuite(int SchemaVersion, string Id, uint Seed, int Bytes, int Iterations,
        string[] Cases, int[] LightSizes, int TopPairs, double MinimumHotMass, double MinimumTopOverlap,
        double MaximumJensenShannon, int TimeoutSeconds, string Corpus = "records-v1", int TarFileBytes = 65536,
        string Purpose = "unspecified");
    private sealed record TrainingPair(string First, string Second, ulong Count);
    private sealed record TrainingNGram(string[] Ops, ulong Count);
    private sealed record TrainingProfile(int SchemaVersion, string CounterSemantics, uint BuildFeatures,
        string SuperOpcodeSetHash, TrainingPair[] Pairs, string? NgramSemantics = null, TrainingNGram[]? Ngrams = null);
    private sealed record TrainingSample(string Case, int Bytes, double Seconds, string OutputHash,
        string NativeHash, uint BuildFeatures, string SetHash, TrainingPair[] Pairs, TrainingNGram[]? Ngrams = null);
    private sealed record TrainingRun(int SchemaVersion, string SuiteHash, string SuiteId, string RootfsLockHash,
        string NativeHash, string Host, string Runtime, TrainingSample[] Samples, string RuntimePackageHash = "",
        string CollectorSourceHash = "", string Scope = "whole guest invocation including GNU bash and dynamic loader",
        double WallSeconds = 0);
    internal sealed record DistributionQuality(double HotMass, double TopOverlap, double JensenShannon);
    private sealed record TrainingBuild(string Flavor, string Cli, string NativeHash, string NativePath, string SetHash);

    private static JsonSerializerOptions TrainingJson => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static int RunTrainingCommand(string[] args)
    {
        var command = args.FirstOrDefault() ?? throw new ArgumentException("superopcode requires build, collect, distill, train, benchmark or verify");
        var root = Path.GetFullPath(GetValue(args, "--project-root") ?? RepoRoot());
        var output = Path.GetFullPath(GetValue(args, "--output") ?? Path.Combine(root, "artifacts", "superopcode", "run"));
        var suitePath = Path.GetFullPath(GetValue(args, "--suite") ?? Path.Combine(root, "benchmark", "superopcode", "heavy-v1.json"));
        var rootfs = Path.GetFullPath(GetValue(args, "--rootfs") ?? Path.Combine(root, "artifacts", "superopcode", "gnu-rootfs"));
        Directory.CreateDirectory(output);
        var leaseDirectory = Path.Combine(root, "artifacts", "superopcode", "builds");
        Directory.CreateDirectory(leaseDirectory);
        // A live native module must not be overwritten by a concurrent workflow.
        using var lease = File.Open(Path.Combine(leaseDirectory, "workflow.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        // Existing helpers assume the repository root for header discovery, not caller CWD.
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(root);
        try
        {
            switch (command)
            {
                case "build":
                    BuildTrainingVariant(root, RequireValue(args, "--flavor"), GetValue(args, "--generated"));
                    break;
                case "collect":
                    var suite = ReadTraining<TrainingSuite>(suitePath);
                    var build = BuildTrainingVariant(root, "training", null);
                    WriteTraining(Path.Combine(output, "profile.json"), CollectTraining(suite, suitePath, rootfs, build, output));
                    break;
                case "distill":
                    DistillTraining(root, suitePath, rootfs, output);
                    break;
                case "train":
                    DistillTraining(root, suitePath, rootfs, output);
                    var candidate = Path.Combine(output, "superopcodes.generated.cpp");
                    BuildTrainingVariant(root, "candidate", candidate);
                    VerifyTraining(root, candidate, Path.Combine(output, "verification"));
                    break;
                case "benchmark":
                    BenchmarkTraining(root, suitePath, rootfs, output,
                        GetIntValue(args, "--repeat", 7), RequireValue(args, "--generated"));
                    break;
                case "verify":
                    VerifyTraining(root, RequireValue(args, "--generated"), output);
                    break;
                case "search":
                    SearchTraining(root, rootfs, RequireValue(args, "--plan"), output, GetValue(args, "--stage") ?? "screen");
                    break;
                default: throw new ArgumentException("Unknown superopcode command: " + command);
            }
            return 0;
        }
        finally { Directory.SetCurrentDirectory(previous); }
    }

    private static TrainingBuild BuildTrainingVariant(string root, string flavor, string? generated)
    {
        if (!Regex.IsMatch(flavor, @"^(training|off|pinned|candidate|tune-[a-z0-9-]+)$")) throw new ArgumentException("Invalid flavor");
        if ((flavor == "candidate" || flavor.StartsWith("tune-", StringComparison.Ordinal)) && (generated is null || !File.Exists(generated)))
            throw new FileNotFoundException("Candidate generation must precede its build", generated);
        var enabled = IsOptimizedTrainingFlavor(flavor);
        var arguments = new List<string>
        {
            "build", "Podish.Cli/Podish.Cli.csproj", "-c", "Release", "--nologo", "-clp:ErrorsOnly;Summary",
            "-p:FusionStrategy=BASE", "-p:OperandSpecialization=OFF", "-p:OperandScope=ALL", "-p:GprPin=OFF",
            "-p:RollForward=Major", $"-p:SuperOpcodeFlavor={flavor}", $"-p:NativeBuildDir=build_native/{flavor}",
            $"-p:EnableSuperOpcodes={enabled.ToString().ToLowerInvariant()}",
            $"-p:EnableSequenceProfile={(flavor == "training").ToString().ToLowerInvariant()}",
            "-p:EnableBlockProfile=false", "-p:EnableHandlerProfile=false"
        };
        if (generated is not null) arguments.Add("-p:SuperOpcodeSource=" + Path.GetFullPath(generated));
        TrainingProcess("dotnet", arguments, root, 1800);
        var directory = Path.Combine(root, "artifacts", "superopcode", "builds", flavor, "managed", "Podish.Cli", "bin", "Release", "net10.0");
        var file = OperatingSystem.IsWindows() ? "fibercpu.dll" : OperatingSystem.IsMacOS() ? "libfibercpu.dylib" : "libfibercpu.so";
        var native = Path.Combine(root, "Fiberish.X86", "build_native", flavor,
            OperatingSystem.IsWindows() ? "Release" : "", file);
        // PreserveNewest must never choose a different flavor's newer DLL.
        File.Copy(native, Path.Combine(directory, file), true);
        var setHash = enabled ? TrainingHash(generated ?? Path.Combine(root, "libfibercpu", "src", "generated", "superopcodes.generated.cpp")) : "none";
        var result = new TrainingBuild(flavor, Path.Combine(directory, "Podish.Cli.dll"), TrainingHash(native), native, setHash);
        WriteTraining(Path.Combine(directory, "training-build.json"), result);
        return result;
    }

    internal static void ValidateTrainingSuite(TrainingSuite suite)
    {
        if (suite.SchemaVersion != 1 || string.IsNullOrWhiteSpace(suite.Id) || suite.Seed == 0 ||
            suite.Bytes < 4096 || suite.Bytes > 128 * 1024 * 1024 ||
            suite.Iterations is < 1 or > 100 || suite.TimeoutSeconds is < 1 or > 3600 ||
            !double.IsFinite(suite.MinimumHotMass) || !double.IsFinite(suite.MinimumTopOverlap) ||
            !double.IsFinite(suite.MaximumJensenShannon) ||
            suite.TopPairs is < 1 or > 512 || suite.MinimumHotMass is <= 0 or > 1 ||
            suite.MinimumTopOverlap is <= 0 or > 1 || suite.MaximumJensenShannon is < 0 or > 1 ||
            suite.Cases is null || suite.LightSizes is null || suite.LightSizes.Any(size => size < 4096 || size >= suite.Bytes) ||
            suite.Cases.Length == 0 || suite.Cases.Distinct(StringComparer.Ordinal).Count() != suite.Cases.Length ||
            suite.TarFileBytes is < 512 or > 1048576 ||
            suite.Corpus is not ("records-v1" or "logs-v2" or "source-v2" or "entropy-v2"))
            throw new InvalidDataException("Invalid training suite limits");
        foreach (var name in suite.Cases)
            if (name is not ("tar-create" or "tar-extract" or "gzip-compress" or "gzip-decompress" or
                "sed-transform" or "grep-fixed" or "grep-regex")) throw new InvalidDataException("Unknown workload: " + name);
    }

    private static string ValidateTrainingRootfs(string rootfs)
    {
        if (TrainingHash(Path.Combine(rootfs, "packages.lock.json")) !=
            TrainingHash(Path.Combine(RepoRoot(), "benchmark", "superopcode", "packages.lock.json")))
            throw new InvalidDataException("Rootfs packages differ from the repository lock");
        var lockPath = Path.Combine(rootfs, "rootfs.lock.json");
        var files = ReadTraining<Dictionary<string, string>>(lockPath);
        foreach (var (path, hash) in files)
        {
            var full = Path.GetFullPath(Path.Combine(rootfs, path));
            if (!full.StartsWith(rootfs + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                TrainingHash(full) != hash) throw new InvalidDataException("Rootfs integrity failure: " + path);
        }
        return TrainingHash(lockPath);
    }

    private static TrainingRun CollectTraining(TrainingSuite suite, string suitePath, string rootfs,
        TrainingBuild build, string output)
    {
        var wall = Stopwatch.StartNew();
        ValidateTrainingSuite(suite);
        var lockHash = ValidateTrainingRootfs(rootfs);
        ValidateGnuTools(rootfs, build, output);
        var samples = new List<TrainingSample>();
        foreach (var name in suite.Cases)
        {
            var sampleDirectory = Path.Combine(output, name);
            Directory.CreateDirectory(sampleDirectory);
            var sample = RunTrainingWorkload(suite, name, rootfs, build, sampleDirectory, true);
            samples.Add(sample);
            Console.WriteLine($"[training] {name} bytes={suite.Bytes} elapsed={sample.Seconds:F3}s pairs={sample.Pairs.Length}");
        }
        return new(1, TrainingHash(suitePath), suite.Id, lockHash, build.NativeHash,
            RuntimeInformation.OSDescription + "/" + RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.FrameworkDescription, samples.ToArray(), TrainingRuntimeHash(build),
            TrainingCollectorHash(),
            WallSeconds: wall.Elapsed.TotalSeconds);
    }

    private static void ValidateGnuTools(string rootfs, TrainingBuild build, string output)
    {
        var text = TrainingGuest(build, rootfs,
            "set -eu\n/bin/tar --version\n/bin/gzip --version\n/bin/sed --version\n/bin/grep --version", null, 120).Output;
        foreach (var marker in new[] { "GNU tar", "gzip 1.13", "GNU sed", "GNU grep" })
            if (!text.Contains(marker, StringComparison.Ordinal)) throw new InvalidDataException("GNU implementation check failed: " + marker);
        if (text.Contains("BusyBox", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("BusyBox is not a valid GNU training workload");
        File.WriteAllText(Path.Combine(output, "gnu-versions.txt"), text, Utf8NoBom);
    }

    private static TrainingSample RunTrainingWorkload(TrainingSuite suite, string name, string rootfs,
        TrainingBuild build, string output, bool collect, long affinityMask = 0)
    {
        var workName = "superopcode-" + Guid.NewGuid().ToString("N");
        var host = Path.Combine(rootfs, "work", workName);
        var guest = "/work/" + workName;
        Directory.CreateDirectory(host);
        if (name == "tar-create") Directory.CreateDirectory(Path.Combine(host, "input"));
        if (name == "tar-extract") Directory.CreateDirectory(Path.Combine(host, "extract"));
        var fixture = CreateTrainingText(suite.Corpus, suite.Seed, suite.Bytes);
        var input = Encoding.ASCII.GetBytes(fixture.Text);
        if (!name.StartsWith("tar-", StringComparison.Ordinal)) File.WriteAllBytes(Path.Combine(host, "input.txt"), input);
        var dataFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (name.StartsWith("tar-", StringComparison.Ordinal))
        for (var offset = 0; offset < input.Length; offset += suite.TarFileBytes)
        {
            var file = $"input/{offset / suite.TarFileBytes:D5}.txt";
            var bytes = input.AsSpan(offset, Math.Min(suite.TarFileBytes, input.Length - offset)).ToArray();
            if (name == "tar-create") File.WriteAllBytes(Path.Combine(host, file), bytes);
            dataFiles[file] = bytes;
        }
        if (name == "gzip-decompress")
            using (var stream = File.Create(Path.Combine(host, "prepared.gz")))
            using (var gzip = new GZipStream(stream, CompressionLevel.Optimal)) gzip.Write(input);
        if (name == "tar-extract")
            using (var stream = File.Create(Path.Combine(host, "prepared.tar")))
            using (var tar = new TarWriter(stream, TarEntryFormat.Ustar))
                foreach (var (file, bytes) in dataFiles)
                {
                    using var data = new MemoryStream(bytes);
                    tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, file) { DataStream = data, ModificationTime = DateTimeOffset.UnixEpoch });
                }
        var command = name switch
        {
            "tar-create" => "/bin/tar --format=ustar --sort=name --mtime=@0 --owner=0 --group=0 --numeric-owner -cf result.tar input",
            "tar-extract" => "/bin/tar -xf prepared.tar -C extract",
            "gzip-compress" => "/bin/gzip -n -6 -c input.txt > result.gz",
            "gzip-decompress" => "/bin/gzip -dc prepared.gz > result.txt",
            "sed-transform" => "/bin/sed " + fixture.SedArguments + " input.txt > result.txt",
            "grep-fixed" => $"/bin/grep -F -c '{fixture.FixedPattern}' input.txt > result.txt",
            "grep-regex" => $"/bin/grep -E -c '{fixture.RegexPattern}' input.txt > result.txt",
            _ => throw new InvalidDataException("Unknown workload")
        };
        var script = $"set -eu\ncd '{guest}'\nprintf '__SO_BEGIN__\\n'\nfor ((i=0; i<{suite.Iterations}; i++)); do\n{command}\ndone\nprintf '__SO_END__\\n'\n";
        var stats = Path.Combine(output, "guest-stats");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "guest-script.sh"), script, Utf8NoBom);
        var result = TrainingGuest(build, rootfs, script, stats, suite.TimeoutSeconds, affinityMask);
        File.WriteAllText(Path.Combine(output, "transcript.log"), result.Output, Utf8NoBom);
        if (result.Seconds <= 0) throw new InvalidDataException("Missing workload timing markers");
        var expected = input;
        byte[] actual;
        if (name == "gzip-compress")
        {
            using var stream = new GZipStream(File.OpenRead(Path.Combine(host, "result.gz")), CompressionMode.Decompress);
            using var decoded = new MemoryStream(); stream.CopyTo(decoded); actual = decoded.ToArray();
        }
        else if (name.StartsWith("tar-", StringComparison.Ordinal))
        {
            if (name == "tar-create")
            {
                using var tar = new TarReader(File.OpenRead(Path.Combine(host, "result.tar")));
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (tar.GetNextEntry() is { } entry)
                {
                    if (entry.EntryType == TarEntryType.Directory) continue;
                    if (!dataFiles.TryGetValue(entry.Name, out var original) || !seen.Add(entry.Name))
                        throw new InvalidDataException("Unexpected tar member: " + entry.Name);
                    using var bytes = new MemoryStream(); entry.DataStream!.CopyTo(bytes);
                    if (!bytes.ToArray().SequenceEqual(original)) throw new InvalidDataException("Tar payload mismatch");
                }
                if (seen.Count != dataFiles.Count) throw new InvalidDataException("Missing tar members");
            }
            else foreach (var (file, bytes) in dataFiles)
                if (!File.ReadAllBytes(Path.Combine(host, "extract", file)).SequenceEqual(bytes))
                    throw new InvalidDataException("Tar extraction mismatch");
            actual = input;
        }
        else
        {
            actual = File.ReadAllBytes(Path.Combine(host, "result.txt"));
            if (name == "sed-transform") expected = Encoding.ASCII.GetBytes(fixture.SedExpected);
            if (name.StartsWith("grep-", StringComparison.Ordinal))
            {
                var lines = fixture.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var count = name == "grep-fixed" ? lines.Count(line => line.Contains(fixture.FixedPattern, StringComparison.Ordinal))
                    : lines.Count(line => Regex.IsMatch(line, fixture.RegexPattern, RegexOptions.CultureInvariant));
                expected = Encoding.ASCII.GetBytes(count.ToString(CultureInfo.InvariantCulture) + "\n");
            }
        }
        if (!actual.SequenceEqual(expected)) throw new InvalidDataException("Independent output validation failed: " + name);
        var profile = ReadTraining<TrainingProfile>(Path.Combine(stats, "sequence_profile.json"));
        var enabled = IsOptimizedTrainingFlavor(build.Flavor);
        var expectedFeatures = (enabled ? 1u : 0u) | (collect ? 2u : 0u);
        if (profile.BuildFeatures != expectedFeatures) throw new InvalidDataException("Wrong native flavor loaded");
        if (profile.SuperOpcodeSetHash != build.SetHash)
            throw new InvalidDataException("Superopcode identity mismatch");
        if (profile.CounterSemantics != "within-block-continue-edges-v1" || collect && profile.Pairs.Length == 0)
            throw new InvalidDataException("Empty or incompatible sequence profile");
        var normalized = profile.Pairs.GroupBy(pair => (NormalizeTrainingHandler(pair.First), NormalizeTrainingHandler(pair.Second)))
            .Select(group => new TrainingPair(group.Key.Item1, group.Key.Item2, group.Aggregate(0UL, (sum, pair) => checked(sum + pair.Count))))
            .OrderBy(pair => pair.First, StringComparer.Ordinal).ThenBy(pair => pair.Second, StringComparer.Ordinal).ToArray();
        if (profile.SchemaVersion != 2 || profile.NgramSemantics != "consecutive-within-block-dispatch-entries-v1" || profile.Ngrams is null)
            throw new InvalidDataException("Missing exact ngram profile schema");
        var ngrams = profile.Ngrams.Select(ngram => new TrainingNGram(ngram.Ops.Select(NormalizeTrainingHandler).ToArray(), ngram.Count))
            .GroupBy(ngram => string.Join('\t', ngram.Ops), StringComparer.Ordinal)
            .Select(group => new TrainingNGram(group.First().Ops, group.Aggregate(0UL, (sum, entry) => checked(sum + entry.Count))))
            .OrderBy(ngram => string.Join('\t', ngram.Ops), StringComparer.Ordinal).ToArray();
        var sample = new TrainingSample(name, input.Length, result.Seconds, Convert.ToHexString(SHA256.HashData(actual)),
            build.NativeHash, profile.BuildFeatures, profile.SuperOpcodeSetHash, normalized, ngrams);
        WriteTraining(Path.Combine(output, "sample.json"), sample);
        // Only the freshly created GUID-owned workload directory is removed.
        Directory.Delete(host, true);
        return sample;
    }

    internal static string NormalizeTrainingHandler(string symbol)
    {
        var matches = Regex.Matches(symbol, @"(?:fiberish::)?op::(Op[A-Za-z0-9_]+)");
        if (matches.Count != 1) throw new InvalidDataException("Cannot normalize training handler: " + symbol);
        return "op::" + matches[0].Groups[1].Value;
    }

    private static Dictionary<string, double> TrainingDistribution(TrainingRun run)
    {
        var distribution = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var sample in run.Samples)
        {
            var total = sample.Pairs.Sum(pair => (double)pair.Count);
            if (total <= 0) throw new InvalidDataException("A required training sample has no edges");
            foreach (var pair in sample.Pairs)
            {
                var key = pair.First + "\t" + pair.Second;
                distribution[key] = distribution.GetValueOrDefault(key) + pair.Count / total / run.Samples.Length;
            }
        }
        return distribution;
    }

    internal static DistributionQuality CompareTrainingDistributions(Dictionary<string, double> reference,
        Dictionary<string, double> sample, int top)
    {
        var hot = reference.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(top).ToArray();
        var selected = sample.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(top).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var mass = hot.Where(pair => sample.ContainsKey(pair.Key)).Sum(pair => pair.Value) / hot.Sum(pair => pair.Value);
        var overlap = hot.Count(pair => selected.Contains(pair.Key)) / (double)hot.Length;
        double divergence = 0;
        foreach (var key in reference.Keys.Concat(sample.Keys).Distinct(StringComparer.Ordinal))
        {
            var p = reference.GetValueOrDefault(key);
            var q = sample.GetValueOrDefault(key);
            var middle = (p + q) / 2;
            if (p > 0) divergence += p / 2 * Math.Log2(p / middle);
            if (q > 0) divergence += q / 2 * Math.Log2(q / middle);
        }
        return new(mass, overlap, divergence);
    }

    private static void DistillTraining(string root, string suitePath, string rootfs, string output)
    {
        var suite = ReadTraining<TrainingSuite>(suitePath);
        ValidateTrainingSuite(suite);
        var build = BuildTrainingVariant(root, "training", null);
        var heavyPath = Path.Combine(output, "heavy", "profile.json");
        TrainingRun CollectOrRead(TrainingSuite specification, string path, string destination)
        {
            var profilePath = Path.Combine(destination, "profile.json");
            if (File.Exists(profilePath))
            {
                var cached = ReadTraining<TrainingRun>(profilePath);
                if (cached.SuiteHash == TrainingHash(path) && cached.NativeHash == build.NativeHash &&
                    cached.RootfsLockHash == ValidateTrainingRootfs(rootfs) &&
                    cached.RuntimePackageHash == TrainingRuntimeHash(build) &&
                    cached.CollectorSourceHash == TrainingCollectorHash()) return cached;
                throw new InvalidDataException("Existing profile is incompatible; use a new output directory: " + profilePath);
            }
            var result = CollectTraining(specification, path, rootfs, build, destination);
            WriteTraining(profilePath, result);
            return result;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(heavyPath)!);
        var heavy = CollectOrRead(suite, suitePath, Path.GetDirectoryName(heavyPath)!);
        var reference = TrainingDistribution(heavy);
        var trials = new List<object>();
        TrainingRun? distilled = null;
        TrainingSuite? selectedSuite = null;
        foreach (var size in suite.LightSizes.Order())
        {
            var lightSuite = CreateLightTrainingSuite(suite, size);
            var directory = Path.Combine(output, "light-" + size);
            Directory.CreateDirectory(directory);
            var lightSuitePath = Path.Combine(directory, "suite.json");
            WriteTraining(lightSuitePath, lightSuite);
            var light = CollectOrRead(lightSuite, lightSuitePath, directory);
            var quality = CompareTrainingDistributions(reference, TrainingDistribution(light), suite.TopPairs);
            var accepted = quality.HotMass >= suite.MinimumHotMass && quality.TopOverlap >= suite.MinimumTopOverlap &&
                           quality.JensenShannon <= suite.MaximumJensenShannon;
            trials.Add(new { bytes = size, quality, accepted, seconds = light.Samples.Sum(sample => sample.Seconds),
                wallSeconds = light.WallSeconds });
            Console.WriteLine($"[distill] bytes={size} mass={quality.HotMass:P2} topOverlap={quality.TopOverlap:P2} JS={quality.JensenShannon:F5} accepted={accepted}");
            if (accepted && distilled is null) { distilled = light; selectedSuite = lightSuite; }
        }
        WriteTraining(Path.Combine(output, "distillation.json"), new
        {
            schemaVersion = 1, referenceSuiteHash = heavy.SuiteHash, referenceNativeHash = heavy.NativeHash,
            selection = "smallest measured input satisfying fixed per-family-normalized thresholds; all GNU families retained",
            heavySeconds = heavy.Samples.Sum(sample => sample.Seconds), heavyWallSeconds = heavy.WallSeconds, trials,
            selectedBytes = selectedSuite?.Bytes
        });
        if (distilled is null || selectedSuite is null) throw new InvalidDataException("No measured lightweight sample passed distillation thresholds");
        WriteTraining(Path.Combine(output, "distilled-suite.json"), selectedSuite);
        var normalized = TrainingDistribution(distilled);
        var rawCounts = distilled.Samples.SelectMany(sample => sample.Pairs)
            .GroupBy(pair => pair.First + "\t" + pair.Second, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (Count: group.Aggregate(0UL, (sum, pair) => checked(sum + pair.Count)),
                Workloads: group.Count()), StringComparer.Ordinal);
        var candidates = normalized.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Where(pair => TrainingPairAllowed(pair.Key.Split('\t')[0], pair.Key.Split('\t')[1]))
            .Take(suite.TopPairs).Select(pair => new
            {
                pair = pair.Key.Split('\t'), weighted_exec_count = rawCounts[pair.Key].Count,
                occurrences = rawCounts[pair.Key].Workloads, score = pair.Value,
                relation_kind = "exact-continue-edge", anchor_display = pair.Key.Split('\t')[0],
                direction = "successor"
            }).ToArray();
        if (candidates.Length == 0) throw new InvalidDataException("No legal candidates");
        var selected = Path.Combine(output, "selected.json");
        WriteTraining(selected, new { schemaVersion = 1, metadata = new
        {
            strategy = "equal-workload-normalized-exact-edges-v1", sourceSuiteHash = distilled.SuiteHash,
            referenceSuiteHash = heavy.SuiteHash, superopcode_width = 2
        }, candidates });
        var generated = GenerateSuperopcodes(selected, suite.TopPairs);
        var path = Path.Combine(output, "superopcodes.generated.cpp");
        File.WriteAllText(path, generated, Utf8NoBom);
        if (generated != GenerateSuperopcodes(selected, suite.TopPairs)) throw new InvalidDataException("Nondeterministic generator");
        Console.WriteLine($"[distill] selected {selectedSuite.Bytes} bytes, {candidates.Length} pairs; generated {path}");
    }

    internal static TrainingSuite CreateLightTrainingSuite(TrainingSuite suite, int size)
    {
        if (!suite.LightSizes.Contains(size)) throw new ArgumentOutOfRangeException(nameof(size));
        var light = suite with { Id = suite.Id + "-light-" + size, Bytes = size, LightSizes = [] };
        ValidateTrainingSuite(light);
        return light;
    }

    internal static bool TrainingPairAllowed(string first, string second)
    {
        // Conservative family allowlist; the existing common flow wrapper handles
        // faults/restarts at either constituent. Unknown/control/string protocols
        // are not admitted merely because they appeared in the profile.
        const string ordinary = @"^op::Op(Mov|Lea|Cmp|Test|Add|Sub|And|Or|Xor|Inc|Dec|Neg|Push|Pop|Group1_|Group2_|Group3_|Group4_|Group5_|Cmov|Setcc|Shl|Shr|Sar|Rol|Ror|Imul|Mul|Cdq|Cwde)";
        return Regex.IsMatch(first, ordinary) && Regex.IsMatch(second, ordinary + "|^op::OpJcc_") &&
               !Regex.IsMatch(first, "Call|Jmp|Ret|String|Movs_|Movs[bdw]|Cmps|Scas|Stos|Lods", RegexOptions.CultureInvariant) &&
               !Regex.IsMatch(second, "Call|Jmp|Ret|String|Movs_|Movs[bdw]|Cmps|Scas|Stos|Lods", RegexOptions.CultureInvariant);
    }

    private static void VerifyTraining(string root, string generated, string output, string candidateFlavor = "candidate")
    {
        Directory.CreateDirectory(output);
        Dictionary<string, string>? baseline = null;
        foreach (var flavor in new[] { "off", candidateFlavor })
        {
            BuildTrainingVariant(root, flavor, flavor != "off" ? generated : null);
            var file = Path.Combine(output, "correctness-" + flavor + ".trx");
            // Never let a failed test/build command reuse an older successful TRX.
            var freshFile = Path.Combine(output, $"correctness-{flavor}-{Guid.NewGuid():N}.trx");
            var arguments = new List<string> { "test", "Fiberish.X86.Tests/Fiberish.X86.Tests.csproj", "-c", "Release", "--nologo",
                "-clp:ErrorsOnly;Summary", "-p:FusionStrategy=BASE", "-p:OperandSpecialization=OFF", "-p:OperandScope=ALL", "-p:GprPin=OFF",
                "-p:RollForward=Major", $"-p:SuperOpcodeFlavor={flavor}",
                $"-p:NativeBuildDir=build_native/{flavor}", $"-p:EnableSuperOpcodes={(flavor != "off").ToString().ToLowerInvariant()}",
                "-p:EnableSequenceProfile=false", "-p:EnableBlockProfile=false", "-p:EnableHandlerProfile=false",
                "--filter", "Category!=Exhaustive", "--logger", "trx;LogFileName=" + freshFile,
                "--blame-hang-timeout", "2m" };
            if (flavor != "off") arguments.Add("-p:SuperOpcodeSource=" + Path.GetFullPath(generated));
            // dotnet test remains red for the six documented upstream x87 cases.
            // Require exactly those failures and compare every row, not exit-code suppression.
            var result = TrainingProcess("dotnet", arguments, root, 1800, allowFailure: true);
            File.WriteAllText(Path.Combine(output, "correctness-" + flavor + ".log"), result.Output, Utf8NoBom);
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var rows = XDocument.Load(freshFile).Descendants(ns + "UnitTestResult").ToArray();
            if (rows.Length < 2000) throw new InvalidDataException("Incomplete correctness test report");
            var known = new[] { "X87/D9_F2", "X87/D9_F3", "X87/D9_FB", "X87/D9_FE", "X87/D9_FF", "X87/D9_F9" };
            var failed = rows.Where(row => (string?)row.Attribute("outcome") != "Passed").ToArray();
            if (failed.Length != known.Length || known.Any(id => failed.Count(row =>
                    (string?)row.Attribute("outcome") == "Failed" &&
                    ((string?)row.Attribute("testName") ?? "").Contains('"' + id + '"', StringComparison.Ordinal)) != 1))
                throw new InvalidDataException("Correctness result differs from reviewed six-case x87 baseline");
            var outcomes = rows.ToDictionary(row => (string)row.Attribute("testName")!, row =>
                (string?)row.Attribute("outcome") + "|" + row.Descendants(ns + "Message").FirstOrDefault()?.Value, StringComparer.Ordinal);
            if (baseline is not null && (outcomes.Count != baseline.Count || outcomes.Any(row => !baseline.TryGetValue(row.Key, out var value) || value != row.Value)))
                throw new InvalidDataException("OFF/candidate correctness results differ");
            baseline = outcomes;
            File.Move(freshFile, file, true);
        }
        WriteTraining(Path.Combine(output, "correctness.json"), new { comparedRows = baseline!.Count,
            identical = true, knownX87Failures = 6, fullSuiteGreen = false });
    }

    private static void BenchmarkTraining(string root, string suitePath, string rootfs, string output, int repeat, string generated)
    {
        if (repeat is < 3 or > 30) throw new ArgumentOutOfRangeException(nameof(repeat), "Use 3..30 interleaved samples");
        var suite = ReadTraining<TrainingSuite>(suitePath);
        ValidateTrainingSuite(suite);
        var rootfsHash = ValidateTrainingRootfs(rootfs);
        var builds = new[] { BuildTrainingVariant(root, "off", null), BuildTrainingVariant(root, "pinned", null), BuildTrainingVariant(root, "candidate", generated) };
        foreach (var build in builds) ValidateGnuTools(rootfs, build, output);
        var rows = new List<object>();
        var durations = new Dictionary<(string Case, string Flavor), List<double>>();
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in suite.Cases)
        {
            // Untimed process/host setup is separate from the guest marker interval.
            for (var round = -1; round < repeat; round++)
            for (var position = 0; position < builds.Length; position++)
            {
                var build = builds[(position + Math.Max(round, 0)) % builds.Length];
                var directory = Path.Combine(output, "samples", name, round < 0 ? "warmup-" + build.Flavor : $"{round:D2}-{build.Flavor}");
                var sample = RunTrainingWorkload(suite, name, rootfs, build, directory, false);
                if (hashes.TryGetValue(name, out var hash) && hash != sample.OutputHash) throw new InvalidDataException("Variant output hashes differ");
                hashes[name] = sample.OutputHash;
                if (round < 0) continue;
                if (!durations.TryGetValue((name, build.Flavor), out var list)) durations[(name, build.Flavor)] = list = [];
                list.Add(sample.Seconds);
                rows.Add(new { workload = name, flavor = build.Flavor, round, sample.Seconds, sample.Bytes, sample.OutputHash,
                    sample.NativeHash, sample.SetHash, sample.BuildFeatures });
                Console.WriteLine($"[benchmark] {name} {build.Flavor} round={round} seconds={sample.Seconds:F4}");
                WriteTraining(Path.Combine(output, "samples.json"), rows);
            }
        }
        var summary = suite.Cases.Select(name =>
        {
            double MedianFor(string flavor) => TrainingMedian(durations[(name, flavor)]);
            var off = MedianFor("off"); var pinned = MedianFor("pinned"); var candidate = MedianFor("candidate");
            double MadFor(string flavor, double median) => TrainingMedian(durations[(name, flavor)]
                .Select(value => Math.Abs(value - median)).ToList());
            return new { workload = name, off, pinned, candidate, speedupVsOff = off / candidate,
                speedupVsPinned = pinned / candidate, samples = repeat,
                offMad = MadFor("off", off), pinnedMad = MadFor("pinned", pinned), candidateMad = MadFor("candidate", candidate) };
        }).ToArray();
        WriteTraining(Path.Combine(output, "benchmark.json"), new { schemaVersion = 1, suiteHash = TrainingHash(suitePath), rootfsHash,
            host = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            runtime = RuntimeInformation.FrameworkDescription,
            scope = "host-observed guest command markers; includes guest exec/decode and any in-interval JIT; excludes host fixtures and pre-marker CLI startup",
            generatedHash = TrainingHash(generated), builds, suite, summary,
            geometricSpeedupVsPinned = Math.Exp(summary.Average(row => Math.Log(row.speedupVsPinned))),
            geometricSpeedupVsOff = Math.Exp(summary.Average(row => Math.Log(row.speedupVsOff))) });
        var markdown = new StringBuilder("# GNU superopcode benchmark\n\n| Workload | OFF seconds | Pinned seconds | Candidate seconds | Speedup/pinned |\n|---|---:|---:|---:|---:|\n");
        foreach (var row in summary) markdown.Append(CultureInfo.InvariantCulture, $"| {row.workload} | {row.off:F4} | {row.pinned:F4} | {row.candidate:F4} | {row.speedupVsPinned:F3}x |\n");
        File.WriteAllText(Path.Combine(output, "benchmark.md"), markdown.ToString(), Utf8NoBom);
    }

    private static double TrainingMedian(List<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) throw new InvalidDataException("No measurements");
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static (double Seconds, string Output) TrainingGuest(TrainingBuild build, string rootfs, string script,
        string? stats, int timeout, long affinityMask = 0)
    {
        var arguments = new List<string> { build.Cli, "run", "--rm", "--init", "--log-driver", "none", "--network", "private",
            "--rootfs", rootfs, "--env", "LC_ALL=C", "--env", "LANG=C", "--env", "TZ=UTC", "--env", "PATH=/bin:/usr/bin" };
        if (stats is not null) arguments.AddRange(["--guest-stats-dir", stats]);
        arguments.AddRange(["/bin/bash", "--noprofile", "--norc", "-c", script]);
        return TrainingProcess("dotnet", arguments, Directory.GetCurrentDirectory(), timeout, stats is not null,
            affinityMask: affinityMask);
    }

    private static (double Seconds, string Output) TrainingProcess(string program, IEnumerable<string> arguments,
        string root, int timeout, bool markers = false, bool allowFailure = false, long affinityMask = 0)
    {
        var info = new ProcessStartInfo(program) { WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        info.Environment["PODISH_SKIP_BLOCKS_EXPORT"] = "1";
        info.Environment["FIBERCPU_DISABLE_FATAL_SIGNAL_HANDLER"] = "1";
        var lines = new StringBuilder();
        var gate = new object();
        long begin = 0, end = 0;
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null) return;
            lock (gate)
            {
                if (args.Data.Trim() == "__SO_BEGIN__") begin = Stopwatch.GetTimestamp();
                if (args.Data.Trim() == "__SO_END__") end = Stopwatch.GetTimestamp();
                lines.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) lock (gate) lines.AppendLine(args.Data); };
        Console.WriteLine($"[superopcode] {program} {string.Join(' ', arguments.Take(4))}");
        process.Start();
        if (affinityMask != 0)
        {
            try
            {
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) process.ProcessorAffinity = (nint)affinityMask;
                else throw new PlatformNotSupportedException("Explicit benchmark affinity is supported on Windows/Linux only");
            }
            catch { process.Kill(true); process.WaitForExit(); throw; }
        }
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        if (!process.WaitForExit(checked(timeout * 1000))) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("Training process timed out"); }
        process.WaitForExit();
        var text = lines.ToString();
        if (process.ExitCode != 0 && !allowFailure) throw new InvalidOperationException($"Process exited {process.ExitCode}:\n{text}");
        if (markers && (begin == 0 || end <= begin)) throw new InvalidDataException("Missing/invalid timing marker interval");
        return ((end - begin) / (double)Stopwatch.Frequency, text);
    }

    private static T ReadTraining<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), TrainingJson)
        ?? throw new InvalidDataException("Empty JSON: " + path);
    private static void WriteTraining<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, TrainingJson).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", Utf8NoBom);
        File.Move(temporary, path, true);
    }
    private static string TrainingHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static bool IsOptimizedTrainingFlavor(string flavor) => flavor is not ("off" or "training");

    private static string TrainingCollectorHash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        TrainingHash(Path.Combine(RepoRoot(), "Podish.PerfTools", "Program.Training.cs")) + "\n" +
        TrainingHash(Path.Combine(RepoRoot(), "Podish.PerfTools", "Program.TrainingFixtures.cs"))))).ToLowerInvariant();

    private static string TrainingRuntimeHash(TrainingBuild build)
    {
        var directory = Path.GetDirectoryName(build.Cli)!;
        var hashes = Directory.EnumerateFiles(directory).Where(path => path.EndsWith(".dll", StringComparison.Ordinal) ||
                path.EndsWith(".so", StringComparison.Ordinal) || path.EndsWith(".dylib", StringComparison.Ordinal))
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(path => Path.GetFileName(path) + "=" + TrainingHash(path));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', hashes)))).ToLowerInvariant();
    }
}
