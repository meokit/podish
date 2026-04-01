using Fiberish.VFS;

namespace Podish.Core;

public static class OciLayerIndexMerger
{
    public static LayerIndex Merge(IReadOnlyList<IReadOnlyList<LayerIndexEntry>> layers)
    {
        var merged = new Dictionary<string, LayerIndexEntry>(StringComparer.Ordinal)
        {
            ["/"] = new("/", InodeType.Directory, 0x1ED)
        };

        foreach (var layer in layers)
        foreach (var entry in layer)
        {
            var path = NormalizeAbsolutePath(entry.Path);
            if (path == "/")
                continue;

            var parent = ParentPath(path);
            var name = BaseName(path);
            if (name == ".wh..wh..opq")
            {
                RemoveAllChildren(merged, parent);
                continue;
            }

            if (name.StartsWith(".wh.", StringComparison.Ordinal) && name.Length > 4)
            {
                var hiddenName = name[4..];
                var hiddenPath = parent == "/" ? "/" + hiddenName : parent + "/" + hiddenName;
                RemovePathWithDescendants(merged, hiddenPath);
                continue;
            }

            EnsureParents(merged, parent);
            merged[path] = entry with { Path = path };
        }

        var index = new LayerIndex();
        foreach (var entry in merged.Values.OrderBy(static entry => entry.Path, StringComparer.Ordinal))
            index.AddEntry(entry);
        return index;
    }

    private static void RemoveAllChildren(Dictionary<string, LayerIndexEntry> merged, string parent)
    {
        var prefix = parent == "/" ? "/" : parent + "/";
        var toRemove = merged.Keys
            .Where(path => path.StartsWith(prefix, StringComparison.Ordinal) && path != parent)
            .ToList();
        foreach (var path in toRemove)
            merged.Remove(path);
    }

    private static void RemovePathWithDescendants(Dictionary<string, LayerIndexEntry> merged, string path)
    {
        var prefix = path == "/" ? "/" : path + "/";
        var toRemove = merged.Keys
            .Where(existingPath => existingPath == path || existingPath.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        foreach (var existingPath in toRemove)
            merged.Remove(existingPath);
    }

    private static void EnsureParents(Dictionary<string, LayerIndexEntry> merged, string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            if (!merged.ContainsKey("/"))
                merged["/"] = new LayerIndexEntry("/", InodeType.Directory, 0x1ED);
            return;
        }

        if (merged.TryGetValue(path, out var existing))
        {
            if (existing.Type != InodeType.Directory)
                merged[path] = existing with { Type = InodeType.Directory, Mode = 0x1ED, Size = 0 };
            return;
        }

        var parent = ParentPath(path);
        EnsureParents(merged, parent);
        merged[path] = new LayerIndexEntry(path, InodeType.Directory, 0x1ED);
    }

    private static string NormalizeAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        if (normalized.Length > 1 && normalized.EndsWith('/'))
            normalized = normalized.TrimEnd('/');
        return normalized;
    }

    private static string ParentPath(string path)
    {
        if (path == "/")
            return "/";

        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex <= 0 ? "/" : path[..separatorIndex];
    }

    private static string BaseName(string path)
    {
        if (path == "/")
            return "/";

        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex < 0 ? path : path[(separatorIndex + 1)..];
    }
}