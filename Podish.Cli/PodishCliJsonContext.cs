using System.Text.Json.Serialization;
using Podish.Core;

namespace Podish.Cli;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ContainerLogEntry))]
[JsonSerializable(typeof(ContainerEvent))]
[JsonSerializable(typeof(OciStoredImage))]
[JsonSerializable(typeof(PodishContainerMetadata))]
[JsonSerializable(typeof(List<PodishContainerMetadata>))]
internal partial class PodishCliJsonContext : JsonSerializerContext
{
}
