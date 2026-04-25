using System.Text.Json.Serialization;

namespace Podish.PerfTools;

internal sealed record BlocksAnalysisOutput(
    BlocksAnalysisMetadata metadata,
    BlocksAnalysisValidation validation,
    List<BlockAnalysisBlock>? blocks = null,
    BlocksAnalysisNgrams? ngrams = null,
    BlockCandidateSummary? candidate_summary = null);

internal sealed record BlocksAnalysisMetadata(
    string input_path,
    string? lib_path,
    int symbol_count,
    int n_gram,
    int top_ngrams_limit,
    ulong base_addr,
    int declared_block_count,
    int parsed_block_count,
    int dump_format_version,
    string output_mode,
    bool has_embedded_handler_metadata,
    int embedded_handler_metadata_count,
    string symbol_source);

internal sealed record BlocksAnalysisValidation(string[] warnings);

internal sealed record BlocksAnalysisNgrams(int n, int top, List<BlockAnalysisNgramEntry> entries);

internal sealed record BlockAnalysisNgramEntry(string gram, int count, int block_coverage);

internal readonly record struct BlockAnalysisBlock(
    uint start_eip,
    string start_eip_hex,
    ulong exec_count,
    List<BlockAnalysisOp> ops,
    uint? end_eip = null,
    string? end_eip_hex = null,
    uint? inst_count = null);

internal readonly record struct BlockAnalysisOp(
    uint index,
    string? logic_func,
    string? symbol,
    int? op_id,
    byte prefixes,
    byte modrm,
    string modrm_hex,
    byte meta,
    string meta_hex,
    BlockAnalysisMem mem,
    BlockDefUseInfo def_use,
    uint? next_eip = null,
    string? next_eip_hex = null,
    ulong? handler_ptr = null,
    string? handler_ptr_hex = null,
    ulong? handler_offset = null,
    string? handler_offset_hex = null,
    int? handler_id = null,
    string? handler_symbol = null,
    string? handler_symbol_source = null,
    string? symbol_raw = null,
    string? op_id_hex = null,
    uint? imm = null,
    string? imm_hex = null,
    byte? len = null,
    string? len_hex = null,
    string? prefixes_hex = null)
{
    [JsonIgnore]
    public string? candidate_name { get; init; }

    [JsonIgnore]
    public OpSemanticSummary semantic_summary { get; init; }
}

internal readonly record struct BlockAnalysisMem(
    uint disp,
    uint ea_desc,
    uint base_offset,
    uint index_offset,
    uint scale,
    uint segment,
    string? disp_hex = null,
    string? ea_desc_hex = null,
    string? base_offset_hex = null,
    string? index_offset_hex = null,
    string? scale_hex = null,
    string? segment_hex = null);

internal readonly record struct BlockDefUseInfo(
    string[] reads,
    string[] writes,
    string[] notes,
    bool control_flow,
    bool memory_side_effect);

internal readonly record struct OpSemanticSummary(
    ushort reads_mask,
    ushort writes_mask,
    bool control_flow,
    bool memory_side_effect,
    uint note_flags = 0);

internal sealed record BlockCandidateSummary(
    List<BlockCandidateAnchorSummary> anchors,
    List<BlockCandidatePairSummary> pairs);

internal sealed record BlockCandidateAnchorSummary(
    string anchor,
    long weighted_exec_count,
    long occurrences,
    int unique_block_count,
    List<BlockCandidateExampleBlock> example_blocks,
    OpSemanticsExport? semantics);

internal sealed record BlockCandidatePairSummary(
    string first_handler,
    string second_handler,
    string anchor_handler,
    string direction,
    string relation_kind,
    int relation_priority,
    string[] shared_resources,
    string[] legality_notes,
    long weighted_exec_count,
    long occurrences,
    int unique_block_count,
    Dictionary<string, int> shared_resource_variants,
    Dictionary<string, int> relation_kind_variants,
    List<BlockCandidatePairExample> example_blocks);

internal readonly record struct BlockCandidateExampleBlock(
    string start_eip_hex,
    long exec_count,
    int anchor_op_index);

internal readonly record struct BlockCandidatePairExample(
    string start_eip_hex,
    long exec_count,
    int start_op_index,
    string anchor_handler,
    string direction);

internal sealed record OpSemanticsExport(
    string[] reads,
    string[] writes,
    string[] notes,
    bool control_flow,
    bool memory_side_effect);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(BlocksAnalysisOutput))]
[JsonSerializable(typeof(BlockCandidateSummary))]
internal sealed partial class PerfToolsIndentedJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false)]
[JsonSerializable(typeof(BlocksAnalysisOutput))]
[JsonSerializable(typeof(BlockCandidateSummary))]
internal sealed partial class PerfToolsCompactJsonContext : JsonSerializerContext;

