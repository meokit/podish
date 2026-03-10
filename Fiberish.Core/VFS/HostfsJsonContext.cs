using System.Text.Json.Serialization;

namespace Fiberish.VFS;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(HostfsMetaManifest))]
[JsonSerializable(typeof(HostfsPathBinding))]
[JsonSerializable(typeof(HostfsIdentityBinding))]
[JsonSerializable(typeof(HostfsObjectRecord))]
internal partial class HostfsJsonContext : JsonSerializerContext
{
}
