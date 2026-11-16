using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fiberish.X86.Tests.Fex;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FexCatalog(int SchemaVersion, string Repository, string Revision, string License,
    string Adaptation, string NasmVersion, FexCatalogEntry[] Tests);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FexCatalogEntry(string Id, string Source, string SourceSha256, string Manifest,
    string ManifestSha256, string Status, string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FexManifest(int SchemaVersion, string Id, string Revision, string SourceSha256,
    uint LoadAddress, uint EntryPoint, string? Binary, string? BinarySha256, uint[] HaltAddresses,
    ulong InstructionLimit, FexDynamicCode[] DynamicCode, JsonElement Config);

internal sealed record FexDynamicCode(uint Address, string Bytes, uint HaltAddress);

internal static class FexJson
{
    public static JsonSerializerOptions Options => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public const string Revision = "8e95a994a014b0e68fa38b7bf17d84987102e374";
    public const string Repository = "https://github.com/FEX-Emu/FEX";
}
