using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Podish.Wayland;

internal static partial class WaylandThemerc
{
    private const string LatteResource = "Podish.Wayland.Themes.catppuccin-latte.openbox-3.";
    private static readonly WaylandColor Transparent = new(0, 0, 0, 0);

    public static WaylandUiTheme LoadCatppuccinLatte()
    {
        var assembly = typeof(WaylandThemerc).Assembly;
        return ReadTheme(name => assembly.GetManifestResourceStream(LatteResource + name), setDesktopColor: true);
    }

    public static WaylandUiTheme Load(string nameOrPath)
    {
        string file = FindThemeFile(nameOrPath) ??
            throw new FileNotFoundException($"Cannot find a Wayland themerc file for '{nameOrPath}'.", nameOrPath);
        string directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
        return ReadTheme(name => name == "themerc" ? File.OpenRead(file) : OpenOptionalFile(Path.Combine(directory, name)),
            setDesktopColor: false);
    }

    private static string? FindThemeFile(string nameOrPath)
    {
        if (File.Exists(nameOrPath))
            return Path.GetFullPath(nameOrPath);
        if (Directory.Exists(nameOrPath) && FindThemeInDirectory(nameOrPath) is string local)
            return local;
        if (Path.IsPathRooted(nameOrPath) || nameOrPath.Contains('/') || nameOrPath.Contains('\\'))
            return null;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!Path.IsPathFullyQualified(dataHome ?? string.Empty))
            dataHome = Path.Combine(home, ".local", "share");
        string? dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        string[] systemDirs = string.IsNullOrWhiteSpace(dataDirs)
            ? ["/usr/local/share", "/usr/share"]
            : dataDirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        IEnumerable<string> roots = new[] { Path.Combine(dataHome!, "themes"), Path.Combine(home, ".themes") }
            .Concat(systemDirs.Where(Path.IsPathFullyQualified).Select(directory => Path.Combine(directory, "themes")));
        foreach (string root in roots)
        {
            string? file = FindThemeInDirectory(Path.Combine(root, nameOrPath));
            if (file != null)
                return file;
        }

        return null;
    }

    private static string? FindThemeInDirectory(string root)
    {
        string[] files =
        [
            Path.Combine(root, "labwc", "themerc"),
            Path.Combine(root, "openbox-3", "themerc"),
            Path.Combine(root, "themerc")
        ];
        return Array.Find(files, File.Exists);
    }

    private static Stream? OpenOptionalFile(string path) => File.Exists(path) ? File.OpenRead(path) : null;

    private static WaylandUiTheme ReadTheme(Func<string, Stream?> open, bool setDesktopColor)
    {
        using Stream source = open("themerc") ?? throw new InvalidDataException("Missing Wayland themerc resource.");
        using var reader = new StreamReader(source);
        var settings = new ThemeSettings(reader);
        var defaults = WaylandUiTheme.BreezeLight;

        WaylandSurfaceChromeTheme active = ReadChrome(settings, "active", defaults.WindowDecoration.Active);
        WaylandSurfaceChromeTheme inactive = ReadChrome(settings, "inactive", defaults.WindowDecoration.Inactive);
        int horizontalPadding = settings.Integer("window.titlebar.padding.width", settings.Integer("padding.width", 8, 0, 64), 0, 64);
        int verticalPadding = settings.Integer("window.titlebar.padding.height", settings.Integer("padding.height", 7, 0, 32), 0, 32);
        int buttonWidth = settings.Integer("window.button.width", 18, 8, 96);
        int buttonHeight = settings.Integer("window.button.height", buttonWidth, 8, 96);
        int borderWidth = settings.Integer("border.width", 1, 0, 16);
        var decoration = defaults.WindowDecoration with
        {
            Active = active,
            Inactive = inactive,
            Close = ReadButton("close"),
            Maximize = ReadButton("max"),
            Minimize = ReadButton("iconify")
        };
        return defaults with
        {
            Colors = defaults.Colors with
            {
                Border = inactive.Border,
                BorderActive = active.Border,
                TitlebarBackgroundActive = active.Background,
                TitlebarBackgroundInactive = inactive.Background,
                TitlebarForegroundActive = active.Foreground,
                TitlebarForegroundInactive = inactive.Foreground
            },
            Desktop = setDesktopColor
                ? new WaylandDesktopTheme(settings.Color("menu.items.bg.color", active.Background))
                : defaults.Desktop,
            WindowDecoration = decoration,
            Typography = defaults.Typography with
            {
                Alignment = settings.Get("window.label.text.justify")?.ToLowerInvariant() switch
                {
                    "left" => WaylandTitleAlignment.Left,
                    "right" => WaylandTitleAlignment.Right,
                    _ => WaylandTitleAlignment.Center
                }
            },
            Spacing = defaults.Spacing with
            {
                WindowBorderThickness = borderWidth,
                TitlebarHeight = Math.Max(buttonHeight, defaults.Typography.TitleFontSize + 2 * verticalPadding),
                TitlebarHorizontalPadding = horizontalPadding,
                ButtonInset = horizontalPadding,
                ButtonSize = buttonWidth,
                ButtonHeight = buttonHeight,
                ButtonGap = settings.Integer("window.button.spacing", 4, 0, 32),
                IconStrokeWidth = 1
            }
        };

        WaylandTitlebarButtonTheme ReadButton(string name)
        {
            WaylandButtonBitmap? image = ReadBitmap(open(name + ".xbm"));
            WaylandButtonBitmap? hover = ReadBitmap(open(name + "_hover.xbm")) ?? image;
            WaylandButtonBitmap? toggled = ReadBitmap(open(name + "_toggled.xbm")) ?? image;
            WaylandButtonBitmap? toggledHover = ReadBitmap(open(name + "_toggled_hover.xbm")) ??
                ReadBitmap(open(name + "_hover_toggled.xbm")) ?? toggled;
            return new WaylandTitlebarButtonTheme(
                ReadAppearances(settings, "active", name, active.Foreground, image, hover),
                ReadAppearances(settings, "inactive", name, inactive.Foreground, image, hover),
                ReadAppearances(settings, "active", name, active.Foreground, toggled, toggledHover, toggled: true),
                ReadAppearances(settings, "inactive", name, inactive.Foreground, toggled, toggledHover, toggled: true));
        }
    }

    private static WaylandSurfaceChromeTheme ReadChrome(ThemeSettings settings, string focus, WaylandSurfaceChromeTheme defaults)
    {
        string window = $"window.{focus}";
        var border = settings.Color(window + ".border.color", settings.Color("border.color", defaults.Border));
        return new WaylandSurfaceChromeTheme(
            settings.Color(window + ".title.bg.color", defaults.Background),
            settings.Color(window + ".label.text.color", defaults.Foreground),
            border,
            border);
    }

    private static WaylandButtonAppearances ReadAppearances(ThemeSettings settings, string focus, string name,
        WaylandColor fallbackForeground, WaylandButtonBitmap? bitmap, WaylandButtonBitmap? hover, bool toggled = false)
    {
        string button = $"window.{focus}.button";
        var unpressed = ReadAppearance("unpressed", fallbackForeground, bitmap);
        return new WaylandButtonAppearances(unpressed,
            ReadAppearance("hover", unpressed.Foreground, hover),
            ReadAppearance("pressed", unpressed.Foreground, bitmap));

        WaylandButtonAppearance ReadAppearance(string state, WaylandColor foreground, WaylandButtonBitmap? image)
        {
            string? Get(string suffix) =>
                (toggled ? settings.Get($"{button}.{name}.toggled.{state}.{suffix}") ??
                    settings.Get($"{button}.toggled.{state}.{suffix}") ??
                    settings.Get($"{button}.{name}.toggled.{suffix}") ??
                    settings.Get($"{button}.toggled.{suffix}") : null) ??
                settings.Get($"{button}.{name}.{state}.{suffix}") ?? settings.Get($"{button}.{state}.{suffix}");

            string? backgroundStyle = Get("bg");
            WaylandColor background = backgroundStyle?.Contains("parentrelative", StringComparison.OrdinalIgnoreCase) == true
                ? Transparent
                : ParseColor(Get("bg.color")) ?? Transparent;
            if (state == "hover" && background.A == 0 && hover == bitmap)
                background = settings.Color("window.button.hover.bg.color", Transparent);
            return new WaylandButtonAppearance(ParseColor(Get("image.color")) ?? foreground, background, image);
        }
    }

    private static WaylandColor? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
        {
            string[] components = value[4..].Split('/');
            if (components.Length == 3 && TryParseRgbChannel(components[0], out byte r) &&
                TryParseRgbChannel(components[1], out byte g) && TryParseRgbChannel(components[2], out byte b))
                return new WaylandColor(r, g, b);
            return null;
        }
        if (value[0] != '#')
        {
            string name = value.Replace(" ", string.Empty, StringComparison.Ordinal);
            var named = Color.FromName(name.Replace("grey", "gray", StringComparison.OrdinalIgnoreCase));
            if (named.IsKnownColor)
                return new WaylandColor(named.R, named.G, named.B, named.A);
            if (name.Length > 4 &&
                (name.StartsWith("grey", StringComparison.OrdinalIgnoreCase) || name.StartsWith("gray", StringComparison.OrdinalIgnoreCase)) &&
                int.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int level) &&
                level <= 100)
            {
                byte gray = (byte)((level * 255 + 50) / 100);
                return new WaylandColor(gray, gray, gray);
            }
            return null;
        }

        string[] parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string hex = parts[0][1..];
        if (!uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint number))
            return null;
        byte alpha = 255;
        if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int opacity))
            alpha = (byte)((Math.Min(opacity, 100) * 255 + 50) / 100);
        return hex.Length switch
        {
            3 => new WaylandColor((byte)((number >> 8) * 17), (byte)(((number >> 4) & 15) * 17), (byte)((number & 15) * 17), alpha),
            6 => new WaylandColor((byte)(number >> 16), (byte)(number >> 8), (byte)number, alpha),
            8 => new WaylandColor((byte)(number >> 24), (byte)(number >> 16), (byte)(number >> 8), (byte)number),
            _ => null
        };
    }

    private static bool TryParseRgbChannel(string text, out byte value)
    {
        value = 0;
        if (text.Length is < 1 or > 4 ||
            !uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint component))
            return false;
        uint max = (1u << (text.Length * 4)) - 1;
        value = (byte)((component * 255 + max / 2) / max);
        return true;
    }

    private static WaylandButtonBitmap? ReadBitmap(Stream? source)
    {
        if (source == null)
            return null;
        using var reader = new StreamReader(source);
        string file = reader.ReadToEnd();
        var dimensions = XbmDimension().Matches(file);
        int width = 0, height = 0;
        foreach (Match dimension in dimensions)
        {
            if (!int.TryParse(dimension.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                return null;
            if (dimension.Groups[1].ValueSpan.SequenceEqual("width"))
                width = value;
            else
                height = value;
        }
        int start = file.IndexOf('{');
        int end = file.LastIndexOf('}');
        if (width is < 1 or > 128 || height is < 1 or > 128 || start < 0 || end <= start)
            return null;

        var data = XbmByte().Matches(file[(start + 1)..end]);
        int length = ((width + 7) / 8) * height;
        if (data.Count != length)
            return null;
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
            bytes[i] = byte.Parse(data[i].Groups[1].ValueSpan, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return new WaylandButtonBitmap(width, height, bytes);
    }

    [GeneratedRegex(@"(?m)^\s*#define\s+\w+_(width|height)\s+(\d+)\b")]
    private static partial Regex XbmDimension();

    [GeneratedRegex(@"\b0[xX]([0-9a-fA-F]{1,2})\b")]
    private static partial Regex XbmByte();

    private sealed class ThemeSettings
    {
        private readonly Dictionary<string, string> _exact = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(Regex Pattern, int Specificity, string Value)> _patterns = [];

        public ThemeSettings(TextReader reader)
        {
            while (reader.ReadLine() is string line)
            {
                ReadOnlySpan<char> trimmed = line.AsSpan().Trim();
                if (trimmed.IsEmpty || trimmed[0] is '!' or '#')
                    continue;
                int separator = trimmed.IndexOf(':');
                if (separator < 1)
                    continue;
                string key = trimmed[..separator].Trim().ToString();
                string value = trimmed[(separator + 1)..].Trim().ToString();
                if (key.Contains('*'))
                {
                    string pattern = Regex.Escape(key).Replace(@"\*", ".*", StringComparison.Ordinal);
                    _patterns.Add((new Regex("^" + pattern + "$", RegexOptions.IgnoreCase |
                        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking),
                        key.Count(character => character != '*'), value));
                }
                else
                    _exact[key] = value;
            }
        }

        public string? Get(string key)
        {
            if (_exact.TryGetValue(key, out string? value))
                return value;
            int best = -1;
            foreach ((Regex pattern, int specificity, string candidate) in _patterns)
            {
                if (specificity < best || !pattern.IsMatch(key))
                    continue;
                best = specificity;
                value = candidate;
            }
            return value;
        }

        public int Integer(string key, int fallback, int min, int max) =>
            int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? Math.Clamp(value, min, max)
                : fallback;

        public WaylandColor Color(string key, WaylandColor fallback) => ParseColor(Get(key)) ?? fallback;
    }
}
