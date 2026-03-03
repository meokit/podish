namespace Fiberish.VFS;

public enum FsContextState
{
    Open = 0,
    Configuring = 1,
    Created = 2
}

/// <summary>
///     File descriptor returned by fsopen(2), holding filesystem context until fsmount(2).
/// </summary>
public sealed class FsContextFile : LinuxFile
{
    public FsContextFile(Dentry dentry, Mount mount, string fsType, FileFlags flags = FileFlags.O_RDONLY)
        : base(dentry, flags, mount)
    {
        FsType = fsType;
    }

    public string FsType { get; }
    public string? Source { get; set; }
    public Dictionary<string, string?> StringOptions { get; } = new(StringComparer.Ordinal);
    public HashSet<string> FlagOptions { get; } = new(StringComparer.Ordinal);
    public FsContextState State { get; set; } = FsContextState.Open;

    public void SetString(string key, string? value)
    {
        StringOptions[key] = value;
        if (string.Equals(key, "source", StringComparison.Ordinal)) Source = value;
        if (State == FsContextState.Open) State = FsContextState.Configuring;
    }

    public void SetFlag(string key)
    {
        FlagOptions.Add(key);
        if (State == FsContextState.Open) State = FsContextState.Configuring;
    }

    public string? BuildMountDataString()
    {
        var parts = new List<string>();
        foreach (var kv in StringOptions)
        {
            if (string.Equals(kv.Key, "source", StringComparison.Ordinal)) continue;
            if (kv.Value == null) parts.Add(kv.Key);
            else parts.Add($"{kv.Key}={kv.Value}");
        }

        foreach (var flag in FlagOptions)
        {
            // flags are also represented in mount options for compatibility with existing parsers
            if (string.Equals(flag, "ro", StringComparison.Ordinal) ||
                string.Equals(flag, "rw", StringComparison.Ordinal))
                parts.Add(flag);
            else
                parts.Add(flag);
        }

        return parts.Count == 0 ? null : string.Join(",", parts);
    }
}
