using System.Runtime.InteropServices;
using System.Text;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Core.Graphics;
using Podish.Display;
using Podish.Wayland;

namespace Podish.Cli.Display;

/// <summary>Composes into an offscreen GLES3 framebuffer and previews completed virtual KMS flips.</summary>
internal sealed unsafe partial class AngleDisplayRenderer : IDisposable
{
    private const uint Texture2D = 0x0DE1, Rgba = 0x1908, Rgba8 = 0x8058, UnsignedByte = 0x1401;
    private const uint Framebuffer = 0x8D40, ColorAttachment0 = 0x8CE0, FramebufferComplete = 0x8CD5;
    private const uint ReadFramebuffer = 0x8CA8, DrawFramebuffer = 0x8CA9, ColorBufferBit = 0x4000;
    private readonly AngleLibrary _angle;
    private readonly KmsEglWindow _window;
    private nint _surface;
    private nint _context;
    private uint _program;
    private uint _framebuffer;
    private uint _importFramebuffer;
    private ScanoutBuffer[] _scanout = [];
    private DisplaySize _size;
    private DisplaySize _logicalSize;
    private DisplaySize _previewSize;
    private ILogger? _logger;
    private int _viewport, _destination, _mode, _color, _titleColor, _chrome;
    private int _textureOrigin, _textureBasis;
    private readonly Queue<(nint Fence, ulong Lease, DrmScanoutBuffer Buffer)> _uploads = new();

    internal KmsEglOutput? GuestTextures { get; private set; }

    internal static AngleDisplayRenderer Create(KmsEglWindow window, VirtualDrmScanout scanout, DisplaySize logicalSize, bool enableGles,
        ILogger logger)
    {
        string? directory = Environment.GetEnvironmentVariable("PODISH_ANGLE_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Set PODISH_ANGLE_DIR to the host ANGLE EGL/GLES library directory.");

        var angle = AngleLibrary.Acquire(Path.GetFullPath(directory), window, hardwareCompositor: true);
        AngleDisplayRenderer? renderer = null;
        try
        {
            renderer = new AngleDisplayRenderer(angle, window);
            renderer.Initialize(scanout, logicalSize, enableGles, logger);
            return renderer;
        }
        catch
        {
            if (renderer != null)
                renderer.Dispose();
            else
                angle.Release();
            throw;
        }
    }

    private void Initialize(VirtualDrmScanout scanout, DisplaySize logicalSize, bool enableGles, ILogger logger)
    {
        _logger = logger;
        var choose = (delegate* unmanaged[Cdecl]<nint, int*, nint*, int, int*, uint>)_angle.Egl("eglChooseConfig");
        int* configArgs = stackalloc int[]
        {
            0x3033, 0x0004, // EGL_SURFACE_TYPE, EGL_WINDOW_BIT
            0x3040, AngleLibrary.Es3Bit,
            0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3038
        };
        nint config;
        int count;
        RequireEgl(choose(_angle.Display, configArgs, &config, 1, &count) != 0 && count > 0,
            "choose a GLES3 window config");

        var createWindow = (delegate* unmanaged[Cdecl]<nint, nint, nint, int*, nint>)_angle.Egl("eglCreateWindowSurface");
        _surface = createWindow(_angle.Display, config, _window.Handle, null);
        RequireEgl(_surface != 0, "create the preview surface");
        int* contextArgs = stackalloc int[] { 0x3098, 3, 0x3038 }; // EGL_CONTEXT_CLIENT_VERSION
        _context = _angle.CreateContext(_angle.Display, config, 0, contextArgs);
        RequireEgl(_context != 0, "create a GLES3 context");
        RequireEgl(_angle.MakeCurrent(_angle.Display, _surface, _surface, _context) != 0, "bind the GLES3 context");

        int version = 0;
        _glGetIntegerv(0x821B /* GL_MAJOR_VERSION */, &version);
        string renderer = Marshal.PtrToStringUTF8(_glGetString(0x1F01 /* GL_RENDERER */)) ?? "";
        if (version < 3 || renderer.Length == 0 ||
            new[] { "SwiftShader", "WARP", "llvmpipe", "softpipe", "swrast", "Software Rasterizer", "Microsoft Basic Render" }
                .Any(name => renderer.Contains(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A hardware GLES3 renderer is required. ANGLE reported '{renderer}'.");

        var interval = (delegate* unmanaged[Cdecl]<nint, int, uint>)_angle.Egl("eglSwapInterval");
        interval(_angle.Display, 0); // Frame cadence comes from virtual KMS.

        _size = new DisplaySize(scanout.Width, scanout.Height);
        _logicalSize = logicalSize;
        _program = BuildProgram();
        _glUseProgram(_program);
        _viewport = Uniform("viewport\0"u8);
        _destination = Uniform("destination\0"u8);
        _mode = Uniform("mode\0"u8);
        _color = Uniform("color\0"u8);
        _titleColor = Uniform("titleColor\0"u8);
        _chrome = Uniform("chrome\0"u8);
        _textureOrigin = Uniform("textureOrigin\0"u8);
        _textureBasis = Uniform("textureBasis\0"u8);
        _glUniform1i(Uniform("image\0"u8), 0);

        uint framebuffer;
        _glGenFramebuffers(1, &framebuffer);
        _framebuffer = framebuffer;
        for (int i = 0; i < 2; i++)
        {
            var buffer = new ScanoutBuffer(this, AllocateTexture(_size.Width, _size.Height), _size);
            _scanout = [.. _scanout, buffer];
            BindScanout(buffer);
            if (_glCheckFramebufferStatus(Framebuffer) != FramebufferComplete)
                throw new InvalidOperationException("ANGLE could not allocate a virtual KMS framebuffer.");
        }

        if (enableGles)
        {
            if (_glImageTargetTexture == null)
                throw new InvalidOperationException("ANGLE does not support importing EGLImages into GLES textures.");
            GuestTextures = new KmsEglOutput(_angle, window: null, logger);
            _glGenFramebuffers(1, &framebuffer);
            _importFramebuffer = framebuffer;
        }

        _glEnable(0x0BE2 /* GL_BLEND */);
        _glBlendFunc(1 /* GL_ONE */, 0x0303 /* GL_ONE_MINUS_SRC_ALPHA */);
        CheckGl("initialize the compositor");
        scanout.SetGpuBuffers(_scanout[0], _scanout[1]);
        logger.LogInformation("Wayland GLES3 compositor renderer: {Renderer}, {Version}", renderer,
            Marshal.PtrToStringUTF8(_glGetString(0x1F02 /* GL_VERSION */)));
    }

    private void BindScanout(ScanoutBuffer buffer)
    {
        _glBindFramebuffer(Framebuffer, _framebuffer);
        _glFramebufferTexture2D(Framebuffer, ColorAttachment0, Texture2D, buffer.Name, 0);
    }

    internal void BeginScene(DrmScanoutBuffer buffer)
    {
        BindScanout((ScanoutBuffer)buffer);
        _glViewport(0, 0, _size.Width, _size.Height);
        _glUniform2f(_viewport, _size.Width, _size.Height);
        Draw(3, new DisplayRect(0, 0, _size.Width, _size.Height));
    }

    internal void EndScene()
    {
        CheckGl("render the virtual KMS framebuffer");
        _glFlush();
    }

    private bool Present(ScanoutBuffer scanout)
    {
        if (_window.CanPresent?.Invoke() == false)
            return true;

        var query = (delegate* unmanaged[Cdecl]<nint, nint, int, int*, uint>)_angle.Egl("eglQuerySurface");
        int width, height;
        RequireEgl(query(_angle.Display, _surface, 0x3057, &width) != 0, "query preview width");
        RequireEgl(query(_angle.Display, _surface, 0x3056, &height) != 0, "query preview height");
        if (width <= 0 || height <= 0)
            return true;

        var previewSize = new DisplaySize(width, height);
        if (_previewSize != previewSize)
        {
            _previewSize = previewSize;
            _logger?.LogInformation("Wayland EGL preview {Width}x{Height} pixels for {ScanoutWidth}x{ScanoutHeight} scanout",
                width, height, _size.Width, _size.Height);
        }

        BindScanout(scanout);
        _glBindFramebuffer(DrawFramebuffer, 0);
        // Avoid filtering when the preview matches scanout within native window-size rounding.
        uint filter = Math.Abs(width - _size.Width) <= 1 && Math.Abs(height - _size.Height) <= 1
            ? 0x2600u /* GL_NEAREST */ : 0x2601u /* GL_LINEAR */;
        _glBlitFramebuffer(0, 0, _size.Width, _size.Height, 0, 0, width, height, ColorBufferBit, filter);
        CheckGl("blit the virtual KMS framebuffer");
        RequireEgl(_angle.SwapBuffers(_angle.Display, _surface) != 0, "present the virtual KMS flip");
        return true;
    }

    internal void DrawChrome(DisplayRect bounds, DisplayColor border, DisplayColor title, int radius, int inset, int titleHeight)
    {
        var pixels = GetPixelBounds(bounds);
        var titlePixels = GetPixelBounds(new DisplayRect(bounds.X + inset, bounds.Y, bounds.Width - 2 * inset, titleHeight));
        SetColor(_color, border);
        SetColor(_titleColor, title);
        _glUniform4f(_chrome, radius * (float)_size.Height / _logicalSize.Height,
            titlePixels.X - pixels.X, titlePixels.Height, titlePixels.X + titlePixels.Width - pixels.X);
        Draw(4, pixels);
    }

    internal void FillRect(DisplayRect rect, DisplayColor color)
    {
        SetColor(_color, color);
        Draw(0, GetPixelBounds(rect));
    }

    private void SetColor(int uniform, DisplayColor color)
    {
        float alpha = color.A / 255f;
        _glUniform4f(uniform, color.R / 255f * alpha, color.G / 255f * alpha, color.B / 255f * alpha, alpha);
    }

    internal void Blit(IDisplayTexture texture, DisplayRect destination, WaylandSurfaceMapping? mapping = null)
        => BlitPixels(texture, GetPixelBounds(destination), mapping);

    internal void BlitPixels(IDisplayTexture texture, DisplayRect destination, WaylandSurfaceMapping? mapping = null)
    {
        var image = (Texture)texture;
        var sample = mapping ?? new WaylandSurfaceMapping(0, 0, 0, 0, 1, 0, 0, 1);
        _glUniform2f(_textureOrigin, sample.U, sample.V);
        _glUniform4f(_textureBasis, sample.Ux, sample.Vx, sample.Uy, sample.Vy);
        _glBindTexture(Texture2D, image.Name);
        int mode = image.Format == DisplayPixelFormat.Argb8888 ? 1 : 2;
        Draw(image.NativeRgba ? mode + 4 : mode, destination);
    }

    internal DisplayRect GetPixelBounds(DisplayRect logical) => GetPixelBounds(logical, _logicalSize, _size);

    internal static DisplayRect GetPixelBounds(DisplayRect bounds, DisplaySize logical, DisplaySize pixels)
    {
        static int Snap(long edge, int source, int target) =>
            (int)Math.Clamp(Math.Floor((double)edge * target / source + 0.5), int.MinValue, int.MaxValue);

        int left = Snap(bounds.X, logical.Width, pixels.Width);
        int top = Snap(bounds.Y, logical.Height, pixels.Height);
        int right = Snap((long)bounds.X + bounds.Width, logical.Width, pixels.Width);
        int bottom = Snap((long)bounds.Y + bounds.Height, logical.Height, pixels.Height);
        return new DisplayRect(left, top, (int)Math.Clamp((long)right - left, 0, int.MaxValue),
            (int)Math.Clamp((long)bottom - top, 0, int.MaxValue));
    }

    private void Draw(int mode, DisplayRect rect)
    {
        _glUniform1i(_mode, mode);
        _glUniform4f(_destination, rect.X, rect.Y, rect.Width, rect.Height);
        _glDrawArrays(0x0005 /* GL_TRIANGLE_STRIP */, 0, 4);
    }

    internal IDisplayTexture CreateTexture(DisplayTextureDescriptor descriptor, bool nearest = false) =>
        new Texture(this, AllocateTexture(descriptor.Width, descriptor.Height, nearest), descriptor);

    private uint AllocateTexture(int width, int height, bool nearest = false)
    {
        uint texture;
        _glGenTextures(1, &texture);
        _glBindTexture(Texture2D, texture);
        int filter = nearest ? 0x2600 /* GL_NEAREST */ : 0x2601 /* GL_LINEAR */;
        _glTexParameteri(Texture2D, 0x2801 /* GL_TEXTURE_MIN_FILTER */, filter);
        _glTexParameteri(Texture2D, 0x2800 /* GL_TEXTURE_MAG_FILTER */, filter);
        _glTexParameteri(Texture2D, 0x2802 /* GL_TEXTURE_WRAP_S */, 0x812F /* GL_CLAMP_TO_EDGE */);
        _glTexParameteri(Texture2D, 0x2803 /* GL_TEXTURE_WRAP_T */, 0x812F);
        _glTexImage2D(Texture2D, 0, (int)Rgba8, width, height, 0, Rgba, UnsignedByte, null);
        return texture;
    }

    private void DeleteTexture(uint texture) => _glDeleteTextures(1, &texture);

    private uint BuildProgram()
    {
        uint vertex = Compile(0x8B31 /* GL_VERTEX_SHADER */, VertexShader);
        uint fragment = 0;
        uint program = 0;
        try
        {
            fragment = Compile(0x8B30 /* GL_FRAGMENT_SHADER */, FragmentShader);
            program = _glCreateProgram();
            _glAttachShader(program, vertex);
            _glAttachShader(program, fragment);
            _glLinkProgram(program);
            int linked;
            _glGetProgramiv(program, 0x8B82 /* GL_LINK_STATUS */, &linked);
            if (linked == 0)
            {
                byte* log = stackalloc byte[2048];
                int length;
                _glGetProgramInfoLog(program, 2048, &length, log);
                throw new InvalidOperationException($"Compositor shader link failed: {Encoding.UTF8.GetString(log, length)}");
            }

            return program;
        }
        catch
        {
            _glDeleteProgram(program);
            throw;
        }
        finally
        {
            _glDeleteShader(vertex);
            _glDeleteShader(fragment);
        }
    }

    private uint Compile(uint type, string text)
    {
        uint shader = _glCreateShader(type);
        var source = Encoding.UTF8.GetBytes(text);
        fixed (byte* data = source)
        {
            byte* pointer = data;
            int length = source.Length;
            _glShaderSource(shader, 1, &pointer, &length);
        }
        _glCompileShader(shader);
        int compiled;
        _glGetShaderiv(shader, 0x8B81 /* GL_COMPILE_STATUS */, &compiled);
        if (compiled != 0)
            return shader;

        byte* log = stackalloc byte[2048];
        int logLength;
        _glGetShaderInfoLog(shader, 2048, &logLength, log);
        _glDeleteShader(shader);
        throw new InvalidOperationException($"Compositor shader compilation failed: {Encoding.UTF8.GetString(log, logLength)}");
    }

    private int Uniform(ReadOnlySpan<byte> name)
    {
        fixed (byte* text = name)
            return _glGetUniformLocation(_program, text);
    }

    private void RequireEgl(bool result, string operation)
    {
        if (!result)
            throw new InvalidOperationException($"ANGLE could not {operation} (EGL error 0x{_angle.GetError():X}).");
    }

    private void CheckGl(string operation)
    {
        uint error = _glGetError();
        if (error != 0)
            throw new InvalidOperationException($"ANGLE could not {operation} (GLES error 0x{error:X}).");
    }

    public void Dispose()
    {
        if (_context != 0)
        {
            uint framebuffer = _framebuffer;
            if (framebuffer != 0)
                _glDeleteFramebuffers(1, &framebuffer);
            if (_importFramebuffer != 0)
            {
                framebuffer = _importFramebuffer;
                _glDeleteFramebuffers(1, &framebuffer);
            }
            foreach (var buffer in _scanout)
                buffer.Release();
            if (_uploads.Count != 0)
                _glFinish();
            while (_uploads.TryDequeue(out var upload))
            {
                DestroyFence(upload.Fence);
                upload.Buffer.Release();
            }
            if (_program != 0)
                _glDeleteProgram(_program);
            _angle.MakeCurrent(_angle.Display, 0, 0, 0);
            _angle.DestroyContext(_angle.Display, _context);
            _context = 0;
        }
        if (_surface != 0)
        {
            _angle.DestroySurface(_angle.Display, _surface);
            _surface = 0;
        }
        GuestTextures?.Dispose();
        _angle.Release();
    }

    private sealed class ScanoutBuffer(AngleDisplayRenderer owner, uint name, DisplaySize size)
        : DrmScanoutBuffer((uint)size.Width, (uint)size.Height, (uint)size.Width * 4)
    {
        internal uint Name => name;
        public override bool Ready => true;
        public override bool Present() => owner.Present(this);
        protected override void Destroy() => owner.DeleteTexture(name);
    }

    private sealed class Texture(AngleDisplayRenderer owner, uint name, DisplayTextureDescriptor descriptor) : IDisplayTexture
    {
        internal uint Name { get; private set; } = name;
        public int Width => descriptor.Width;
        public int Height => descriptor.Height;
        public DisplayPixelFormat Format => descriptor.Format;
        internal bool NativeRgba { get; set; }

        public void Update(ReadOnlySpan<byte> pixels, int pitch) =>
            Update(new DisplayRect(0, 0, Width, Height), pixels, pitch);

        public void Update(DisplayRect rect, ReadOnlySpan<byte> pixels, int pitch)
        {
            NativeRgba = false;
            owner._glBindTexture(Texture2D, Name);
            fixed (byte* data = pixels)
            {
                if (pitch % 4 == 0)
                {
                    owner._glPixelStorei(0x0CF2 /* GL_UNPACK_ROW_LENGTH */, pitch / 4);
                    owner._glTexSubImage2D(Texture2D, 0, rect.X, rect.Y, rect.Width, rect.Height, Rgba, UnsignedByte, data);
                    owner._glPixelStorei(0x0CF2, 0);
                }
                else
                {
                    for (int row = 0; row < rect.Height; row++)
                        owner._glTexSubImage2D(Texture2D, 0, rect.X, rect.Y + row, rect.Width, 1,
                            Rgba, UnsignedByte, data + row * pitch);
                }
            }
        }

        public void Dispose()
        {
            if (Name == 0)
                return;
            owner.DeleteTexture(Name);
            Name = 0;
        }
    }
}
