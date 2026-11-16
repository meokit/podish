using System.Security.Cryptography;
using System.Text.Json;

namespace Fiberish.X86.Tests.Fex;

internal static class FexAssets
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Assets", "Fex");

    public static FexCatalog ReadCatalog()
    {
        var catalog = ReadJson<FexCatalog>("catalog.json");
        Require(catalog.SchemaVersion == 1 && catalog.Repository == FexJson.Repository &&
            catalog.Revision == FexJson.Revision && catalog.License == "MIT", "Unexpected FEX catalog provenance");
        Require(catalog.Tests.Length == 260 && catalog.Tests.Select(test => test.Id).Distinct(StringComparer.Ordinal).Count() == 260,
            "The pinned FEX inventory must contain exactly 260 unique sources");
        foreach (var entry in catalog.Tests)
            Require(entry.Status is "ready" or "blocked" && (entry.Status == "blocked") == !string.IsNullOrWhiteSpace(entry.Reason),
                $"Missing/unknown catalog disposition: {entry.Id}");
        return catalog;
    }

    public static FexManifest ReadManifest(FexCatalogEntry entry)
    {
        var bytes = ReadVerified(entry.Manifest, entry.ManifestSha256);
        var manifest = JsonSerializer.Deserialize<FexManifest>(bytes, FexJson.Options)
            ?? throw new InvalidDataException($"Empty manifest: {entry.Id}");
        Require(manifest.SchemaVersion == 1 && manifest.Id == entry.Id && manifest.Revision == FexJson.Revision &&
            manifest.SourceSha256 == entry.SourceSha256 && manifest.LoadAddress == 0x10000 && manifest.EntryPoint == 0x10000 &&
            manifest.InstructionLimit is > 0 and <= 1_000_000, $"Invalid manifest/provenance: {entry.Id}");
        return manifest;
    }

    public static byte[] ReadBinary(FexManifest manifest)
    {
        Require(manifest.Binary != null && manifest.BinarySha256 != null, $"Missing binary: {manifest.Id}");
        var bytes = ReadVerified(manifest.Binary!, manifest.BinarySha256!);
        Require(bytes.Length != 0 && (ulong)manifest.LoadAddress + (ulong)bytes.Length <= 0x1_0000_0000UL,
            $"Invalid binary address range: {manifest.Id}");
        foreach (var halt in manifest.HaltAddresses)
            Require(halt >= manifest.LoadAddress && (ulong)halt - manifest.LoadAddress < (ulong)bytes.Length &&
                bytes[halt - manifest.LoadAddress] == 0xF4, $"Invalid NASM-listed HLT address: {manifest.Id}");
        foreach (var image in manifest.DynamicCode)
        {
            var declaredBytes = Convert.FromHexString(image.Bytes);
            Require(manifest.Id is "Primary/Primary_E8" or "Primary/Primary_E9" &&
                image.Address == 0xE0000000 && image.HaltAddress == 0xE0000005 &&
                image.Bytes == "B844434241F4" && declaredBytes.Length == 6,
                $"Unknown dynamic-code termination contract: {manifest.Id}");
        }
        return bytes;
    }

    public static IEnumerable<object[]> Cases(int shard)
    {
        Require(shard is >= 0 and < 8, "Invalid FEX shard");
        // Strings are framework-serializable theory data. Discovery owns no CPU or shared fixture/cache.
        return ReadCatalog().Tests.Where(test => test.Status == "ready").OrderBy(test => test.Id, StringComparer.Ordinal)
            .Where((_, index) => index % 8 == shard).Select(test => new object[] { test.Id }).ToArray();
    }

    private static byte[] ReadVerified(string path, string expectedHash)
    {
        var bytes = File.ReadAllBytes(AssetPath(path));
        Require(Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase),
            $"FEX asset SHA256 mismatch: {path}");
        return bytes;
    }

    private static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(AssetPath(path)), FexJson.Options)
        ?? throw new InvalidDataException($"Empty FEX JSON: {path}");

    private static string AssetPath(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        Require(full.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            $"FEX asset path escapes asset root: {relative}");
        return full;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
