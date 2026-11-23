namespace Podish.Wayland;

public readonly record struct WaylandColor(byte R, byte G, byte B, byte A = 255);

public readonly record struct WaylandColorTheme(
    WaylandColor Background,
    WaylandColor Surface,
    WaylandColor SurfaceVariant,
    WaylandColor Border,
    WaylandColor BorderActive,
    WaylandColor TitlebarBackgroundActive,
    WaylandColor TitlebarBackgroundInactive,
    WaylandColor TitlebarForegroundActive,
    WaylandColor TitlebarForegroundInactive,
    WaylandColor Accent,
    WaylandColor AccentMuted,
    WaylandColor Danger,
    WaylandColor Success,
    WaylandColor Warning,
    WaylandColor Shadow,
    WaylandColor Scrim);

public readonly record struct WaylandButtonTheme(
    WaylandColor Foreground,
    WaylandColor BackgroundClose,
    WaylandColor BackgroundMaximize,
    WaylandColor BackgroundMinimize,
    WaylandColor BackgroundHover,
    WaylandColor BackgroundPressed,
    int CornerRadius);

public readonly record struct WaylandSurfaceChromeTheme(
    WaylandColor Background,
    WaylandColor Foreground,
    WaylandColor Border,
    WaylandColor BorderActive);

public readonly record struct WaylandDesktopTheme(
    WaylandColor Background);

public readonly record struct WaylandTypographyTheme(
    int TitleFontSize,
    int TitleFontWeight)
{
    public WaylandTitleAlignment Alignment { get; init; }
}

public enum WaylandTitleAlignment
{
    Left,
    Center,
    Right
}

public readonly record struct WaylandSpacingTheme(
    int WindowBorderThickness,
    int TitlebarHeight,
    int TitlebarHorizontalPadding,
    int ButtonSize,
    int ButtonGap,
    int ButtonInset,
    int ResizeGripThickness,
    int IconStrokeWidth)
{
    public int? ButtonHeight { get; init; }
}

public readonly record struct WaylandShadowTheme(
    int BlurRadius,
    int OffsetX,
    int OffsetY,
    byte Opacity);

public readonly record struct WaylandCornerTheme(
    int WindowCornerRadius,
    int ButtonCornerRadius);

public readonly record struct WaylandWindowDecorationTheme(
    WaylandSurfaceChromeTheme Active,
    WaylandSurfaceChromeTheme Inactive,
    WaylandButtonTheme Buttons)
{
    public WaylandTitlebarButtonTheme? Close { get; init; }
    public WaylandTitlebarButtonTheme? Maximize { get; init; }
    public WaylandTitlebarButtonTheme? Minimize { get; init; }

    public WaylandTitlebarButtonTheme? GetButton(WaylandSceneHitKind kind) => kind switch
    {
        WaylandSceneHitKind.CloseButton => Close,
        WaylandSceneHitKind.MaximizeButton => Maximize,
        WaylandSceneHitKind.MinimizeButton => Minimize,
        _ => null
    };
}

public sealed record WaylandButtonBitmap(int Width, int Height, ReadOnlyMemory<byte> Bits)
{
    public bool IsSet(int x, int y) => (Bits.Span[y * ((Width + 7) / 8) + x / 8] & (1 << (x % 8))) != 0;
}

public readonly record struct WaylandButtonAppearance(
    WaylandColor Foreground, WaylandColor Background, WaylandButtonBitmap? Bitmap);

public readonly record struct WaylandButtonAppearances(
    WaylandButtonAppearance Unpressed, WaylandButtonAppearance Hover, WaylandButtonAppearance Pressed);

public sealed record WaylandTitlebarButtonTheme(
    WaylandButtonAppearances Active,
    WaylandButtonAppearances Inactive,
    WaylandButtonAppearances ActiveToggled,
    WaylandButtonAppearances InactiveToggled)
{
    public WaylandButtonAppearance GetAppearance(bool active, bool toggled, bool hover, bool pressed)
    {
        var states = toggled ? (active ? ActiveToggled : InactiveToggled) : (active ? Active : Inactive);
        return pressed ? states.Pressed : hover ? states.Hover : states.Unpressed;
    }
}

public readonly record struct WaylandUiTheme(
    WaylandColorTheme Colors,
    WaylandDesktopTheme Desktop,
    WaylandWindowDecorationTheme WindowDecoration,
    WaylandTypographyTheme Typography,
    WaylandSpacingTheme Spacing,
    WaylandShadowTheme Shadow,
    WaylandCornerTheme Corners)
{
    private static readonly Lazy<WaylandUiTheme> Latte = new(WaylandThemerc.LoadCatppuccinLatte);

    public static WaylandUiTheme CatppuccinLatte => Latte.Value;

    public static WaylandUiTheme Load(string? theme) => theme?.Trim().ToLowerInvariant() switch
    {
        null or "" or "catppuccin-latte" => CatppuccinLatte,
        "breeze-light" => BreezeLight,
        _ => WaylandThemerc.Load(theme!.Trim())
    };

    public static WaylandUiTheme BreezeLight => new(
        Colors: new WaylandColorTheme(
            Background: new WaylandColor(236, 239, 244),
            Surface: new WaylandColor(252, 252, 253),
            SurfaceVariant: new WaylandColor(227, 232, 240),
            Border: new WaylandColor(178, 186, 196),
            BorderActive: new WaylandColor(81, 112, 164),
            TitlebarBackgroundActive: new WaylandColor(235, 240, 247),
            TitlebarBackgroundInactive: new WaylandColor(240, 242, 246),
            TitlebarForegroundActive: new WaylandColor(34, 44, 57),
            TitlebarForegroundInactive: new WaylandColor(78, 86, 97),
            Accent: new WaylandColor(61, 111, 186),
            AccentMuted: new WaylandColor(131, 156, 194),
            Danger: new WaylandColor(208, 72, 72),
            Success: new WaylandColor(85, 156, 84),
            Warning: new WaylandColor(212, 157, 66),
            Shadow: new WaylandColor(22, 24, 28, 255),
            Scrim: new WaylandColor(12, 14, 18, 176)),
        Desktop: new WaylandDesktopTheme(
            Background: new WaylandColor(142, 158, 182)),
        WindowDecoration: new WaylandWindowDecorationTheme(
            Active: new WaylandSurfaceChromeTheme(
                Background: new WaylandColor(235, 240, 247),
                Foreground: new WaylandColor(34, 44, 57),
                Border: new WaylandColor(154, 166, 182),
                BorderActive: new WaylandColor(81, 112, 164)),
            Inactive: new WaylandSurfaceChromeTheme(
                Background: new WaylandColor(240, 242, 246),
                Foreground: new WaylandColor(78, 86, 97),
                Border: new WaylandColor(188, 195, 203),
                BorderActive: new WaylandColor(188, 195, 203)),
            Buttons: new WaylandButtonTheme(
                Foreground: new WaylandColor(248, 249, 251),
                BackgroundClose: new WaylandColor(208, 72, 72),
                BackgroundMaximize: new WaylandColor(111, 141, 109),
                BackgroundMinimize: new WaylandColor(183, 149, 88),
                BackgroundHover: new WaylandColor(96, 120, 155, 224),
                BackgroundPressed: new WaylandColor(72, 95, 129, 224),
                CornerRadius: 8)),
        Typography: new WaylandTypographyTheme(
            TitleFontSize: 13,
            TitleFontWeight: 500),
        Spacing: new WaylandSpacingTheme(
            WindowBorderThickness: 2,
            TitlebarHeight: 34,
            TitlebarHorizontalPadding: 12,
            ButtonSize: 16,
            ButtonGap: 6,
            ButtonInset: 9,
            ResizeGripThickness: 6,
            IconStrokeWidth: 2),
        Shadow: new WaylandShadowTheme(
            BlurRadius: 20,
            OffsetX: 0,
            OffsetY: 8,
            Opacity: 72),
        Corners: new WaylandCornerTheme(
            WindowCornerRadius: 12,
            ButtonCornerRadius: 8));
}
