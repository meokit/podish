using System.Numerics;
using System.Runtime.InteropServices;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Core.Graphics;
using Podish.Display;
using Podish.Wayland;

namespace Podish.Core.Native;

internal sealed unsafe class DesktopVulkanRenderer : IDesktopSceneRenderer
{
    private readonly nint _library;
    private readonly nint _renderer;
    private readonly ILogger _logger;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _destroy;
    private readonly delegate* unmanaged[Cdecl]<nint, int, int, int> _resize;
    private readonly delegate* unmanaged[Cdecl]<nint, int> _begin;
    private readonly delegate* unmanaged[Cdecl]<nint, ulong> _end;
    private readonly delegate* unmanaged[Cdecl]<nint, ulong> _completed;
    private readonly delegate* unmanaged[Cdecl]<nint, int, int, int, int, nint> _createTexture;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, int, int, int, int, byte*, int, int> _updateTexture;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, void> _destroyTexture;
    private readonly delegate* unmanaged[Cdecl]<nint, int, ulong, ulong, int, int, int, int, nint> _importTexture;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, DrawArgs*, void> _draw;
    private readonly delegate* unmanaged[Cdecl]<byte*> _lastError;
    private readonly HashSet<Texture> _usedImports = [];
    private readonly PriorityQueue<(DrmScanoutBuffer Buffer, ulong Lease), ulong> _retiredImports = new();
    private DisplaySize _logicalSize;
    private DisplaySize _pixelSize;
    private bool _disposed;

    private DesktopVulkanRenderer(nint library, nint layer, DisplaySize logicalSize, DisplaySize pixelSize, ILogger logger)
    {
        _library = library;
        _logicalSize = logicalSize;
        _pixelSize = pixelSize;
        _logger = logger;
        var create = (delegate* unmanaged[Cdecl]<nint, int, int, nint>)Symbol("create");
        _destroy = (delegate* unmanaged[Cdecl]<nint, void>)Symbol("destroy");
        _resize = (delegate* unmanaged[Cdecl]<nint, int, int, int>)Symbol("resize");
        _begin = (delegate* unmanaged[Cdecl]<nint, int>)Symbol("begin");
        _end = (delegate* unmanaged[Cdecl]<nint, ulong>)Symbol("end");
        _completed = (delegate* unmanaged[Cdecl]<nint, ulong>)Symbol("completed");
        _createTexture = (delegate* unmanaged[Cdecl]<nint, int, int, int, int, nint>)Symbol("texture_create");
        _updateTexture = (delegate* unmanaged[Cdecl]<nint, nint, int, int, int, int, byte*, int, int>)Symbol("texture_update");
        _destroyTexture = (delegate* unmanaged[Cdecl]<nint, nint, void>)Symbol("texture_destroy");
        _importTexture = (delegate* unmanaged[Cdecl]<nint, int, ulong, ulong, int, int, int, int, nint>)Symbol("texture_import_fd");
        _draw = (delegate* unmanaged[Cdecl]<nint, nint, DrawArgs*, void>)Symbol("draw");
        _lastError = (delegate* unmanaged[Cdecl]<byte*>)Symbol("last_error");

        _renderer = create(layer, pixelSize.Width, pixelSize.Height);
        if (_renderer == 0)
            throw Failure("Create compositor");
        logger.LogInformation("Apple Wayland DiligentCore Vulkan compositor {Width}x{Height}",
            pixelSize.Width, pixelSize.Height);

        nint Symbol(string name) => NativeLibrary.GetExport(library, "podish_compositor_" + name);
    }

    internal static DesktopVulkanRenderer Create(string frameworkDirectory, nint layer,
        DisplaySize logicalSize, DisplaySize pixelSize, ILogger logger)
    {
        nint library = NativeLibrary.Load(Path.Combine(frameworkDirectory, "PodishVenus.framework", "PodishVenus"));
        try
        {
            return new DesktopVulkanRenderer(library, layer, logicalSize, pixelSize, logger);
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
    }

    internal bool HasPendingUploads => _retiredImports.Count != 0;
    internal bool SupportsVirglTextures => false;

    internal bool BeginScene()
    {
        if (_begin(_renderer) == 0)
            return false;

        Draw(null, new DisplayRect(0, 0, _pixelSize.Width, _pixelSize.Height), 3);
        return true;
    }

    internal void EndScene()
    {
        ulong submission = _end(_renderer);
        if (submission == 0)
            throw Failure("Submit compositor frame");

        foreach (var texture in _usedImports)
            texture.LastUse = submission;
        _usedImports.Clear();
    }

    internal void TakeCompletedUploads(List<ulong> consumed)
    {
        ulong completed = _completed(_renderer);
        while (_retiredImports.TryPeek(out var upload, out ulong submission) && submission <= completed)
        {
            _retiredImports.Dequeue();
            upload.Buffer.Release();
            consumed.Add(upload.Lease);
        }
    }

    public void Resize(DisplaySize logicalSize, DisplaySize pixelSize)
    {
        if (logicalSize.Width <= 0 || logicalSize.Height <= 0 || pixelSize.Width <= 0 || pixelSize.Height <= 0)
            return;

        if (_pixelSize != pixelSize && _resize(_renderer, pixelSize.Width, pixelSize.Height) == 0)
            throw Failure("Resize compositor");
        _pixelSize = pixelSize;
        _logicalSize = logicalSize;
    }

    public DisplayRect GetPixelBounds(DisplayRect logical) =>
        DesktopSceneGeometry.GetPixelBounds(logical, _logicalSize, _pixelSize);

    public IDisplayTexture CreateTexture(DisplayTextureDescriptor descriptor, bool nearest = false)
    {
        nint texture = _createTexture(_renderer, descriptor.Width, descriptor.Height, NativeFormat(descriptor.Format),
            nearest ? 1 : 0);
        if (texture == 0)
            throw Failure("Create compositor texture");
        return new Texture(this, texture, descriptor);
    }

    internal IDisplayTexture? ImportTexture(WaylandGpuFrame frame, DisplayTextureDescriptor descriptor)
    {
        int format = NativeFormat(descriptor.Format);
        if (frame.Buffer is not VenusScanoutBuffer source || frame.YInverted)
            return null;

        nint texture = _importTexture(_renderer, source.HostFd, source.BackingSize, source.Offset,
            checked((int)source.Pitch), descriptor.Width, descriptor.Height, format);
        if (texture == 0)
        {
            _logger.LogWarning("Failed to import {BufferType} {Width}x{Height} stride={Stride}: {Error}",
                frame.Buffer.GetType().Name, descriptor.Width, descriptor.Height, frame.Buffer.Pitch, ErrorMessage());
            return null;
        }

        frame.Buffer.Retain();
        return new Texture(this, texture, descriptor, frame.Buffer, frame.LeaseToken, frame.YInverted);
    }

    public void FillRect(DisplayRect rect, DisplayColor color) =>
        Draw(null, GetPixelBounds(rect), 0, color: Premultiply(color));

    public void DrawChrome(DisplayRect bounds, DisplayColor border, DisplayColor title, int radius, int inset, int titleHeight)
    {
        var pixels = GetPixelBounds(bounds);
        var titlePixels = GetPixelBounds(new DisplayRect(bounds.X + inset, bounds.Y, bounds.Width - 2 * inset, titleHeight));
        Draw(null, pixels, 4, color: Premultiply(border), titleColor: Premultiply(title),
            parameters: new Vector4(ScaleRadius(radius), titlePixels.X - pixels.X, titlePixels.Height,
                titlePixels.X + titlePixels.Width - pixels.X));
    }

    public void DrawRoundedRect(DisplayRect rect, DisplayColor color, float radiusLogical) =>
        Draw(null, GetPixelBounds(rect), 7, color: Premultiply(color),
            parameters: new Vector4(ScaleRadius(radiusLogical), 0, 0, 0));

    public void DrawVectorIcon(DisplayRect rect, int iconKind, DisplayColor color) =>
        Draw(null, GetPixelBounds(rect), 9, color: Premultiply(color), parameters: new Vector4(iconKind, 0, 0, 0));

    public void Blit(IDisplayTexture texture, DisplayRect destination, WaylandSurfaceMapping? mapping = null) =>
        BlitPixels(texture, GetPixelBounds(destination), mapping);

    public void BlitPixels(IDisplayTexture texture, DisplayRect destination, WaylandSurfaceMapping? mapping = null) =>
        Draw((Texture)texture, destination, texture.Format == DisplayPixelFormat.Argb8888 ? 5 : 6, mapping);

    public void BlitRounded(IDisplayTexture texture, DisplayRect destination, float radiusLogical, float alpha = 1f,
        WaylandSurfaceMapping? mapping = null)
    {
        if (alpha <= 0.001f)
            return;
        int format = texture.Format == DisplayPixelFormat.Argb8888 ? 2 : 3;
        Draw((Texture)texture, GetPixelBounds(destination), 8, mapping,
            parameters: new Vector4(ScaleRadius(radiusLogical), format, Math.Clamp(alpha, 0f, 1f), 0));
    }

    private float ScaleRadius(float radius) => radius * _pixelSize.Height / Math.Max(1, _logicalSize.Height);

    private static int NativeFormat(DisplayPixelFormat format) => format == DisplayPixelFormat.Argb8888 ? 1 : 0;

    private static Vector4 Premultiply(DisplayColor color)
    {
        float alpha = color.A / 255f;
        return new Vector4(color.R / 255f * alpha, color.G / 255f * alpha, color.B / 255f * alpha, alpha);
    }

    private void Draw(Texture? texture, DisplayRect rect, int mode, WaylandSurfaceMapping? mapping = null,
        Vector4 color = default, Vector4 titleColor = default, Vector4 parameters = default)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var sample = mapping ?? new WaylandSurfaceMapping(0, 0, 0, 0, 1, 0, 0, 1);
        if (texture?.YInverted == true)
            sample = sample with { V = 1f - sample.V, Vx = -sample.Vx, Vy = -sample.Vy };
        var args = new DrawArgs
        {
            Rect = new Vector4(rect.X, rect.Y, rect.Width, rect.Height),
            Color = color,
            TitleColor = titleColor,
            UvOrigin = new Vector2(sample.U, sample.V),
            UvBasis = new Vector4(sample.Ux, sample.Vx, sample.Uy, sample.Vy),
            Parameters = parameters,
            Mode = mode
        };
        _draw(_renderer, texture?.Handle ?? 0, &args);
        if (texture is { Source: not null })
            _usedImports.Add(texture);
    }

    private string ErrorMessage() => Marshal.PtrToStringUTF8((nint)_lastError()) ?? "Unknown Vulkan error";
    private InvalidOperationException Failure(string operation) => new($"{operation}: {ErrorMessage()}");

    private void DestroyTexture(Texture texture, nint handle)
    {
        _destroyTexture(_renderer, handle);
        if (texture.Source != null)
            _retiredImports.Enqueue((texture.Source, texture.Lease), texture.LastUse);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _destroy(_renderer);
        while (_retiredImports.TryDequeue(out var upload, out _))
            upload.Buffer.Release();
        NativeLibrary.Free(_library);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrawArgs
    {
        public Vector4 Rect;
        public Vector4 Color;
        public Vector4 TitleColor;
        public Vector2 UvOrigin;
        public Vector4 UvBasis;
        public Vector4 Parameters;
        public float Mode;
        public float Padding;
    }

    private sealed class Texture(DesktopVulkanRenderer owner, nint handle, DisplayTextureDescriptor descriptor,
        DrmScanoutBuffer? source = null, ulong lease = 0, bool yInverted = false) : IDisplayTexture
    {
        internal nint Handle { get; private set; } = handle;
        internal DrmScanoutBuffer? Source { get; } = source;
        internal ulong Lease { get; } = lease;
        internal bool YInverted { get; } = yInverted;
        internal ulong LastUse { get; set; }
        public int Width => descriptor.Width;
        public int Height => descriptor.Height;
        public DisplayPixelFormat Format => descriptor.Format;

        public void Update(ReadOnlySpan<byte> pixels, int pitch) =>
            Update(new DisplayRect(0, 0, Width, Height), pixels, pitch);

        public void Update(DisplayRect rect, ReadOnlySpan<byte> pixels, int pitch)
        {
            ObjectDisposedException.ThrowIf(Handle == 0, this);
            if (Source != null)
                throw new InvalidOperationException("Cannot upload pixels into a Venus buffer.");
            if (rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0 ||
                (long)rect.X + rect.Width > Width || (long)rect.Y + rect.Height > Height ||
                pitch < (long)rect.Width * 4 || (long)(rect.Height - 1) * pitch + rect.Width * 4L > pixels.Length)
                throw new ArgumentException("Texture update does not match the source or destination bounds.", nameof(rect));

            fixed (byte* data = pixels)
            {
                if (owner._updateTexture(owner._renderer, Handle, rect.X, rect.Y, rect.Width, rect.Height, data, pitch) == 0)
                    throw owner.Failure("Upload compositor texture");
            }
        }

        public void Dispose()
        {
            if (Handle == 0)
                return;
            owner.DestroyTexture(this, Handle);
            Handle = 0;
        }
    }
}
