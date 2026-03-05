namespace Podish.Core;

internal static class OciStorePath
{
    public const string RelativeStoreDirectory = ".";

    public static string Resolve(string storeDir, string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
            return storedPath;

        var normalized = storedPath.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
            return Path.GetFullPath(normalized);

        return Path.GetFullPath(Path.Combine(storeDir, normalized));
    }

    public static string ToStoredPath(string storeDir, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return fullPath;

        var storeRoot = EnsureTrailingSeparator(Path.GetFullPath(storeDir));
        var absolute = Path.GetFullPath(fullPath);

        if (absolute.StartsWith(storeRoot, StringComparison.Ordinal))
        {
            var relative = Path.GetRelativePath(storeDir, absolute);
            return relative.Replace('\\', '/');
        }

        // Keep absolute path if not under store root (compat edge case).
        return absolute.Replace('\\', '/');
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar))
            return path;
        return path + Path.DirectorySeparatorChar;
    }
}
