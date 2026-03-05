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
            throw new InvalidOperationException($"absolute OCI stored path is not supported: {storedPath}");

        var resolved = Path.GetFullPath(Path.Combine(storeDir, normalized));
        var storeRoot = EnsureTrailingSeparator(Path.GetFullPath(storeDir));
        if (!resolved.StartsWith(storeRoot, StringComparison.Ordinal))
            throw new InvalidOperationException($"OCI stored path escapes store root: {storedPath}");
        return resolved;
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

        throw new InvalidOperationException(
            $"resolved OCI path must stay under store root. store='{storeDir}', path='{fullPath}'");
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar))
            return path;
        return path + Path.DirectorySeparatorChar;
    }
}
