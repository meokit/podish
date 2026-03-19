using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Podish.PerfTools;

internal static partial class Program
{
    private static string GenerateSuperopcodes(string inputPath, int top)
    {
        var data = JsonDocument.Parse(File.ReadAllText(inputPath, Encoding.UTF8)).RootElement;
        var candidates =
            new List<(string Op0, string Op1, long Wec, long Occ, string Relation, string Anchor, string Direction)>();

        if (data.TryGetProperty("candidates", out var candidatesNode) &&
            candidatesNode.ValueKind == JsonValueKind.Array)
        {
            var seen = new HashSet<(string, string)>();
            foreach (var candidate in candidatesNode.EnumerateArray())
            {
                var pair = candidate.TryGetProperty("pair", out var pairNode) &&
                           pairNode.ValueKind == JsonValueKind.Array
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
            sb.AppendLine(
                $"// weighted_exec_count={c.Wec} occurrences={c.Occ} relation={c.Relation} anchor={c.Anchor} direction={c.Direction}");
            sb.AppendLine("ATTR_PRESERVE_NONE int64_t " + handlerName +
                          "(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,");
            sb.AppendLine(
                "                                          mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {");
            sb.AppendLine($"    RUN_SUPEROPCODE_OP({c.Op0}, state, op, instr_limit, utlb, branch, flags_cache);");
            sb.AppendLine();
            sb.AppendLine("    DecodedOp* second_op = NextOp(op);");
            sb.AppendLine(
                $"    RUN_SUPEROPCODE_OP({c.Op1}, state, second_op, instr_limit, utlb, branch, flags_cache);");
            sb.AppendLine();
            sb.AppendLine("    if (auto* next_op = NextOp(second_op)) {");
            sb.AppendLine(
                "        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);");
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
            sb.AppendLine(
                $"    if (ops[0].handler == (HandlerFunc)DispatchWrapper<{c.Op0}> && ops[1].handler == (HandlerFunc)DispatchWrapper<{c.Op1}>) return {handlerName};");
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
    {
        return Regex.Replace(name, "[^A-Za-z0-9_]", "_");
    }
}