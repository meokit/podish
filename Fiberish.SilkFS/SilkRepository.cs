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
        Directory.CreateDirectory(Options.LiveDataPath);

        if (!File.Exists(Options.MetadataPath))
        {
            using var _ = File.Create(Options.MetadataPath);
        }

        Metadata.Initialize();
    }

    public string GetLiveInodePath(long ino)
    {
        return Path.Combine(Options.LiveDataPath, $"{ino}.bin");
    }

    public byte[]? ReadLiveInodeData(long ino)
    {
        var path = GetLiveInodePath(ino);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void WriteLiveInodeData(long ino, ReadOnlySpan<byte> data)
    {
        var path = GetLiveInodePath(ino);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Options.LiveDataPath);
        using var handle = File.OpenHandle(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        RandomAccess.SetLength(handle, data.Length);
        if (!data.IsEmpty)
            RandomAccess.Write(handle, data, 0);
    }

    public void TruncateLiveInodeData(long ino, long size)
    {
        var path = GetLiveInodePath(ino);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Options.LiveDataPath);
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        fs.SetLength(Math.Max(0, size));
    }

    public void DeleteLiveInodeData(long ino)
    {
        var path = GetLiveInodePath(ino);
        if (File.Exists(path))
            File.Delete(path);
    }
}
