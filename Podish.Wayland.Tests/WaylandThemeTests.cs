using Xunit;

namespace Podish.Wayland.Tests;

public sealed class WaylandThemeTests
{
    [Fact]
    public void Theme_DefaultLoadsBundledCatppuccinLatte()
    {
        WaylandUiTheme theme = WaylandUiTheme.Load(null);
        var close = Assert.IsType<WaylandTitlebarButtonTheme>(theme.WindowDecoration.Close);
        var bitmap = Assert.IsType<WaylandButtonBitmap>(close.Active.Unpressed.Bitmap);

        Assert.Equal(new WaylandColor(0xdc, 0xe0, 0xe8), theme.WindowDecoration.Active.Background);
        Assert.Equal(new WaylandColor(0xd2, 0x0f, 0x39), close.Active.Unpressed.Foreground);
        Assert.Equal(new WaylandColor(0xcc, 0xd0, 0xda), close.Active.Hover.Foreground);
        Assert.Equal(new WaylandColor(0x8c, 0x8f, 0xa1), close.Inactive.Unpressed.Foreground);
        Assert.Equal((byte)0, close.Active.Unpressed.Background.A);
        Assert.Equal(WaylandTitleAlignment.Center, theme.Typography.Alignment);
        Assert.Equal(10, bitmap.Width);
        Assert.True(bitmap.IsSet(8, 2));
        Assert.False(bitmap.IsSet(9, 2));
        Assert.Equal(WaylandUiTheme.BreezeLight, WaylandUiTheme.Load("breeze-light"));
    }

    [Fact]
    public void Theme_LoadsLabwcSettingsAndCompanionButtonBitmap()
    {
        var themeDirectory = Directory.CreateTempSubdirectory("podish-wayland-theme-");
        try
        {
            DirectoryInfo labwc = themeDirectory.CreateSubdirectory("labwc");
            File.WriteAllText(Path.Combine(labwc.FullName, "themerc"), """
                border.width: 3
                window.*.title.bg.color: #123
                window.active.title.bg.color: rgb:4/5/6
                window.label.text.justify: Right
                window.titlebar.padding.height: 4
                window.button.width: 27
                window.button.height: 22
                window.button.spacing: 5
                window.active.button.unpressed.image.color: #ffeedd
                window.active.button.max.toggled.hover.image.color: #ddeeff
                window.button.hover.bg.color: #00000080
                """);
            File.WriteAllText(Path.Combine(labwc.FullName, "max_toggled.xbm"), """
                #define max_toggled_width 3
                #define max_toggled_height 2
                static unsigned char max_toggled_bits[] = { 0x01, 0x04 };
                """);

            WaylandUiTheme theme = WaylandUiTheme.Load(themeDirectory.FullName);
            var maximize = Assert.IsType<WaylandTitlebarButtonTheme>(theme.WindowDecoration.Maximize);
            WaylandButtonAppearance hover = maximize.GetAppearance(active: true, toggled: true, hover: true, pressed: false);
            var bitmap = Assert.IsType<WaylandButtonBitmap>(hover.Bitmap);
            WaylandSurfaceBounds button = WaylandDecorationLayout.GetMaximizeButtonBounds(new(0, 0, 300, 200), theme);

            Assert.Equal(new WaylandColor(0x44, 0x55, 0x66), theme.WindowDecoration.Active.Background);
            Assert.Equal(new WaylandColor(0x11, 0x22, 0x33), theme.WindowDecoration.Inactive.Background);
            Assert.Equal(new WaylandColor(0xdd, 0xee, 0xff), hover.Foreground);
            Assert.Equal((byte)128, hover.Background.A);
            Assert.True(bitmap.IsSet(2, 1));
            Assert.False(bitmap.IsSet(1, 1));
            Assert.Equal(3, theme.Spacing.WindowBorderThickness);
            Assert.Equal(WaylandTitleAlignment.Right, theme.Typography.Alignment);
            Assert.Equal(27, button.Width);
            Assert.Equal(22, button.Height);
        }
        finally
        {
            themeDirectory.Delete(recursive: true);
        }
    }
}
