using System.Text.Json.Serialization;
using Fiberish.Core;

namespace Podish.Core;

public sealed record GuestStatsSummary(
    [property: JsonPropertyName("schema_version")]
    int SchemaVersion,
    [property: JsonPropertyName("exported_at_utc")]
    DateTimeOffset ExportedAtUtc,
    [property: JsonPropertyName("container_id")]
    string ContainerId,
    [property: JsonPropertyName("image")]
    string Image,
    [property: JsonPropertyName("image_base")]
    string ImageBase,
    [property: JsonPropertyName("native_stats")]
    string? NativeStats,
    [property: JsonPropertyName("block_stats")]
    GuestStatsBlockStats BlockStats,
    [property: JsonPropertyName("handler_profile")]
    GuestStatsHandlerProfileEntry[] HandlerProfile,
    [property: JsonPropertyName("files")]
    GuestStatsFiles Files);

public sealed record GuestStatsBlockStats(
    [property: JsonPropertyName("block_count")]
    ulong BlockCount,
    [property: JsonPropertyName("total_block_insts")]
    ulong TotalBlockInsts,
    [property: JsonPropertyName("stop_reason_counts")]
    ulong[] StopReasonCounts,
    [property: JsonPropertyName("inst_histogram")]
    ulong[] InstHistogram,
    [property: JsonPropertyName("block_concat_attempts")]
    ulong BlockConcatAttempts,
    [property: JsonPropertyName("block_concat_success")]
    ulong BlockConcatSuccess,
    [property: JsonPropertyName("block_concat_success_direct_jmp")]
    ulong BlockConcatSuccessDirectJmp,
    [property: JsonPropertyName("block_concat_success_jcc_fallthrough")]
    ulong BlockConcatSuccessJccFallthrough,
    [property: JsonPropertyName("block_concat_reject_not_concat_terminal")]
    ulong BlockConcatRejectNotConcatTerminal,
    [property: JsonPropertyName("block_concat_reject_cross_page")]
    ulong BlockConcatRejectCrossPage,
    [property: JsonPropertyName("block_concat_reject_size_limit")]
    ulong BlockConcatRejectSizeLimit,
    [property: JsonPropertyName("block_concat_reject_loop")]
    ulong BlockConcatRejectLoop,
    [property: JsonPropertyName("block_concat_reject_target_missing")]
    ulong BlockConcatRejectTargetMissing)
{
    public static GuestStatsBlockStats FromSnapshot(BlockStatsSnapshot snapshot)
    {
        return new GuestStatsBlockStats(
            snapshot.BlockCount,
            snapshot.TotalBlockInsts,
            snapshot.StopReasonCounts,
            snapshot.InstHistogram,
            snapshot.BlockConcatAttempts,
            snapshot.BlockConcatSuccess,
            snapshot.BlockConcatSuccessDirectJmp,
            snapshot.BlockConcatSuccessJccFallthrough,
            snapshot.BlockConcatRejectNotConcatTerminal,
            snapshot.BlockConcatRejectCrossPage,
            snapshot.BlockConcatRejectSizeLimit,
            snapshot.BlockConcatRejectLoop,
            snapshot.BlockConcatRejectTargetMissing);
    }
}

public sealed record GuestStatsHandlerProfileEntry(
    [property: JsonPropertyName("handler")]
    string Handler,
    [property: JsonPropertyName("exec_count")]
    ulong ExecCount);

public sealed record GuestStatsFiles(
    [property: JsonPropertyName("blocks")]
    string Blocks);
