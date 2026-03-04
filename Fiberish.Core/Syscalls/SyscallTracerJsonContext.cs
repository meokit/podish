using System.Text.Json.Serialization;

namespace Fiberish.Syscalls;

[JsonSerializable(typeof(string))]
internal partial class SyscallTracerJsonContext : JsonSerializerContext
{
}
