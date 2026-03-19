using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Podish.PerfTools;

internal static partial class Program
{
    private static int RunAnalyzeSuperopcodeCandidates(string[] args)
    {
        var inputs = GetMultiValue(args, "--input");
        if (inputs.Count == 0) inputs = GetPositionalArgs(args);

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
                    ["analysis_file"] = analysisFile,
                    ["reasons"] = skipReasons
                });
                continue;
            }

            if (!data.RootElement.TryGetProperty("blocks", out var blocksNode) ||
                blocksNode.ValueKind != JsonValueKind.Array)
            {
                skippedSamples.Add(new Dictionary<string, object?>
                {
                    ["analysis_file"] = analysisFile,
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
                    ["analysis_file"] = analysisFile,
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
                    .GroupBy(c => Convert.ToString(c["relation_kind"], CultureInfo.InvariantCulture) ?? "",
                        StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)
            },
            ["included_samples"] = includedSamples,
            ["skipped_samples"] = skippedSamples,
            ["anchors"] = anchors.Take(anchorTop).ToList(),
            ["candidates"] = candidates
        };

        File.WriteAllText(outputJson, JsonSerializer.Serialize(output, JsonOptions), Utf8NoBom);
        Console.WriteLine(
            $"Wrote {candidates.Count} candidates from {includedSamples.Count} samples to {Path.GetFullPath(outputJson)}");
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
}