using System.Buffers;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Cli.Display;
using Podish.Display;
using Podish.Wayland;

namespace Podish.Cli.Wayland;

internal sealed class WaylandSdlDisplayHost : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly WaylandDesktopOptions _desktopOptions;
    private SdlDisplayBackend? _backend;
    private IDisplayOutput? _output;
    private IDisplayRenderer? _renderer;
    private readonly Dictionary<ulong, SurfaceTextureState> _surfaces = [];
    private readonly List<ulong> _zOrder = [];

    public WaylandSdlDisplayHost(ILoggerFactory loggerFactory, WaylandDesktopOptions desktopOptions)
    {
        _loggerFactory = loggerFactory;
        _desktopOptions = desktopOptions;
    }

    public bool IsClosed => _output?.IsClosed ?? false;

    public void Start()
    {
        if (_output != null)
            return;

        _backend = new SdlDisplayBackend(_loggerFactory.CreateLogger<SdlDisplayBackend>());
        _output = _backend.CreateOutput(new DisplayOutputOptions(
            _desktopOptions.Width,
            _desktopOptions.Height,
            "Podish Wayland",
            DisplayVSyncMode.Enabled,
            AllowHighDpi: true,
            Resizable: true));
        _renderer = _output.Renderer;
    }

    public bool DrainCommands(IEnumerable<WaylandDisplayCommand> commands, List<ulong> consumedLeases, out bool shutdownRequested)
    {
        EnsureStarted();
        shutdownRequested = false;
        bool dirty = false;

        foreach (WaylandDisplayCommand command in commands)
        {
            switch (command.Kind)
            {
                case WaylandDisplayCommandKind.PresentSurface:
                    dirty = true;
                    UpsertSurface(command.SceneSurfaceId, command.Frame, command.Bounds);
                    consumedLeases.Add(command.Frame.LeaseToken);
                    break;
                case WaylandDisplayCommandKind.UpdateSurfaceBounds:
                    dirty = true;
                    UpdateSurfaceBounds(command.SceneSurfaceId, command.Bounds);
                    break;
                case WaylandDisplayCommandKind.RaiseSurface:
                    dirty = true;
                    RaiseSurface(command.SceneSurfaceId);
                    break;
                case WaylandDisplayCommandKind.RemoveSurface:
                    dirty = true;
                    RemoveSurface(command.SceneSurfaceId);
                    break;
                case WaylandDisplayCommandKind.SetCursor:
                    SetCursor(command.Cursor);
                    break;
                case WaylandDisplayCommandKind.ClearCursor:
                    ClearCursor();
                    break;
                case WaylandDisplayCommandKind.Shutdown:
                    shutdownRequested = true;
                    break;
            }
        }

        return dirty;
    }

    public IReadOnlyList<DisplayInputEvent> PumpInputEvents()
    {
        EnsureStarted();
        _output!.PumpEvents();
        return _output.DrainInputEvents();
    }

    public bool TryDequeueResize(out DisplaySize size)
    {
        EnsureStarted();
        return _output!.TryDequeueResize(out size);
    }

    public void Render()
    {
        EnsureStarted();
        _renderer!.Clear(new DisplayColor(0, 0, 0, 255));

        foreach (ulong sceneSurfaceId in _zOrder)
        {
            if (!_surfaces.TryGetValue(sceneSurfaceId, out SurfaceTextureState? surface))
                continue;

            _renderer.Blit(surface.Texture, destination: ToDisplayRect(surface.Bounds));
        }

        _output!.Present();
    }

    public void Dispose()
    {
        foreach (SurfaceTextureState surface in _surfaces.Values)
            surface.Texture.Dispose();
        _surfaces.Clear();
        _zOrder.Clear();
        _output?.ClearCursor();
        _output?.Dispose();
        _output = null;
        _renderer = null;
        _backend?.Dispose();
        _backend = null;
    }

    private void UpsertSurface(ulong sceneSurfaceId, WaylandShmFrame frame, WaylandSurfaceBounds bounds)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Stride <= 0)
            throw new InvalidOperationException(
                $"Invalid Wayland shm frame geometry: {frame.Width}x{frame.Height} stride={frame.Stride}.");

        DisplayPixelFormat pixelFormat = frame.Format switch
        {
            WlShmFormat.Argb8888 => DisplayPixelFormat.Argb8888,
            WlShmFormat.Xrgb8888 => DisplayPixelFormat.Xrgb8888,
            _ => throw new NotSupportedException($"Unsupported wl_shm format {frame.Format}.")
        };

        SurfaceTextureState state = EnsureSurfaceTexture(sceneSurfaceId, frame.Width, frame.Height, pixelFormat);
        UpdateTexture(state.Texture, frame);
        state.Width = frame.Width;
        state.Height = frame.Height;
        state.Format = pixelFormat;
        state.Bounds = bounds;

        _zOrder.Remove(sceneSurfaceId);
        _zOrder.Add(sceneSurfaceId);
    }

    private void UpdateSurfaceBounds(ulong sceneSurfaceId, WaylandSurfaceBounds bounds)
    {
        if (_surfaces.TryGetValue(sceneSurfaceId, out SurfaceTextureState? surface))
            surface.Bounds = bounds;
    }

    private void RaiseSurface(ulong sceneSurfaceId)
    {
        if (!_surfaces.ContainsKey(sceneSurfaceId))
            return;

        _zOrder.Remove(sceneSurfaceId);
        _zOrder.Add(sceneSurfaceId);
    }

    private void RemoveSurface(ulong sceneSurfaceId)
    {
        if (!_surfaces.Remove(sceneSurfaceId, out SurfaceTextureState? surface))
            return;

        _zOrder.Remove(sceneSurfaceId);
        surface.Texture.Dispose();
    }

    private SurfaceTextureState EnsureSurfaceTexture(ulong sceneSurfaceId, int width, int height,
        DisplayPixelFormat format)
    {
        EnsureStarted();
        if (_surfaces.TryGetValue(sceneSurfaceId, out SurfaceTextureState? existing) &&
            existing.Width == width &&
            existing.Height == height &&
            existing.Format == format)
            return existing;

        existing?.Texture.Dispose();
        var replacement = new SurfaceTextureState(
            _renderer!.CreateTexture(new DisplayTextureDescriptor(width, height, format, DisplayTextureAccess.Streaming)),
            width,
            height,
            format);
        _surfaces[sceneSurfaceId] = replacement;
        return replacement;
    }

    private static DisplayRect ToDisplayRect(WaylandSurfaceBounds bounds) =>
        new(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    private void SetCursor(WaylandCursorFrame cursor)
    {
        EnsureStarted();
        try
        {
            DisplayCursorDescriptor descriptor = ReadCursor(cursor);
            _output!.SetCursor(descriptor);
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<WaylandSdlDisplayHost>()
                .LogWarning(ex, "Failed to bridge Wayland cursor sceneSurfaceId={SceneSurfaceId}; ignoring cursor update",
                    cursor.SceneSurfaceId);
        }
    }

    private void ClearCursor()
    {
        EnsureStarted();
        _output!.ClearCursor();
    }

    private void EnsureStarted()
    {
        if (_output == null || _renderer == null)
            throw new InvalidOperationException("SDL display host has not been started.");
    }

    private static void UpdateTexture(IDisplayTexture texture, WaylandShmFrame frame)
    {
        const int maxChunkRows = 64;
        int maxChunkBytes = frame.Stride * Math.Min(frame.Height, maxChunkRows);
        byte[] scratch = ArrayPool<byte>.Shared.Rent(maxChunkBytes);
        try
        {
            for (int y = 0; y < frame.Height; y += maxChunkRows)
            {
                int rows = Math.Min(maxChunkRows, frame.Height - y);
                int chunkBytes = frame.Stride * rows;
                ReadExactly(frame, y, scratch, chunkBytes);
                texture.Update(new DisplayRect(0, y, frame.Width, rows), scratch.AsSpan(0, chunkBytes), frame.Stride);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static void ReadExactly(WaylandShmFrame frame, int rowOffset, byte[] destination, int length)
    {
        if (frame.File.OpenedInode == null)
            throw new InvalidOperationException("Wayland shm file has no inode.");

        long offset = frame.Offset + (long)rowOffset * frame.Stride;
        if (frame.File.OpenedInode is IndexedMemoryInode indexedInode &&
            TryReadIndexedSegments(indexedInode, offset, destination, length))
            return;

        ReadExactly(frame.File, offset, destination.AsSpan(0, length));
    }

    private static bool TryReadIndexedSegments(IndexedMemoryInode indexedInode, long offset, byte[] destination,
        int length)
    {
        int written = 0;
        bool ok = indexedInode.VisitReadSegments(offset, length, (ptr, chunkLength) =>
        {
            unsafe
            {
                new ReadOnlySpan<byte>((void*)ptr, chunkLength).CopyTo(destination.AsSpan(written, chunkLength));
            }

            written += chunkLength;
            return true;
        });

        return ok && written == length;
    }

    private static void ReadExactly(LinuxFile file, long offset, Span<byte> destination)
    {
        if (file.OpenedInode == null)
            throw new InvalidOperationException("Wayland shm file has no inode.");

        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = file.OpenedInode.Read(file, destination[totalRead..], offset + totalRead);
            if (read <= 0)
                throw new InvalidOperationException("Failed to read complete shm frame contents.");
            totalRead += read;
        }
    }

    private static DisplayCursorDescriptor ReadCursor(WaylandCursorFrame cursor)
    {
        WaylandShmFrame frame = cursor.Frame;
        int byteLength = checked(frame.Stride * frame.Height);
        byte[] pixels = GC.AllocateUninitializedArray<byte>(byteLength);
        ReadExactly(frame, 0, pixels, byteLength);

        if (frame.Format == WlShmFormat.Xrgb8888)
        {
            for (int i = 0; i < byteLength; i += 4)
                pixels[i + 3] = 0xFF;
        }

        return new DisplayCursorDescriptor(
            frame.Width,
            frame.Height,
            cursor.HotspotX,
            cursor.HotspotY,
            DisplayPixelFormat.Argb8888,
            pixels,
            frame.Stride);
    }

    private sealed class SurfaceTextureState(IDisplayTexture texture, int width, int height, DisplayPixelFormat format)
    {
        public IDisplayTexture Texture { get; } = texture;
        public int Width { get; set; } = width;
        public int Height { get; set; } = height;
        public DisplayPixelFormat Format { get; set; } = format;
        public WaylandSurfaceBounds Bounds { get; set; }
    }
}
