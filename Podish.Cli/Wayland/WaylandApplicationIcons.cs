using System.Text;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Wayland;

namespace Podish.Cli.Wayland;

/// <summary>Looks up the guest's installed application metadata and icon assets.</summary>
internal sealed class WaylandApplicationIcons(SyscallManager syscalls, ILogger logger, int targetPixels)
{
    private static readonly string[] DataDirectories = ["/usr/local/share", "/usr/share"];
    private static readonly int[] IconSizes = [16, 22, 24, 32, 48, 64, 128, 256];
    private readonly int[] _iconSizes = IconSizes.OrderBy(size => size < targetPixels)
        .ThenBy(size => Math.Abs(size - targetPixels)).ToArray();
    private const int MaxDesktopBytes = 64 * 1024, MaxIconBytes = 512 * 1024;

    internal WaylandApplicationIcon? Resolve(string appId)
    {
        if (!IsName(appId))
            return null;

        try
        {
            string name = appId.EndsWith(".desktop", StringComparison.Ordinal) ? appId[..^8] : appId;
            string shortName = name[(name.LastIndexOf('.') + 1)..];
            string? icon = ReadDesktopIcon(name);
            if (icon == null && shortName != name)
                icon = ReadDesktopIcon(shortName);

            var result = icon == null ? null : FindIcon(icon);
            result ??= FindIcon(name);
            if (result == null && shortName != name)
                result = FindIcon(shortName);
            logger.LogDebug("Wayland application icon app_id={AppId} icon={Icon} loaded={Loaded}",
                appId, icon ?? name, result != null);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not read the guest icon for {AppId}", appId);
            return null;
        }
    }

    private string? ReadDesktopIcon(string name)
    {
        foreach (string directory in DataDirectories)
        {
            var bytes = ReadFile($"{directory}/applications/{name}.desktop", MaxDesktopBytes);
            if (bytes == null)
                continue;

            bool desktopEntry = false;
            using var text = new StringReader(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            while (text.ReadLine() is { } line)
            {
                ReadOnlySpan<char> value = line.AsSpan().Trim();
                if (value.StartsWith("["))
                {
                    if (desktopEntry)
                        break;
                    desktopEntry = value.SequenceEqual("[Desktop Entry]");
                }

                if (desktopEntry && value.StartsWith("Icon=", StringComparison.Ordinal))
                    return value[5..].Trim().ToString();
            }
        }
        return null;
    }

    private WaylandApplicationIcon? FindIcon(string icon)
    {
        if (icon.StartsWith('/'))
            return ReadIcon(icon);
        if (!IsName(icon))
            return null;

        bool hasExtension = icon.EndsWith(".png", StringComparison.Ordinal) || icon.EndsWith(".svg", StringComparison.Ordinal);
        foreach (string data in DataDirectories)
        foreach (string directory in IconDirectories(data))
        {
            var result = hasExtension ? ReadIcon($"{directory}/{icon}") :
                ReadIcon($"{directory}/{icon}.png") ?? ReadIcon($"{directory}/{icon}.svg");
            if (result != null)
                return result;
        }

        return null;
    }

    private IEnumerable<string> IconDirectories(string data)
    {
        foreach (int size in _iconSizes.Where(size => size >= targetPixels))
            yield return $"{data}/icons/hicolor/{size}x{size}/apps";
        yield return $"{data}/icons/hicolor/scalable/apps";
        foreach (int size in _iconSizes.Where(size => size < targetPixels))
            yield return $"{data}/icons/hicolor/{size}x{size}/apps";
        yield return $"{data}/pixmaps";
        yield return $"{data}/icons";

        foreach (int size in _iconSizes)
        {
            yield return $"{data}/icons/breeze/apps/{size}";
            yield return $"{data}/icons/Adwaita/{size}x{size}/apps";
        }
        yield return $"{data}/icons/Adwaita/scalable/apps";
    }

    private WaylandApplicationIcon? ReadIcon(string path)
    {
        bool svg = path.EndsWith(".svg", StringComparison.Ordinal);
        if (!svg && !path.EndsWith(".png", StringComparison.Ordinal))
            return null;

        var bytes = ReadFile(path, MaxIconBytes);
        return bytes == null ? null : new WaylandApplicationIcon(bytes, svg);
    }

    private byte[]? ReadFile(string path, int limit)
    {
        if (path.Length > 4096 || path.Contains('\0'))
            return null;

        var location = syscalls.PathWalk(path);
        var inode = location.Dentry?.Inode;
        if (inode == null || inode.Type != InodeType.File || inode.Size == 0 || inode.Size > (ulong)limit)
            return null;

        using var file = new LinuxFile(location, FileFlags.O_RDONLY);
        var bytes = new byte[(int)inode.Size];
        var segments = inode.GetReadableSegments(file, 0, bytes.Length);
        int offset = 0;
        foreach (var segment in segments)
        {
            segment.CopyTo(bytes.AsSpan(offset));
            offset += segment.Length;
        }
        return segments.Succeeded && offset == bytes.Length ? bytes : null;
    }

    private static bool IsName(string value) =>
        value.Length is > 0 and <= 256 && value is not "." and not ".." &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
