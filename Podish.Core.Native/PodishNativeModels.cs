using System.Text.Json.Serialization;
using Podish.Core;

namespace Podish.Core.Native;

internal sealed record NativeImageListItem(
    string ImageReference,
    string ManifestDigest,
    int LayerCount,
    string StoreDirectory,
    string? Tag,
    string? Repository);

internal sealed record NativeContainerListItem(
    string Handle,
    string ContainerId,
    string Image,
    string State,
    bool HasTerminal,
    bool Running,
    int? ExitCode);

internal sealed record NativeContainerInspect(
    string Handle,
    string ContainerId,
    string Image,
    string State,
    bool HasTerminal,
    bool Running,
    int? ExitCode,
    PodishRunSpec Spec);

internal sealed record NativeContainerMetadata(
    string ContainerId,
    string Image,
    string State,
    bool HasTerminal,
    bool Running,
    int? ExitCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    PodishRunSpec Spec);

internal sealed record NativeEventsChunk(string Cursor, IReadOnlyList<ContainerEvent> Events);
internal sealed record NativeLogsChunk(string Cursor, IReadOnlyList<ContainerLogEntry> Entries);

internal sealed record NativeJobPollResponse(string Status, string? Error, string? ResultJson);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PodishRunSpec))]
[JsonSerializable(typeof(List<NativeImageListItem>))]
[JsonSerializable(typeof(List<NativeContainerListItem>))]
[JsonSerializable(typeof(NativeContainerInspect))]
[JsonSerializable(typeof(NativeContainerMetadata))]
[JsonSerializable(typeof(List<NativeContainerMetadata>))]
[JsonSerializable(typeof(NativeEventsChunk))]
[JsonSerializable(typeof(NativeLogsChunk))]
[JsonSerializable(typeof(ContainerLogEntry))]
[JsonSerializable(typeof(List<ContainerLogEntry>))]
[JsonSerializable(typeof(NativeJobPollResponse))]
internal partial class PodishNativeJsonContext : JsonSerializerContext
{
}
