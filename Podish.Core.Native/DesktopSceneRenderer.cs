using Podish.Display;
using Podish.Wayland;

namespace Podish.Core.Native;

internal interface IDesktopSceneRenderer : IDisposable
{
    void Resize(DisplaySize logicalSize, DisplaySize pixelSize);
    DisplayRect GetPixelBounds(DisplayRect logical);
    IDisplayTexture CreateTexture(DisplayTextureDescriptor descriptor, bool nearest = false);
    void FillRect(DisplayRect rect, DisplayColor color);
    void DrawChrome(DisplayRect bounds, DisplayColor border, DisplayColor title, int radius, int inset, int titleHeight);
    void DrawRoundedRect(DisplayRect rect, DisplayColor color, float radiusLogical);
    void DrawVectorIcon(DisplayRect rect, int iconKind, DisplayColor color);
    void Blit(IDisplayTexture texture, DisplayRect destination, WaylandSurfaceMapping? mapping = null);
    void BlitPixels(IDisplayTexture texture, DisplayRect destination, WaylandSurfaceMapping? mapping = null);
    void BlitRounded(IDisplayTexture texture, DisplayRect destination, float radiusLogical, float alpha = 1f,
        WaylandSurfaceMapping? mapping = null);
}

internal static class DesktopSceneGeometry
{
    internal static DisplayRect GetPixelBounds(DisplayRect rect, DisplaySize logical, DisplaySize pixels)
    {
        static int Snap(long edge, int source, int target) =>
            (int)Math.Clamp(Math.Floor((double)edge * target / Math.Max(1, source) + 0.5), int.MinValue, int.MaxValue);

        int left = Snap(rect.X, logical.Width, pixels.Width);
        int top = Snap(rect.Y, logical.Height, pixels.Height);
        int right = Snap((long)rect.X + rect.Width, logical.Width, pixels.Width);
        int bottom = Snap((long)rect.Y + rect.Height, logical.Height, pixels.Height);
        return new DisplayRect(left, top, (int)Math.Clamp((long)right - left, 0, int.MaxValue),
            (int)Math.Clamp((long)bottom - top, 0, int.MaxValue));
    }
}
