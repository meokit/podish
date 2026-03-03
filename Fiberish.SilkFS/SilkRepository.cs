using System.Security.Cryptography;

namespace Fiberish.SilkFS;

public sealed class SilkRepository
{
    public SilkRepository(SilkFsOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Metadata = new SilkMetadataStore(Options.MetadataPath);
    }

    public SilkFsOptions Options { get; }
    public SilkMetadataStore Metadata { get; }

    public void Initialize()
    {
        Directory.CreateDirectory(Options.RootPath);
        Directory.CreateDirectory(Options.ObjectsPath);

        if (!File.Exists(Options.MetadataPath))
        {
            using var _ = File.Create(Options.MetadataPath);
        }

        Metadata.Initialize();
    }

    public string PutObject(ReadOnlySpan<byte> data)
    {
        var hash = SHA256.HashData(data);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        var path = GetObjectPath(hex);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (!File.Exists(path))
            File.WriteAllBytes(path, data.ToArray());
        return hex;
    }

    public byte[]? ReadObject(string objectId)
    {
        if (string.IsNullOrWhiteSpace(objectId)) return null;
        var path = GetObjectPath(objectId);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void DeleteObject(string objectId)
    {
        if (string.IsNullOrWhiteSpace(objectId)) return;
        var path = GetObjectPath(objectId);
        if (!File.Exists(path)) return;
        File.Delete(path);
        var shardDir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(shardDir) && Directory.Exists(shardDir))
        {
            using var iter = Directory.EnumerateFileSystemEntries(shardDir).GetEnumerator();
            if (!iter.MoveNext())
                Directory.Delete(shardDir, false);
        }
    }

    private string GetObjectPath(string objectId)
    {
        var shard = objectId.Length >= 2 ? objectId[..2] : "00";
        return Path.Combine(Options.ObjectsPath, shard, objectId);
    }
}
