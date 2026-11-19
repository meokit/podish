using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Podish.PerfTools;

internal static partial class Program
{
    private sealed record SelectionStrategy(string Id, string Mode, int Budget, double CostPower = 0, string? Source = null,
        int Width = 2, string? BaseManifest = null);
    private sealed record SelectionPlan(int SchemaVersion, string Id, TrainingSuite[] TrainingSuites,
        TrainingSuite[] DevelopmentSuites, TrainingSuite[] ValidationSuites, SelectionStrategy[] Strategies,
        string ReferenceGenerated, int ScreenRepeats, int ConfirmationRepeats, int ValidationRepeats,
        double MinimumSpeedup, double MinimumFamilyRatio, double MinimumCaseRatio,
        bool Pairwise = false, long DevelopmentAffinityMask = 0, long ValidationAffinityMask = 0);
    private sealed record SelectionProgram(string Id, string Flavor, string Source, string SourceHash, int Pairs);
    private sealed record SelectionProfiles(TrainingSuite Suite, TrainingRun Heavy, TrainingRun Light);
    private sealed record SelectionSample(string Dataset, string Workload, string Variant, int Round,
        double Seconds, string OutputHash, uint BuildFeatures, string NativeHash, string SetHash, string Comparison = "");
    private sealed record SelectionCase(string Dataset, string Workload, string Variant, double Median, double Mad,
        double RatioVsPinned, double RatioVsOff, double PairedRatioVsPinned, double PinnedMedian = 0);
    private sealed record SelectionMetric(string Variant, double GeometricVsPinned, double GeometricVsOff,
        double PairedVsPinned, double BootstrapLower, double BootstrapUpper, double WorstFamilyRatio,
        double WorstCaseRatio, double Score, bool Accepted);
    private sealed record SelectionBenchmark(string PlanHash, string Stage, int Repeats, string Scope,
        SelectionProgram[] Programs, TrainingBuild[] Builds, SelectionCase[] Cases, SelectionMetric[] Metrics);

    private static void SearchTraining(string root, string rootfs, string planPath, string output, string stage)
    {
        if (stage is not ("prepare" or "screen" or "confirm" or "validate")) throw new ArgumentException("Invalid search stage");
        var plan = ReadTraining<SelectionPlan>(planPath);
        ValidateSelectionPlan(plan);
        var planHash = TrainingHash(planPath);
        var identity = Path.Combine(output, "search-identity.json");
        var runnerHash = TrainingHash(Path.Combine(root, "Podish.PerfTools", "Program.Selection.cs"));
        var identityData = File.Exists(identity) ? ReadTraining<Dictionary<string, string>>(identity) :
            new Dictionary<string, string> { ["planHash"] = planHash, ["plan"] = planPath, ["runnerHash"] = runnerHash };
        if (identityData["planHash"] != planHash || identityData["runnerHash"] != runnerHash)
            throw new InvalidDataException("Search plan/runner changed; use a new experiment directory");
        if (stage is "prepare" or "screen" && File.Exists(Path.Combine(output, "screen", "samples.json")))
            throw new InvalidDataException("Screening evidence already exists; do not regenerate its inputs");
        WriteTraining(identity, identityData);
        var provenance = Path.Combine(output, "provenance");
        Directory.CreateDirectory(provenance);
        foreach (var file in new[] { "Program.Selection.cs", "Program.Training.cs", "Program.TrainingFixtures.cs", "Program.Superopcodes.cs", "Program.SuperopcodeSequences.cs" })
        {
            var destination = Path.Combine(provenance, file);
            var current = Path.Combine(root, "Podish.PerfTools", file);
            if (!File.Exists(destination)) File.Copy(current, destination);
            else if (TrainingHash(destination) != TrainingHash(current))
                throw new InvalidDataException("Frozen experiment source changed: " + file);
        }
        foreach (var relative in new[] { "Podish.PerfTools/Program.cs", "Podish.PerfTools/Podish.PerfTools.csproj",
                     "Directory.Build.props", "Fiberish.X86/Fiberish.X86.csproj", "Fiberish.X86/Native/X86Native.Profiling.cs",
                     "Fiberish.X86.Tests/Fiberish.X86.Tests.csproj", "Fiberish.X86.Tests/Isolation/LongSequenceFusionTests.cs",
                     "Fiberish.X86.Tests/Isolation/SequenceProfileTests.cs", "Podish.PerfTools.Tests/TrainingTests.cs",
                     "benchmark/superopcode/sequence-probes.json",
                     "Fiberish.Core/Core/KernelRuntime.cs", "Podish.Core/ContainerRuntimeService.cs", "libfibercpu/CMakeLists.txt",
                     "libfibercpu/src/state.h", "libfibercpu/src/dispatch.h", "libfibercpu/src/bindings.h", "libfibercpu/src/bindings.cpp",
                     "libfibercpu/src/generated/superopcodes.generated.cpp" })
        {
            var current = Path.Combine(root, relative);
            var destination = Path.Combine(provenance, "workspace", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination)) File.Copy(current, destination);
            else if (TrainingHash(destination) != TrainingHash(current))
                throw new InvalidDataException("Frozen runtime/build source changed: " + relative);
        }
        if (!File.Exists(Path.Combine(provenance, "plan.json"))) File.Copy(planPath, Path.Combine(provenance, "plan.json"));
        SelectionProgram[] programs;
        if (stage is "prepare" or "screen")
        {
            ValidateTrainingRootfs(rootfs);
            var training = BuildTrainingVariant(root, "training", null);
            var profiles = plan.TrainingSuites.Select(suite =>
            {
                var heavy = CollectSelectionProfile(suite, training, rootfs, Path.Combine(output, "profiles", suite.Id, "heavy"));
                var lightSuite = CreateLightTrainingSuite(suite, suite.LightSizes.Single());
                var light = CollectSelectionProfile(lightSuite, training, rootfs, Path.Combine(output, "profiles", suite.Id, "light"));
                return new SelectionProfiles(suite, heavy, light);
            }).ToArray();
            programs = plan.Strategies.Select(strategy => GenerateSelectionProgram(root, output, strategy, profiles, plan.ReferenceGenerated)).ToArray();
            WriteTraining(Path.Combine(output, "programs.json"), programs);
            identityData["programsHash"] = TrainingHash(Path.Combine(output, "programs.json"));
            WriteTraining(identity, identityData);
            if (stage == "prepare") return;
            RunSelectionBenchmark(root, rootfs, output, plan, planHash, stage, plan.DevelopmentSuites, programs, plan.ScreenRepeats);
            return;
        }
        if (TrainingHash(Path.Combine(output, "programs.json")) != identityData["programsHash"])
            throw new InvalidDataException("Search program identities changed");
        programs = ReadTraining<SelectionProgram[]>(Path.Combine(output, "programs.json"));
        foreach (var program in programs)
            if (TrainingHash(program.Source) != program.SourceHash) throw new InvalidDataException("Search source changed: " + program.Id);
        var screen = ReadTraining<SelectionBenchmark>(Path.Combine(output, "screen", "benchmark.json"));
        if (screen.PlanHash != planHash) throw new InvalidDataException("Screening plan mismatch");
        var reference = programs.Single(program => program.Id == "legacy");
        if (stage == "confirm")
        {
            var finalists = screen.Metrics.Where(metric => metric.Variant is not ("off" or "pinned" or "legacy"))
                .OrderByDescending(metric => metric.Score).ThenBy(metric => metric.Variant, StringComparer.Ordinal).Take(2)
                .Select(metric => programs.Single(program => program.Id == metric.Variant)).ToArray();
            foreach (var finalist in finalists)
                VerifyTraining(root, finalist.Source, Path.Combine(output, "verification", finalist.Id), finalist.Flavor);
            RunSelectionBenchmark(root, rootfs, output, plan, planHash, stage, plan.DevelopmentSuites,
                finalists.Append(reference).ToArray(), plan.ConfirmationRepeats);
            return;
        }
        var confirmation = ReadTraining<SelectionBenchmark>(Path.Combine(output, "confirm", "benchmark.json"));
        if (confirmation.PlanHash != planHash) throw new InvalidDataException("Confirmation plan mismatch");
        var winnerMetric = confirmation.Metrics.Where(metric => metric.Variant is not ("off" or "pinned" or "legacy"))
            .OrderByDescending(metric => metric.Score).ThenBy(metric => metric.Variant, StringComparer.Ordinal).First();
        var winner = programs.Single(program => program.Id == winnerMetric.Variant);
        // Lock the decision before running ANY validation timings. No holdout-driven reranking.
        var decisionPath = Path.Combine(output, "decision.json");
        if (File.Exists(decisionPath)) throw new InvalidDataException("Validation was already unsealed; use a new explicitly versioned experiment");
        WriteTraining(decisionPath, new { planHash, winner, developmentMetric = winnerMetric,
            rationale = "development-only penalized paired speedup; fixed thresholds; validation never selects a candidate",
            validationSuiteIds = plan.ValidationSuites.Select(suite => suite.Id).ToArray() });
        RunSelectionBenchmark(root, rootfs, output, plan, planHash, stage, plan.ValidationSuites,
            [winner, reference], plan.ValidationRepeats);
    }

    private static void ValidateSelectionPlan(SelectionPlan plan)
    {
        if (plan.SchemaVersion != 1 || plan.TrainingSuites.Length < 2 || plan.DevelopmentSuites.Length < 2 ||
            plan.ValidationSuites.Length < 2 || plan.Strategies.Length < 2 || plan.ScreenRepeats < 3 ||
            plan.ConfirmationRepeats < 5 || plan.ValidationRepeats < 5 ||
            plan.ScreenRepeats > 30 || plan.ConfirmationRepeats > 30 || plan.ValidationRepeats > 30 ||
            plan.DevelopmentAffinityMask < 0 || plan.ValidationAffinityMask < 0 ||
            plan.MinimumSpeedup < 1 || plan.MinimumFamilyRatio is <= 0 or > 1 || plan.MinimumCaseRatio is <= 0 or > 1)
            throw new InvalidDataException("Invalid search plan");
        if (plan.DevelopmentAffinityMask != 0 || plan.ValidationAffinityMask != 0)
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                var allowed = (long)process.ProcessorAffinity;
                if ((plan.DevelopmentAffinityMask & allowed) != plan.DevelopmentAffinityMask ||
                    (plan.ValidationAffinityMask & allowed) != plan.ValidationAffinityMask)
                    throw new InvalidDataException("Requested benchmark CPUs are outside the allowed process affinity");
            }
            else throw new PlatformNotSupportedException("Explicit benchmark affinity requires Windows/Linux");
        }
        var all = plan.TrainingSuites.Concat(plan.DevelopmentSuites).Concat(plan.ValidationSuites).ToArray();
        if (all.Select(suite => suite.Id).Distinct(StringComparer.Ordinal).Count() != all.Length ||
            all.Select(suite => suite.Seed).Distinct().Count() != all.Length)
            throw new InvalidDataException("Datasets must have distinct identities and seeds across all splits");
        foreach (var suite in all) ValidateTrainingSuite(suite);
        if (plan.TrainingSuites.Any(suite => suite.Purpose != "training" || suite.LightSizes.Length != 1) ||
            plan.DevelopmentSuites.Any(suite => suite.Purpose != "development") ||
            plan.ValidationSuites.Any(suite => suite.Purpose != "validation")) throw new InvalidDataException("Incorrect split purpose");
        if (plan.Strategies.Select(strategy => strategy.Id).Distinct(StringComparer.Ordinal).Count() != plan.Strategies.Length ||
            plan.Strategies.Count(strategy => strategy.Id == "legacy" && strategy.Mode == "reference") != 1)
            throw new InvalidDataException("Unique strategy IDs and a legacy reference are required");
        foreach (var strategy in plan.Strategies)
            if (!Regex.IsMatch(strategy.Id, "^[a-z0-9-]+$") || strategy.Budget is < 1 or > 512 ||
                !double.IsFinite(strategy.CostPower) || strategy.CostPower is < 0 or > 2 ||
                strategy.Width is < 2 or > 4 ||
                strategy.Mode == "sequence" && strategy.Width < 3 ||
                strategy.Mode is not ("reference" or "light" or "heavy" or "slope" or "cost" or "coverage" or "union" or "branch" or "register" or "overlap" or
                    "density" or "density-branch" or "density-overlap" or "sequence" or "sequence-control"))
                throw new InvalidDataException("Invalid selection strategy");
    }

    private static TrainingRun CollectSelectionProfile(TrainingSuite suite, TrainingBuild build, string rootfs, string directory)
    {
        Directory.CreateDirectory(directory);
        var suitePath = Path.Combine(directory, "suite.json");
        WriteTraining(suitePath, suite);
        var path = Path.Combine(directory, "profile.json");
        if (File.Exists(path))
        {
            var cached = ReadTraining<TrainingRun>(path);
            if (cached.SchemaVersion != TrainingProfileVersion || cached.SuiteHash != TrainingHash(suitePath) || cached.NativeHash != build.NativeHash ||
                cached.RootfsLockHash != ValidateTrainingRootfs(rootfs) || cached.RuntimePackageHash != TrainingRuntimeHash(build) ||
                cached.CollectorSourceHash != TrainingCollectorHash()) throw new InvalidDataException("Incompatible search profile: " + path);
            return cached;
        }
        var collected = CollectTraining(suite, suitePath, rootfs, build, directory);
        WriteTraining(path, collected);
        return collected;
    }

    internal static Dictionary<string, double> PositiveTrainingSlope(IReadOnlyDictionary<string, double> heavy,
        IReadOnlyDictionary<string, double> light, double byteDifference)
    {
        if (byteDifference <= 0 || !double.IsFinite(byteDifference)) throw new ArgumentOutOfRangeException(nameof(byteDifference));
        return heavy.Select(pair => (pair.Key, Value: (pair.Value - light.GetValueOrDefault(pair.Key)) / byteDifference))
            .Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private static string SelectionPairName(TrainingSample sample, TrainingPair pair) =>
        ResolveTrainingOperation(sample.Operations, pair.First) + "\t" +
        ResolveTrainingOperation(sample.Operations, pair.Second);

    private static SortedDictionary<uint, string> SelectionOperations(SelectionProfiles[] profiles)
    {
        var operations = new SortedDictionary<uint, string>();
        MergeTrainingOperations(operations, profiles.SelectMany(profile => profile.Heavy.Samples)
            .SelectMany(sample => sample.Operations));
        return operations;
    }

    private static Dictionary<string, ulong> SelectionRawPairCounts(SelectionProfiles[] profiles) =>
        profiles.SelectMany(profile => profile.Heavy.Samples)
            .SelectMany(sample => sample.Pairs.Select(pair => (Name: SelectionPairName(sample, pair), pair.Count)))
            .GroupBy(pair => pair.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Aggregate(0UL, (sum, pair) => checked(sum + pair.Count)),
                StringComparer.Ordinal);

    private static List<Dictionary<string, double>> SelectionDistributions(SelectionProfiles[] profiles, string mode)
    {
        var distributions = new List<Dictionary<string, double>>();
        foreach (var profile in profiles)
        foreach (var heavy in profile.Heavy.Samples)
        {
            var light = profile.Light.Samples.Single(sample => sample.Case == heavy.Case);
            Dictionary<string, double> Counts(TrainingSample sample) => sample.Pairs.ToDictionary(
                pair => SelectionPairName(sample, pair), pair => (double)pair.Count, StringComparer.Ordinal);
            var counts = mode == "light" ? Counts(light) : mode == "heavy" ? Counts(heavy) :
                PositiveTrainingSlope(Counts(heavy), Counts(light), heavy.Bytes - light.Bytes);
            var total = counts.Values.Sum();
            if (total <= 0) throw new InvalidDataException("No payload slope for " + heavy.Case);
            var density = mode.StartsWith("density", StringComparison.Ordinal);
            // Dispatch opportunities per command second are closer to a fractional
            // wall-time objective than equally normalizing an I/O-bound tar case.
            // Timings are instrumented, so this remains an empirically tested heuristic.
            distributions.Add(ScaleSelectionDistribution(counts,
                density ? heavy.Bytes / heavy.Seconds : 1 / total));
        }
        return distributions;
    }

    internal static Dictionary<string, double> ScaleSelectionDistribution(IReadOnlyDictionary<string, double> counts, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        return counts.ToDictionary(pair => pair.Key, pair => pair.Value * scale, StringComparer.Ordinal);
    }

    internal static double EstimatedPairCost(string key)
    {
        double Cost(string op)
        {
            if (op.Contains("Jcc_", StringComparison.Ordinal)) return 1;
            if (op.Contains("_Load", StringComparison.Ordinal) || op.Contains("_Store", StringComparison.Ordinal)) return 4;
            if (op.Contains("_ModReg", StringComparison.Ordinal) || op.Contains("_Reg", StringComparison.Ordinal) ||
                Regex.IsMatch(op, "^op::Op(Lea|Cdq|Cwde)")) return 1;
            return 3;
        }
        var pair = key.Split('\t');
        if (pair.Length != 2) throw new InvalidDataException("Pair key must contain two handlers");
        return Cost(pair[0]) + Cost(pair[1]);
    }

    internal static string[] RankSelectionPairs(IReadOnlyList<Dictionary<string, double>> distributions, int budget,
        string mode, double costPower)
    {
        if (distributions.Count == 0 || budget < 1) throw new ArgumentOutOfRangeException(nameof(budget));
        var keys = distributions.SelectMany(distribution => distribution.Keys).Distinct(StringComparer.Ordinal)
            .Where(key => key.Split('\t') is [var first, var second] && TrainingPairAllowed(first, second))
            .Where(key => mode is not ("branch" or "density-branch") || key.Split('\t')[1].StartsWith("op::OpJcc_", StringComparison.Ordinal))
            .Where(key => mode != "register" || EstimatedPairCost(key) <= 4)
            .Order(StringComparer.Ordinal).ToArray();
        if (mode is "overlap" or "density-overlap") return RankOverlapPairs(distributions, keys, budget);
        var weights = keys.ToDictionary(key => key, key => distributions.Average(distribution => distribution.GetValueOrDefault(key)), StringComparer.Ordinal);
        if (mode != "coverage") return keys.OrderByDescending(key => weights[key] / Math.Pow(EstimatedPairCost(key), costPower))
            .ThenBy(key => key, StringComparer.Ordinal).Take(budget).ToArray();
        var remaining = keys.ToHashSet(StringComparer.Ordinal);
        var selected = new List<string>();
        var coverage = new double[distributions.Count];
        while (selected.Count < budget && remaining.Count > 0)
        {
            var best = remaining.OrderByDescending(key => distributions.Select((distribution, index) =>
                    Math.Log(1 + distribution.GetValueOrDefault(key) / (0.02 + coverage[index]))).Sum())
                .ThenBy(key => key, StringComparer.Ordinal).First();
            remaining.Remove(best); selected.Add(best);
            for (var index = 0; index < distributions.Count; index++) coverage[index] += distributions[index].GetValueOrDefault(best);
        }
        return selected.ToArray();
    }

    private static SelectionProgram GenerateSelectionProgram(string root, string output, SelectionStrategy strategy,
        SelectionProfiles[] profiles, string reference)
    {
        var directory = Path.Combine(output, "candidates", strategy.Id);
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "superopcodes.generated.cpp");
        if (strategy.Mode == "reference")
        {
            File.Copy(Path.GetFullPath(Path.Combine(root, strategy.Source ?? reference)), source, true);
            return new(strategy.Id, "tune-" + strategy.Id, source, TrainingHash(source), strategy.Budget);
        }
        if (strategy.Mode is "sequence" or "sequence-control")
            return GenerateObservedSequenceProgram(root, directory, strategy, profiles, source);
        var distributions = SelectionDistributions(profiles, strategy.Mode);
        var ranked = RankSelectionPairs(distributions, strategy.Budget, strategy.Mode, strategy.CostPower);
        var selected = new List<string>();
        var operations = SelectionOperations(profiles);
        if (strategy.Mode == "union")
        {
            if (strategy.BaseManifest is null) throw new InvalidDataException("Pinned union requires a reviewed base manifest");
            using var pinned = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, strategy.BaseManifest)));
            var pinnedOperations = ReadCandidateOperations(pinned.RootElement);
            MergeTrainingOperations(operations, pinnedOperations);
            foreach (var candidate in pinned.RootElement.GetProperty("candidates").EnumerateArray())
            {
                var pair = ReadCandidatePair(candidate.GetProperty("pair"), pinnedOperations);
                if (pair.Length != 2) throw new InvalidDataException("Pinned union requires operation pairs");
                selected.Add(string.Join('\t', pair));
            }
            if (selected.Distinct(StringComparer.Ordinal).Count() != 256 || strategy.Budget < selected.Count)
                throw new InvalidDataException("Pinned union requires the reviewed 256-pair table and sufficient budget");
        }
        selected.AddRange(ranked.Where(key => !selected.Contains(key, StringComparer.Ordinal)).Take(strategy.Budget - selected.Count));
        var weights = distributions.SelectMany(distribution => distribution.Keys).Distinct(StringComparer.Ordinal)
            .ToDictionary(key => key, key => distributions.Average(distribution => distribution.GetValueOrDefault(key)), StringComparer.Ordinal);
        var raw = SelectionRawPairCounts(profiles);
        var ids = operations.ToDictionary(op => op.Value, op => op.Key, StringComparer.Ordinal);
        var manifest = Path.Combine(directory, "selected.json");
        WriteTraining(manifest, new { schemaVersion = CandidateManifestVersion, operations, metadata = new { strategy.Id, strategy.Mode, strategy.Budget,
                strategy.CostPower, normalization = strategy.Mode.StartsWith("density", StringComparison.Ordinal)
                    ? "positive payload slope times heavy bytes per instrumented command second; equal cell averaging, NOT unit-normalized cells"
                    : "equal dataset/workload; positive heavy-minus-light payload slope unless light/heavy mode",
                profiles = profiles.Select(profile => new { dataset = profile.Suite.Id, heavySuiteHash = profile.Heavy.SuiteHash,
                    lightSuiteHash = profile.Light.SuiteHash }).ToArray() },
            candidates = selected.Select(key => new { pair = key.Split('\t').Select(op => ids[op]).ToArray(),
                weighted_exec_count = raw.GetValueOrDefault(key),
                score = weights.GetValueOrDefault(key), relation_kind = strategy.Mode }).ToArray() });
        File.WriteAllText(source, GenerateSuperopcodes(manifest, strategy.Budget), Utf8NoBom);
        return new(strategy.Id, "tune-" + strategy.Id, source, TrainingHash(source), selected.Count);
    }

    internal static bool TrainingSequenceAllowed(string[] ops) => ops.Length is >= 2 and <= 4 &&
        Enumerable.Range(0, ops.Length - 1).All(index => TrainingPairAllowed(ops[index], ops[index + 1]));

    private static SelectionProgram GenerateObservedSequenceProgram(string root, string directory, SelectionStrategy strategy,
        SelectionProfiles[] profiles, string source)
    {
        if (strategy.BaseManifest is null) throw new InvalidDataException("Sequence search requires a reviewed base manifest");
        var baseManifest = Path.GetFullPath(Path.Combine(root, strategy.BaseManifest));
        using var document = JsonDocument.Parse(File.ReadAllText(baseManifest));
        var baseOperations = ReadCandidateOperations(document.RootElement);
        var basePatterns = document.RootElement.GetProperty("candidates").EnumerateArray().Select(candidate =>
            ReadCandidatePair(candidate.GetProperty("pair"), baseOperations)).ToArray();
        if (basePatterns.Any(ops => ops.Length != 2 || !TrainingSequenceAllowed(ops)) || basePatterns.Length > strategy.Budget)
            throw new InvalidDataException("Invalid reviewed base pairs/budget");
        var distributions = new List<Dictionary<string, double>>();
        var raw = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        foreach (var heavy in profile.Heavy.Samples)
        {
            var light = profile.Light.Samples.Single(sample => sample.Case == heavy.Case);
            if (heavy.Ngrams is null || light.Ngrams is null) throw new InvalidDataException("Sequence search requires new exact 3/4-gram profiles");
            string SequenceName(TrainingSample sample, TrainingNGram entry) =>
                string.Join('\t', entry.Ops.Select(id => ResolveTrainingOperation(sample.Operations, id)));
            foreach (var entry in heavy.Ngrams)
            {
                var key = SequenceName(heavy, entry);
                raw[key] = checked(raw.GetValueOrDefault(key) + entry.Count);
            }
            var large = heavy.Ngrams.Where(entry => entry.Ops.Length == strategy.Width)
                .ToDictionary(entry => SequenceName(heavy, entry), entry => (double)entry.Count, StringComparer.Ordinal);
            var small = light.Ngrams.Where(entry => entry.Ops.Length == strategy.Width)
                .ToDictionary(entry => SequenceName(light, entry), entry => (double)entry.Count, StringComparer.Ordinal);
            var slopes = PositiveTrainingSlope(large, small, heavy.Bytes - light.Bytes);
            var edgeTotal = heavy.Pairs.Sum(pair => (double)pair.Count);
            // Each width-N match removes N-1 dispatches, but matches can overlap.
            // This is an opportunity score, not additive realized savings.
            distributions.Add(ScaleSelectionDistribution(slopes, heavy.Bytes * (strategy.Width - 1.0) / edgeTotal));
        }
        var keys = distributions.SelectMany(distribution => distribution.Keys).Distinct(StringComparer.Ordinal)
            .Where(key => TrainingSequenceAllowed(key.Split('\t'))).ToArray();
        var weights = keys.ToDictionary(key => key, key => distributions.Average(distribution => distribution.GetValueOrDefault(key)), StringComparer.Ordinal);
        var selected = strategy.Mode == "sequence-control" ? Array.Empty<string>() :
            keys.OrderByDescending(key => weights[key]).ThenBy(key => key, StringComparer.Ordinal)
                .Take(strategy.Budget - basePatterns.Length).ToArray();
        if (strategy.Mode == "sequence" && selected.Length == 0)
            throw new InvalidDataException("No observed eligible long sequences; do not report a base-only table as a long-sequence strategy");
        var baseRaw = SelectionRawPairCounts(profiles);
        var operations = SelectionOperations(profiles);
        if (baseOperations is not null) MergeTrainingOperations(operations, baseOperations);
        var ids = operations.ToDictionary(op => op.Value, op => op.Key, StringComparer.Ordinal);
        var patterns = basePatterns.Select(ops => (Ops: ops, Count: baseRaw.GetValueOrDefault(string.Join('\t', ops)), Score: 0.0, Kind: "reviewed-base-pair"))
            .Concat(selected.Select(key => (Ops: key.Split('\t'), Count: raw[key], Score: weights[key], Kind: "observed-exact-ngram"))).ToArray();
        var manifest = Path.Combine(directory, "selected.json");
        WriteTraining(manifest, new { schemaVersion = CandidateManifestVersion, operations, metadata = new { strategy.Id, strategy.Mode, strategy.Width,
                extended_lookup = true, baseManifestHash = TrainingHash(baseManifest), counter = "consecutive-within-block-dispatch-entries-v1",
                ranking = "positive heavy-light ngram slope, normalized by heavy Continue edges, times width-minus-one; not additive realized savings" },
            candidates = patterns.Select(pattern => new { pair = pattern.Ops.Select(op => ids[op]).ToArray(), weighted_exec_count = pattern.Count,
                score = pattern.Score, relation_kind = pattern.Kind }).ToArray() });
        File.WriteAllText(source, GenerateSuperopcodes(manifest, strategy.Budget), Utf8NoBom);
        return new(strategy.Id, "tune-" + strategy.Id, source, TrainingHash(source), patterns.Length);
    }

    private static string[] RankOverlapPairs(IReadOnlyList<Dictionary<string, double>> distributions, string[] keys, int budget)
    {
        // A first-order model, not measured trigrams. Estimate whether the first
        // handler remains free after preceding selected pairs have consumed ops.
        var nodes = distributions.SelectMany(distribution => distribution.Keys).SelectMany(key => key.Split('\t'))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var indices = nodes.Select((node, index) => (node, index)).ToDictionary(pair => pair.node, pair => pair.index, StringComparer.Ordinal);
        var endpoints = keys.Select(key => key.Split('\t')).Select(pair => (First: indices[pair[0]], Second: indices[pair[1]])).ToArray();
        var masses = new List<double[]>();
        var probabilities = new List<double[]>();
        foreach (var distribution in distributions)
        {
            var incoming = new double[nodes.Length]; var outgoing = new double[nodes.Length];
            foreach (var (key, probability) in distribution)
            {
                var pair = key.Split('\t'); outgoing[indices[pair[0]]] += probability; incoming[indices[pair[1]]] += probability;
            }
            masses.Add(incoming.Select((value, index) => Math.Max(value, outgoing[index])).ToArray());
            probabilities.Add(keys.Select(key => distribution.GetValueOrDefault(key)).ToArray());
        }
        var selected = new List<int>();
        var available = Enumerable.Repeat(true, keys.Length).ToArray();
        while (selected.Count < budget)
        {
            var scores = new double[keys.Length];
            for (var cell = 0; cell < distributions.Count; cell++)
            {
                var mass = masses[cell]; var probability = probabilities[cell];
                var free = Enumerable.Repeat(1.0, nodes.Length).ToArray();
                var selectedOutgoing = new double[nodes.Length];
                foreach (var index in selected) selectedOutgoing[endpoints[index].First] += probability[index];
                for (var iteration = 0; iteration < 12; iteration++)
                {
                    var next = Enumerable.Repeat(1.0, nodes.Length).ToArray();
                    foreach (var index in selected)
                    {
                        var edge = endpoints[index];
                        if (mass[edge.Second] > 0) next[edge.Second] -= free[edge.First] * probability[index] / mass[edge.Second];
                    }
                    for (var node = 0; node < nodes.Length; node++) free[node] = (free[node] + Math.Clamp(next[node], 0, 1)) / 2;
                }
                for (var index = 0; index < keys.Length; index++)
                {
                    if (!available[index] || probability[index] == 0) continue;
                    var edge = endpoints[index];
                    var displaced = mass[edge.Second] > 0 ? selectedOutgoing[edge.Second] / mass[edge.Second] : 0;
                    scores[index] += probability[index] * free[edge.First] * Math.Max(0, 1 - displaced);
                }
            }
            var best = -1;
            for (var index = 0; index < keys.Length; index++)
                if (available[index] && (best < 0 || scores[index] > scores[best])) best = index;
            if (best < 0 || scores[best] <= 0) break;
            available[best] = false; selected.Add(best);
        }
        return selected.Select(index => keys[index]).ToArray();
    }

    private static SelectionBenchmark RunSelectionBenchmark(string root, string rootfs, string output, SelectionPlan plan,
        string planHash, string stage, TrainingSuite[] suites, SelectionProgram[] programs, int repeat)
    {
        var directory = Path.Combine(output, stage);
        Directory.CreateDirectory(directory);
        if (File.Exists(Path.Combine(directory, "samples.json"))) throw new InvalidDataException("Existing stage samples; do not overwrite evidence: " + directory);
        var builds = new[] { BuildTrainingVariant(root, "off", null), BuildTrainingVariant(root, "pinned", null) }
            .Concat(programs.Select(program => BuildTrainingVariant(root, program.Flavor, program.Source))).ToArray();
        string Id(TrainingBuild build) => build.Flavor.StartsWith("tune-", StringComparison.Ordinal) ? build.Flavor[5..] : build.Flavor;
        ValidateTrainingRootfs(rootfs);
        foreach (var build in builds) ValidateGnuTools(rootfs, build, directory);
        var rows = new List<SelectionSample>();
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var affinityMask = stage == "validate" ? plan.ValidationAffinityMask : plan.DevelopmentAffinityMask;
        void Measure(TrainingSuite suite, string name, TrainingBuild build, int round, string comparison)
        {
            var id = Id(build);
            var sampleDirectory = Path.Combine(directory, "samples", suite.Id, name,
                (round < 0 ? "warmup-" : $"{round:D2}-") + id + (comparison.Length == 0 ? "" : "-vs-" + comparison));
            var sample = RunTrainingWorkload(suite, name, rootfs, build, sampleDirectory, false, affinityMask);
            var key = suite.Id + "/" + name;
            if (hashes.TryGetValue(key, out var hash) && hash != sample.OutputHash) throw new InvalidDataException("Search variant output mismatch");
            hashes[key] = sample.OutputHash;
            if (round < 0) return;
            rows.Add(new(suite.Id, name, id, round, sample.Seconds, sample.OutputHash,
                sample.BuildFeatures, sample.NativeHash, sample.SetHash, comparison));
            WriteTraining(Path.Combine(directory, "samples.json"), rows);
            Console.WriteLine($"[search/{stage}] {suite.Id}/{name} {id} round={round} comparison={comparison} seconds={sample.Seconds:F4}");
        }
        // Round-major interleaving distributes machine drift over all corpora/cases.
        for (var round = -1; round < repeat; round++)
        foreach (var suite in suites)
        foreach (var name in suite.Cases)
        {
            if (!plan.Pairwise || round < 0)
            {
                for (var position = 0; position < builds.Length; position++)
                    Measure(suite, name, builds[(position + Math.Max(round, 0)) % builds.Length], round, "");
                continue;
            }
            var pinnedBuild = builds.Single(build => build.Flavor == "pinned");
            var comparisons = builds.Where(build => build.Flavor != "pinned").ToArray();
            for (var position = 0; position < comparisons.Length; position++)
            {
                var build = comparisons[(position + round) % comparisons.Length];
                // Alternate AB/BA so each candidate gets a nearby dedicated pinned control.
                foreach (var current in round % 2 == 0 ? new[] { pinnedBuild, build } : new[] { build, pinnedBuild })
                    Measure(suite, name, current, round, Id(build));
            }
        }
        var cases = new List<SelectionCase>();
        var pairedLogs = new Dictionary<string, List<double[]>>(StringComparer.Ordinal);
        foreach (var suite in suites)
        foreach (var name in suite.Cases)
        {
            double[] Times(string variant, string comparison = "") => rows.Where(row => row.Dataset == suite.Id && row.Workload == name &&
                    row.Variant == variant && (!plan.Pairwise || row.Comparison == comparison))
                .OrderBy(row => row.Round).Select(row => row.Seconds).ToArray();
            var off = Times("off", plan.Pairwise ? "off" : "");
            var offPinned = Times("pinned", plan.Pairwise ? "off" : "");
            foreach (var build in builds)
            {
                var id = Id(build);
                var comparison = plan.Pairwise ? id == "pinned" ? "off" : id : "";
                var times = Times(id, comparison);
                var pinned = Times("pinned", comparison);
                if (times.Length != repeat) throw new InvalidDataException("Incomplete search measurements");
                var median = TrainingMedian(times.ToList());
                var logs = times.Select((time, index) => Math.Log(pinned[index] / time)).ToArray();
                if (!pairedLogs.TryGetValue(id, out var list)) pairedLogs[id] = list = [];
                list.Add(logs);
                cases.Add(new(suite.Id, name, id, median, TrainingMedian(times.Select(time => Math.Abs(time - median)).ToList()),
                    TrainingMedian(pinned.ToList()) / median,
                    (TrainingMedian(pinned.ToList()) / median) / (TrainingMedian(offPinned.ToList()) / TrainingMedian(off.ToList())),
                    Math.Exp(TrainingMedian(logs.ToList())), TrainingMedian(pinned.ToList())));
            }
        }
        var metrics = builds.Select(build =>
        {
            var id = Id(build);
            var values = cases.Where(row => row.Variant == id).ToArray();
            var paired = Math.Exp(pairedLogs[id].Average(logs => TrainingMedian(logs.ToList())));
            var interval = SelectionBootstrap(pairedLogs[id]);
            var family = values.GroupBy(row => row.Workload).Select(group => Math.Exp(group.Average(row => Math.Log(row.RatioVsPinned)))).Min();
            var worst = values.Min(row => row.RatioVsPinned);
            return new SelectionMetric(id, Math.Exp(values.Average(row => Math.Log(row.RatioVsPinned))),
                Math.Exp(values.Average(row => Math.Log(row.RatioVsOff))), paired, interval.Lower, interval.Upper, family,
                worst, Math.Log(paired) - 2 * Math.Max(0, plan.MinimumFamilyRatio - family),
                paired >= plan.MinimumSpeedup && interval.Lower > 1 && family >= plan.MinimumFamilyRatio && worst >= plan.MinimumCaseRatio);
        }).ToArray();
        var result = new SelectionBenchmark(planHash, stage, repeat,
            $"guest command stdout-marker interval; paired={plan.Pairwise}; affinityMask={affinityMask}; pre-marker startup/host fixtures excluded; in-interval JIT/guest exec/decode/I/O included",
            programs, builds, cases.ToArray(), metrics);
        WriteTraining(Path.Combine(directory, "benchmark.json"), result);
        var markdown = new StringBuilder("# Selection search: " + stage + "\n\n| Variant | GM/pinned | Paired GM/pinned | Bootstrap 95% | Worst family | Worst case | Accepted |\n|---|---:|---:|---|---:|---:|---|\n");
        foreach (var metric in metrics.OrderByDescending(metric => metric.Score))
            markdown.Append(CultureInfo.InvariantCulture, $"| {metric.Variant} | {metric.GeometricVsPinned:F4} | {metric.PairedVsPinned:F4} | [{metric.BootstrapLower:F4}, {metric.BootstrapUpper:F4}] | {metric.WorstFamilyRatio:F4} | {metric.WorstCaseRatio:F4} | {metric.Accepted} |\n");
        File.WriteAllText(Path.Combine(directory, "benchmark.md"), markdown.ToString(), Utf8NoBom);
        Console.WriteLine(markdown);
        return result;
    }

    private static (double Lower, double Upper) SelectionBootstrap(List<double[]> cases)
    {
        // Conditional within-case paired bootstrap, not a claim about independent machines.
        var random = new Random(730201);
        var estimates = new double[2000];
        for (var iteration = 0; iteration < estimates.Length; iteration++)
            estimates[iteration] = Math.Exp(cases.Average(logs => TrainingMedian(
                Enumerable.Range(0, logs.Length).Select(_ => logs[random.Next(logs.Length)]).ToList())));
        Array.Sort(estimates);
        return (estimates[50], estimates[1949]);
    }
}
