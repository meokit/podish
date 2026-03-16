using System.Text.Json.Serialization;
using Fiberish.Core.Net;
using Fiberish.Core;
using Fiberish.VFS;

namespace Podish.Core;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ContainerLogEntry))]
[JsonSerializable(typeof(ContainerEvent))]
[JsonSerializable(typeof(OciStoredImage))]
[JsonSerializable(typeof(OciStoredLayer))]
[JsonSerializable(typeof(List<OciStoredLayer>))]
[JsonSerializable(typeof(List<LayerIndexEntry>))]
[JsonSerializable(typeof(OciLayout))]
[JsonSerializable(typeof(OciIndex))]
[JsonSerializable(typeof(OciDescriptor))]
[JsonSerializable(typeof(List<OciDescriptor>))]
[JsonSerializable(typeof(OciManifest))]
[JsonSerializable(typeof(OciImageConfig))]
[JsonSerializable(typeof(PublishedPortSpec))]
[JsonSerializable(typeof(List<PublishedPortSpec>))]
[JsonSerializable(typeof(TransportProtocol))]
[JsonSerializable(typeof(PodishRunSpec))]
[JsonSerializable(typeof(PodishContainerMetadata))]
[JsonSerializable(typeof(List<PodishContainerMetadata>))]
[JsonSerializable(typeof(GuestStatsSummary))]
[JsonSerializable(typeof(GuestStatsBlockStats))]
[JsonSerializable(typeof(GuestStatsHandlerProfileEntry[]))]
[JsonSerializable(typeof(GuestStatsJccProfileEntry[]))]
[JsonSerializable(typeof(GuestStatsFiles))]
[JsonSerializable(typeof(BlockStatsSnapshot))]
internal partial class PodishJsonContext : JsonSerializerContext
{
}
