namespace Fiberish.SilkFS;

public sealed class SilkFsOptions
{
    public required string RootPath { get; init; }
    public string MetadataFileName { get; init; } = "metadata.sqlite3";
    public string LiveDataDirName { get; init; } = "live";

    public string MetadataPath => Path.Combine(RootPath, MetadataFileName);
    public string LiveDataPath => Path.Combine(RootPath, LiveDataDirName);

    public static SilkFsOptions FromSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("SilkFS source path cannot be empty.", nameof(source));

        return new SilkFsOptions
        {
            RootPath = Path.GetFullPath(source)
        };
    }
}