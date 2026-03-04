using System.Text.Json.Serialization;
using Podish.Core;

namespace Podish.Core.Native;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PodishRunSpec))]
internal partial class PodishRunSpecJsonContext : JsonSerializerContext
{
}
