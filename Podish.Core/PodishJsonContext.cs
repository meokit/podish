using System.Text.Json.Serialization;
using Fiberish.VFS;

namespace Podish.Core;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
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
[JsonSerializable(typeof(PodishRunSpec))]
[JsonSerializable(typeof(PodishContainerMetadata))]
[JsonSerializable(typeof(List<PodishContainerMetadata>))]
internal partial class PodishJsonContext : JsonSerializerContext
{
}
