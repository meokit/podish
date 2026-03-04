using System.Text.Json.Serialization;

namespace Fiberish.VFS;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(HostfsMetaRecord))]
internal partial class HostfsJsonContext : JsonSerializerContext
{
}
