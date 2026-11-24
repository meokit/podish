using System.Runtime.InteropServices;
using System.Xml;
using Podish.Wayland;
using SkiaSharp;
using Svg;
using Svg.Skia;

namespace Podish.Cli.Wayland;

internal static class WaylandIconRasterizer
{
    static WaylandIconRasterizer()
    {
        SvgDocument.DisableDtdProcessing = true;
        SvgDocument.ResolveExternalXmlEntites = ExternalType.None;
        SvgDocument.ResolveExternalElements = ExternalType.None;
        SvgDocument.ResolveExternalImages = ExternalType.None;
    }

    internal static byte[]? Rasterize(WaylandApplicationIcon icon, int size)
    {
        using var stream = new MemoryStream(icon.Data.ToArray(), writable: false);
        using var output = new SKBitmap(new SKImageInfo(size, size,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Transparent);
        if (icon.IsSvg)
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreProcessingInstructions = true,
                MaxCharactersInDocument = 512 * 1024
            });
            using var svg = new SKSvg();
            var picture = svg.Load(reader);
            if (picture == null)
                return null;

            SKRect bounds = picture.CullRect;
            if (!float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height) ||
                bounds.Width <= 0 || bounds.Height <= 0)
                return null;

            SKRect target = Fit(bounds.Width, bounds.Height, size);
            canvas.Translate(target.Left, target.Top);
            canvas.Scale(target.Width / bounds.Width, target.Height / bounds.Height);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
        }
        else
        {
            using var codec = SKCodec.Create(stream);
            if (codec == null || codec.EncodedFormat != SKEncodedImageFormat.Png ||
                codec.Info.Width > 1024 || codec.Info.Height > 1024)
                return null;

            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap == null)
                return null;
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(bitmap, Fit(bitmap.Width, bitmap.Height, size), paint);
        }
        canvas.Flush();

        var pixels = new byte[size * size * 4];
        Marshal.Copy(output.GetPixels(), pixels, 0, pixels.Length);
        return pixels;
    }

    private static SKRect Fit(float width, float height, int size)
    {
        float scale = size / Math.Max(width, height);
        float w = width * scale;
        float h = height * scale;
        return SKRect.Create((size - w) / 2, (size - h) / 2, w, h);
    }
}
