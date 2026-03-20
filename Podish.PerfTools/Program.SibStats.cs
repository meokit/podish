using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Podish.PerfTools;

internal static partial class Program
{
    private static readonly IReadOnlyDictionary<int, string> RegOffsetNames = new Dictionary<int, string>
    {
        [0] = "eax",
        [4] = "ecx",
        [8] = "edx",
        [12] = "ebx",
        [16] = "esp",
        [20] = "ebp",
        [24] = "esi",
        [28] = "edi",
        [32] = "none"
    };

    private static readonly IReadOnlyDictionary<int, string> SegmentNames = new Dictionary<int, string>
    {
        [0] = "default",
        [1] = "es",
        [2] = "cs",
        [3] = "ss",
        [4] = "ds",
        [5] = "fs",
        [6] = "gs"
    };

    private static int RunAnalyzeSibShapes(string[] args)
    {
        var input = RequireValue(args, "--input", 0);
        var output = GetValue(args, "--output") ?? DefaultSibShapeOutput(input);
        var markdownOutput = GetValue(args, "--markdown-output") ?? DefaultSibShapeMarkdownOutput(output);
        var topShapes = GetIntValue(args, "--top-shapes", 20);
        var topOpcodes = GetIntValue(args, "--top-opcodes", 30);
        var filter = new SibShapeFilter(
            NormalizeFilterToken(GetValue(args, "--base")),
            NormalizeFilterToken(GetValue(args, "--index")),
            GetNullableIntValue(args, "--scale"),
            NormalizeFilterToken(GetValue(args, "--segment")),
            NormalizeFilterSet(GetMultiValue(args, "--disp-kind")));

        var result = AnalyzeSibShapes(input, topShapes, topOpcodes, filter);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(markdownOutput))!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, JsonOptions), Utf8NoBom);
        File.WriteAllText(markdownOutput, BuildSibShapeMarkdown(result), Utf8NoBom);
        Console.WriteLine(Path.GetFullPath(output));
        Console.WriteLine(Path.GetFullPath(markdownOutput));
        return 0;
    }

    private static string DefaultSibShapeOutput(string input)
    {
        var full = Path.GetFullPath(input);
        if (Directory.Exists(full))
            return Path.Combine(full, "sib_shape_stats.json");

        var directory = Path.GetDirectoryName(full);
        var stem = Path.GetFileNameWithoutExtension(full);
        return Path.Combine(directory ?? Directory.GetCurrentDirectory(), $"{stem}.sib_shape_stats.json");
    }

    private static string DefaultSibShapeMarkdownOutput(string jsonOutput)
    {
        var full = Path.GetFullPath(jsonOutput);
        var directory = Path.GetDirectoryName(full);
        var stem = Path.GetFileNameWithoutExtension(full);
        return Path.Combine(directory ?? Directory.GetCurrentDirectory(), $"{stem}.md");
    }

    private static Dictionary<string, object?> AnalyzeSibShapes(string inputPath, int topShapes, int topOpcodes,
        SibShapeFilter filter)
    {
        var analysisPath = ResolveBlocksAnalysisPath(inputPath);
        using var doc = JsonDocument.Parse(File.ReadAllText(analysisPath, Encoding.UTF8));
        var root = doc.RootElement;
        if (!root.TryGetProperty("blocks", out var blocksNode) || blocksNode.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"blocks array missing in {analysisPath}");

        var shapeStats = new Dictionary<SibShapeKey, ShapeAggregate>();
        long weightedHasMemOps = 0;
        long weightedSibHasMemOps = 0;
        long weightedSelectedOps = 0;

        foreach (var block in blocksNode.EnumerateArray())
        {
            var execCount = block.TryGetProperty("exec_count", out var execNode) && execNode.TryGetInt64(out var value)
                ? Math.Max(1, value)
                : 1L;

            if (!block.TryGetProperty("ops", out var opsNode) || opsNode.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var op in opsNode.EnumerateArray())
            {
                var meta = op.TryGetProperty("meta", out var metaNode) && metaNode.TryGetInt32(out var metaValue)
                    ? metaValue
                    : 0;
                var hasMem = (meta & 0x2) != 0;
                if (!hasMem)
                    continue;

                weightedHasMemOps += execCount;

                var modrm = op.TryGetProperty("modrm", out var modrmNode) && modrmNode.TryGetInt32(out var modrmValue)
                    ? modrmValue
                    : -1;
                var hasSib = modrm >= 0 && (modrm & 0x7) == 4 && ((modrm >> 6) & 0x3) != 3;
                if (!hasSib)
                    continue;

                weightedSibHasMemOps += execCount;

                var mem = op.TryGetProperty("mem", out var memNode) && memNode.ValueKind == JsonValueKind.Object
                    ? memNode
                    : default;

                var baseOffset = TryGetIntProperty(mem, "base_offset") ?? 32;
                var indexOffset = TryGetIntProperty(mem, "index_offset") ?? 32;
                var scale = TryGetIntProperty(mem, "scale") ?? 0;
                var segment = TryGetIntProperty(mem, "segment") ?? 0;
                var dispRaw = TryGetIntProperty(mem, "disp") ?? 0;
                var shape = new SibShapeKey(
                    RegOffsetToName(baseOffset),
                    RegOffsetToName(indexOffset),
                    indexOffset == 32 ? 0 : scale,
                    SegmentToName(segment),
                    ClassifyDispKind(dispRaw));

                if (!shapeStats.TryGetValue(shape, out var aggregate))
                {
                    aggregate = new ShapeAggregate();
                    shapeStats.Add(shape, aggregate);
                }

                aggregate.Weight += execCount;
                aggregate.SampleCount++;

                var opcodeName = GetOpcodeName(op);
                if (!aggregate.OpcodeWeights.TryGetValue(opcodeName, out var opcodeWeight))
                    opcodeWeight = 0;
                aggregate.OpcodeWeights[opcodeName] = opcodeWeight + execCount;

                if (aggregate.OpcodeExamples.Count < 12 && !aggregate.OpcodeExamples.ContainsKey(opcodeName))
                    aggregate.OpcodeExamples[opcodeName] = new SibOpExample(
                        Signed32(dispRaw),
                        op.TryGetProperty("modrm_hex", out var modrmHexNode) ? modrmHexNode.GetString() : null,
                        op.TryGetProperty("meta_hex", out var metaHexNode) ? metaHexNode.GetString() : null);

                if (filter.Matches(shape))
                    weightedSelectedOps += execCount;
            }
        }

        var orderedShapes = shapeStats.OrderByDescending(entry => entry.Value.Weight).ToList();
        var selectedShapes = orderedShapes.Where(entry => filter.Matches(entry.Key)).ToList();
        var combinedOpcodeWeights = new Dictionary<string, long>(StringComparer.Ordinal);
        var combinedOpcodeExamples = new Dictionary<string, List<object?>>(StringComparer.Ordinal);

        foreach (var (shape, aggregate) in selectedShapes)
        {
            foreach (var (opcode, weight) in aggregate.OpcodeWeights)
            {
                if (!combinedOpcodeWeights.TryGetValue(opcode, out var current))
                    current = 0;
                combinedOpcodeWeights[opcode] = current + weight;

                if (!combinedOpcodeExamples.TryGetValue(opcode, out var examples))
                {
                    examples = new List<object?>();
                    combinedOpcodeExamples[opcode] = examples;
                }

                if (examples.Count < 3 && aggregate.OpcodeExamples.TryGetValue(opcode, out var example))
                    examples.Add(new Dictionary<string, object?>
                    {
                        ["shape"] = SerializeShape(shape),
                        ["disp"] = example.Disp,
                        ["modrm_hex"] = example.ModrmHex,
                        ["meta_hex"] = example.MetaHex
                    });
            }
        }

        return new Dictionary<string, object?>
        {
            ["metadata"] = new Dictionary<string, object?>
            {
                ["input_path"] = analysisPath,
                ["top_shapes_limit"] = topShapes,
                ["top_opcodes_limit"] = topOpcodes,
                ["filter"] = filter.ToDictionary(),
                ["weighted_has_mem_ops"] = weightedHasMemOps,
                ["weighted_sib_has_mem_ops"] = weightedSibHasMemOps,
                ["weighted_selected_sib_ops"] = weightedSelectedOps,
                ["distinct_sib_shapes"] = shapeStats.Count,
                ["selected_shape_count"] = selectedShapes.Count
            },
            ["top_sib_shapes"] = BuildTopShapes(orderedShapes, weightedHasMemOps, weightedSibHasMemOps, topShapes),
            ["selected_shapes"] = BuildSelectedShapes(selectedShapes, weightedHasMemOps, weightedSibHasMemOps, topOpcodes),
            ["combined_top_opcodes"] = BuildCombinedTopOpcodes(
                combinedOpcodeWeights,
                combinedOpcodeExamples,
                weightedSelectedOps,
                topOpcodes)
        };
    }

    private static List<Dictionary<string, object?>> BuildTopShapes(List<KeyValuePair<SibShapeKey, ShapeAggregate>> shapes,
        long weightedHasMemOps, long weightedSibHasMemOps, int topShapes)
    {
        return shapes.Take(topShapes).Select((entry, index) => new Dictionary<string, object?>
        {
            ["rank"] = index + 1,
            ["shape"] = SerializeShape(entry.Key),
            ["weight"] = entry.Value.Weight,
            ["sample_count"] = entry.Value.SampleCount,
            ["pct_of_sib_has_mem"] = Percent(entry.Value.Weight, weightedSibHasMemOps),
            ["pct_of_has_mem"] = Percent(entry.Value.Weight, weightedHasMemOps),
            ["top_opcodes"] = BuildOpcodeRows(entry.Value.OpcodeWeights, entry.Value.OpcodeExamples, entry.Value.Weight, 5)
        }).ToList();
    }

    private static List<Dictionary<string, object?>> BuildSelectedShapes(
        List<KeyValuePair<SibShapeKey, ShapeAggregate>> shapes,
        long weightedHasMemOps,
        long weightedSibHasMemOps,
        int topOpcodes)
    {
        return shapes.Select((entry, index) => new Dictionary<string, object?>
        {
            ["rank"] = index + 1,
            ["shape"] = SerializeShape(entry.Key),
            ["weight"] = entry.Value.Weight,
            ["sample_count"] = entry.Value.SampleCount,
            ["pct_of_sib_has_mem"] = Percent(entry.Value.Weight, weightedSibHasMemOps),
            ["pct_of_has_mem"] = Percent(entry.Value.Weight, weightedHasMemOps),
            ["top_opcodes"] = BuildOpcodeRows(entry.Value.OpcodeWeights, entry.Value.OpcodeExamples, entry.Value.Weight, topOpcodes)
        }).ToList();
    }

    private static List<Dictionary<string, object?>> BuildCombinedTopOpcodes(Dictionary<string, long> combinedOpcodeWeights,
        Dictionary<string, List<object?>> combinedOpcodeExamples, long totalWeight, int topOpcodes)
    {
        return combinedOpcodeWeights
            .OrderByDescending(entry => entry.Value)
            .Take(topOpcodes)
            .Select((entry, index) => new Dictionary<string, object?>
            {
                ["rank"] = index + 1,
                ["opcode"] = entry.Key,
                ["weight"] = entry.Value,
                ["pct_of_selected_sib"] = Percent(entry.Value, totalWeight),
                ["examples"] = combinedOpcodeExamples.TryGetValue(entry.Key, out var examples) ? examples : new List<object?>()
            })
            .ToList();
    }

    private static List<Dictionary<string, object?>> BuildOpcodeRows(Dictionary<string, long> opcodeWeights,
        Dictionary<string, SibOpExample> opcodeExamples, long totalWeight, int topOpcodes)
    {
        return opcodeWeights
            .OrderByDescending(entry => entry.Value)
            .Take(topOpcodes)
            .Select((entry, index) => new Dictionary<string, object?>
            {
                ["rank"] = index + 1,
                ["opcode"] = entry.Key,
                ["weight"] = entry.Value,
                ["pct_of_shape"] = Percent(entry.Value, totalWeight),
                ["example"] = opcodeExamples.TryGetValue(entry.Key, out var example)
                    ? new Dictionary<string, object?>
                    {
                        ["disp"] = example.Disp,
                        ["modrm_hex"] = example.ModrmHex,
                        ["meta_hex"] = example.MetaHex
                    }
                    : null
            })
            .ToList();
    }

    private static Dictionary<string, object?> SerializeShape(SibShapeKey shape)
    {
        return new Dictionary<string, object?>
        {
            ["base"] = shape.Base,
            ["index"] = shape.Index,
            ["scale"] = shape.Scale,
            ["segment"] = shape.Segment,
            ["disp_kind"] = shape.DispKind
        };
    }

    private static string ResolveBlocksAnalysisPath(string inputPath)
    {
        var full = Path.GetFullPath(inputPath);
        if (Directory.Exists(full))
        {
            var direct = Path.Combine(full, "blocks_analysis.json");
            if (File.Exists(direct))
                return direct;
            throw new FileNotFoundException($"blocks_analysis.json not found in directory: {full}");
        }

        if (!File.Exists(full))
            throw new FileNotFoundException($"Analysis file not found: {full}");

        return full;
    }

    private static int? TryGetIntProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var node) ||
            !node.TryGetInt32(out var value))
            return null;
        return value;
    }

    private static string GetOpcodeName(JsonElement op)
    {
        if (op.TryGetProperty("logic_func", out var logicNode))
        {
            var logic = logicNode.GetString();
            if (!string.IsNullOrWhiteSpace(logic))
                return logic!;
        }

        if (op.TryGetProperty("symbol", out var symbolNode))
        {
            var symbol = symbolNode.GetString();
            if (!string.IsNullOrWhiteSpace(symbol))
                return symbol!;
        }

        return "<unknown>";
    }

    private static string RegOffsetToName(int offset)
    {
        return RegOffsetNames.TryGetValue(offset, out var name) ? name : $"ofs_{offset}";
    }

    private static string SegmentToName(int segment)
    {
        return SegmentNames.TryGetValue(segment, out var name) ? name : segment.ToString(CultureInfo.InvariantCulture);
    }

    private static string ClassifyDispKind(int disp)
    {
        var signed = Signed32(disp);
        if (signed == 0)
            return "disp0";
        return signed is >= -128 and <= 127 ? "disp8" : "disp32";
    }

    private static int Signed32(int value)
    {
        return unchecked((int)(uint)value);
    }

    private static double Percent(long value, long total)
    {
        if (total <= 0)
            return 0;
        return Math.Round(value * 100.0 / total, 2);
    }

    private static int? GetNullableIntValue(string[] args, string option)
    {
        var raw = GetValue(args, option);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string? NormalizeFilterToken(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }

    private static HashSet<string>? NormalizeFilterSet(List<string> values)
    {
        if (values.Count == 0)
            return null;
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string BuildSibShapeMarkdown(Dictionary<string, object?> result)
    {
        var lines = new List<string> { "# SIB Shape Analysis", "" };

        if (result.TryGetValue("metadata", out var metadataObj) &&
            metadataObj is Dictionary<string, object?> metadata)
        {
            lines.Add($"- input: `{metadata.GetValueOrDefault("input_path")}`");
            lines.Add($"- weighted has-mem ops: `{metadata.GetValueOrDefault("weighted_has_mem_ops")}`");
            lines.Add($"- weighted SIB has-mem ops: `{metadata.GetValueOrDefault("weighted_sib_has_mem_ops")}`");
            lines.Add($"- weighted selected SIB ops: `{metadata.GetValueOrDefault("weighted_selected_sib_ops")}`");
            lines.Add($"- distinct SIB shapes: `{metadata.GetValueOrDefault("distinct_sib_shapes")}`");
            lines.Add($"- selected shapes: `{metadata.GetValueOrDefault("selected_shape_count")}`");
            if (metadata.TryGetValue("filter", out var filterObj) && filterObj is Dictionary<string, object?> filter)
                lines.Add($"- filter: `{FormatSibFilter(filter)}`");
            lines.Add("");
        }

        if (result.TryGetValue("combined_top_opcodes", out var combinedObj) &&
            combinedObj is List<Dictionary<string, object?>> combined &&
            combined.Count > 0)
        {
            lines.Add("## Combined Top Opcodes");
            lines.Add("");
            lines.Add("| Rank | Opcode | Weight | Selected SIB % |");
            lines.Add("|---:|---|---:|---:|");
            foreach (var row in combined)
                lines.Add(
                    $"| {row.GetValueOrDefault("rank")} | `{row.GetValueOrDefault("opcode")}` | {row.GetValueOrDefault("weight")} | {FormatDouble(row.GetValueOrDefault("pct_of_selected_sib"))} |");
            lines.Add("");
        }

        if (result.TryGetValue("selected_shapes", out var shapesObj) &&
            shapesObj is List<Dictionary<string, object?>> shapes &&
            shapes.Count > 0)
        {
            lines.Add("## Selected Shapes");
            lines.Add("");
            foreach (var shapeRow in shapes)
            {
                var shape = shapeRow.GetValueOrDefault("shape") as Dictionary<string, object?>;
                lines.Add(
                    $"### `{FormatShape(shape)}`");
                lines.Add("");
                lines.Add($"- rank: `{shapeRow.GetValueOrDefault("rank")}`");
                lines.Add($"- weight: `{shapeRow.GetValueOrDefault("weight")}`");
                lines.Add($"- sample count: `{shapeRow.GetValueOrDefault("sample_count")}`");
                lines.Add($"- SIB has-mem %: `{FormatDouble(shapeRow.GetValueOrDefault("pct_of_sib_has_mem"))}`");
                lines.Add($"- has-mem %: `{FormatDouble(shapeRow.GetValueOrDefault("pct_of_has_mem"))}`");
                lines.Add("");

                if (shapeRow.TryGetValue("top_opcodes", out var topOpcodesObj) &&
                    topOpcodesObj is List<Dictionary<string, object?>> topOpcodes &&
                    topOpcodes.Count > 0)
                {
                    lines.Add("| Rank | Opcode | Weight | Shape % | Example |");
                    lines.Add("|---:|---|---:|---:|---|");
                    foreach (var opcodeRow in topOpcodes)
                        lines.Add(
                            $"| {opcodeRow.GetValueOrDefault("rank")} | `{opcodeRow.GetValueOrDefault("opcode")}` | {opcodeRow.GetValueOrDefault("weight")} | {FormatDouble(opcodeRow.GetValueOrDefault("pct_of_shape"))} | {FormatExample(opcodeRow.GetValueOrDefault("example") as Dictionary<string, object?>)} |");
                    lines.Add("");
                }
            }
        }

        if (result.TryGetValue("top_sib_shapes", out var topShapesObj) &&
            topShapesObj is List<Dictionary<string, object?>> topShapes &&
            topShapes.Count > 0)
        {
            lines.Add("## Top SIB Shapes");
            lines.Add("");
            lines.Add("| Rank | Shape | Weight | SIB % | Has-Mem % |");
            lines.Add("|---:|---|---:|---:|---:|");
            foreach (var row in topShapes)
            {
                var shape = row.GetValueOrDefault("shape") as Dictionary<string, object?>;
                lines.Add(
                    $"| {row.GetValueOrDefault("rank")} | `{FormatShape(shape)}` | {row.GetValueOrDefault("weight")} | {FormatDouble(row.GetValueOrDefault("pct_of_sib_has_mem"))} | {FormatDouble(row.GetValueOrDefault("pct_of_has_mem"))} |");
            }
            lines.Add("");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string FormatSibFilter(Dictionary<string, object?> filter)
    {
        var parts = new List<string>();
        foreach (var key in new[] { "base", "index", "scale", "segment" })
            if (filter.TryGetValue(key, out var value) && value is not null)
                parts.Add($"{key}={value}");

        if (filter.TryGetValue("disp_kinds", out var dispKindsObj) && dispKindsObj is string[] dispKinds && dispKinds.Length > 0)
            parts.Add($"disp_kinds=[{string.Join(",", dispKinds)}]");

        return parts.Count == 0 ? "<none>" : string.Join(" ", parts);
    }

    private static string FormatShape(Dictionary<string, object?>? shape)
    {
        if (shape is null)
            return "<unknown>";
        return $"{shape.GetValueOrDefault("base")} + {shape.GetValueOrDefault("index")}*{shape.GetValueOrDefault("scale")} {shape.GetValueOrDefault("segment")} {shape.GetValueOrDefault("disp_kind")}";
    }

    private static string FormatExample(Dictionary<string, object?>? example)
    {
        if (example is null)
            return "";

        var parts = new List<string>();
        if (example.TryGetValue("disp", out var disp) && disp is not null)
            parts.Add($"disp={disp}");
        if (example.TryGetValue("modrm_hex", out var modrm) && modrm is not null)
            parts.Add($"modrm={modrm}");
        if (example.TryGetValue("meta_hex", out var meta) && meta is not null)
            parts.Add($"meta={meta}");
        if (example.TryGetValue("shape", out var shapeObj) && shapeObj is Dictionary<string, object?> shape)
            parts.Add($"shape={FormatShape(shape)}");
        return string.Join(", ", parts);
    }

    private static string FormatDouble(object? value)
    {
        return value switch
        {
            double d => d.ToString("F2", CultureInfo.InvariantCulture),
            float f => f.ToString("F2", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
    }

    private sealed record SibShapeKey(string Base, string Index, int Scale, string Segment, string DispKind);

    private sealed class ShapeAggregate
    {
        public long Weight { get; set; }
        public int SampleCount { get; set; }
        public Dictionary<string, long> OpcodeWeights { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SibOpExample> OpcodeExamples { get; } = new(StringComparer.Ordinal);
    }

    private sealed record SibOpExample(int Disp, string? ModrmHex, string? MetaHex);

    private sealed record SibShapeFilter(
        string? Base,
        string? Index,
        int? Scale,
        string? Segment,
        HashSet<string>? DispKinds)
    {
        public bool Matches(SibShapeKey shape)
        {
            return (Base is null || string.Equals(Base, shape.Base, StringComparison.Ordinal)) &&
                   (Index is null || string.Equals(Index, shape.Index, StringComparison.Ordinal)) &&
                   (Scale is null || Scale.Value == shape.Scale) &&
                   (Segment is null || string.Equals(Segment, shape.Segment, StringComparison.Ordinal)) &&
                   (DispKinds is null || DispKinds.Contains(shape.DispKind));
        }

        public Dictionary<string, object?> ToDictionary()
        {
            return new Dictionary<string, object?>
            {
                ["base"] = Base,
                ["index"] = Index,
                ["scale"] = Scale,
                ["segment"] = Segment,
                ["disp_kinds"] = DispKinds?.OrderBy(value => value, StringComparer.Ordinal).ToArray()
            };
        }
    }
}
