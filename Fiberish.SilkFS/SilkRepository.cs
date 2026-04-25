using Microsoft.Win32.SafeHandles;

namespace Fiberish.SilkFS;

public sealed class SilkRepository
{
    public SilkRepository(SilkFsOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Directory.CreateDirectory(Options.RootPath);
        Directory.CreateDirectory(Options.LiveDataPath);
        Metadata = new SilkMetadataStore(Options.MetadataPath);
    }

    public SilkFsOptions Options { get; }
    public SilkMetadataStore Metadata { get; }

    public SilkMetadataSession OpenMetadataSession()
    {
        return Metadata.OpenSession();
    }

    public void Initialize()
    {
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

    public SafeFileHandle OpenLiveInodeHandle(long ino, FileMode mode, FileAccess access)
    {
        return File.OpenHandle(GetLiveInodePath(ino), mode, access, FileShare.ReadWrite | FileShare.Delete);
    }

    public void EnsureLiveInodeDataFile(long ino)
    {
        using var handle = OpenLiveInodeHandle(ino, FileMode.OpenOrCreate, FileAccess.ReadWrite);
    }

    public byte[]? ReadLiveInodeData(long ino)
    {
        var path = GetLiveInodePath(ino);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void WriteLiveInodeData(long ino, ReadOnlySpan<byte> data)
    {
        using var handle = OpenLiveInodeHandle(ino, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        RandomAccess.SetLength(handle, data.Length);
        if (!data.IsEmpty)
            RandomAccess.Write(handle, data, 0);
    }

    public void TruncateLiveInodeData(long ino, long size)
    {
        using var handle = OpenLiveInodeHandle(ino, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        RandomAccess.SetLength(handle, Math.Max(0, size));
    }

    public void DeleteLiveInodeData(long ino)
    {
        var path = GetLiveInodePath(ino);
        if (File.Exists(path))
            File.Delete(path);
    }
}
