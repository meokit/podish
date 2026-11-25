using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Podish.Core.Graphics;

/// <summary>Samples an ANGLE pbuffer into a native window without copying pixels to the CPU.</summary>
internal sealed unsafe partial class KmsEglOutput : IDisposable
{
    private const int BackBuffer = 0x3084;
    private const int Texture2D = 0x0DE1;
    private static readonly Lazy<nint> WslD3d12Core = new(() =>
        NativeLibrary.TryLoad("/usr/lib/wsl/lib/libd3d12core.so", out var handle) ? handle : 0);
    private readonly AngleLibrary _angle;
    private readonly KmsEglWindow _window;
    private readonly ILogger _logger;
    internal readonly nint Config;
    private nint _surface;
    private nint _context;

    private readonly delegate* unmanaged[Cdecl]<nint, nint, int, uint> _bindTexture;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, int, uint> _releaseTexture;
    private readonly delegate* unmanaged[Cdecl]<nint, uint, nint*, nint> _createSync;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, int, uint> _waitSync;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, int, ulong, int> _clientWait;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, uint> _destroySync;
    private readonly delegate* unmanaged[Cdecl]<int, nint> _getSurface;
    private readonly delegate* unmanaged[Cdecl]<nint> _getContext;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, int, int*, uint> _querySurface;

    internal KmsEglOutput(AngleLibrary angle, KmsEglWindow? window, ILogger logger)
    {
        _angle = angle;
        _window = window.GetValueOrDefault();
        _logger = logger;
        _bindTexture = (delegate* unmanaged[Cdecl]<nint, nint, int, uint>)angle.Egl("eglBindTexImage");
        _releaseTexture = (delegate* unmanaged[Cdecl]<nint, nint, int, uint>)angle.Egl("eglReleaseTexImage");
        _createSync = (delegate* unmanaged[Cdecl]<nint, uint, nint*, nint>)angle.Egl("eglCreateSync");
        _waitSync = (delegate* unmanaged[Cdecl]<nint, nint, int, uint>)angle.Egl("eglWaitSync");
        _clientWait = (delegate* unmanaged[Cdecl]<nint, nint, int, ulong, int>)angle.Egl("eglClientWaitSync");
        _destroySync = (delegate* unmanaged[Cdecl]<nint, nint, uint>)angle.Egl("eglDestroySync");
        _getSurface = (delegate* unmanaged[Cdecl]<int, nint>)angle.Egl("eglGetCurrentSurface");
        _getContext = (delegate* unmanaged[Cdecl]<nint>)angle.Egl("eglGetCurrentContext");
        _querySurface = (delegate* unmanaged[Cdecl]<nint, nint, int, int*, uint>)angle.Egl("eglQuerySurface");

        if (window == null)
        {
            Config = angle.Config;
            Require(TextureContext() != 0, "create a GLES3 context for virtual GBM buffers");
            return;
        }

        var choose = (delegate* unmanaged[Cdecl]<nint, int*, nint*, int, int*, uint>)angle.Egl("eglChooseConfig");
        int* attributes = stackalloc int[]
        {
            0x3033, 0x0005, // WINDOW_BIT | PBUFFER_BIT
            0x303A, 1, // BIND_TO_TEXTURE_RGBA
            0x3040, AngleLibrary.Es2Bit | AngleLibrary.Es3Bit,
            0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, 0x3025, 24, 0x3026, 8, 0x3038
        };
        int count;
        nint config;
        Require(choose(angle.Display, attributes, &config, 1, &count) != 0 && count > 0,
            "choose a texture-bindable EGL window and pbuffer config");
        Config = config;

        try
        {
            var createWindow = (delegate* unmanaged[Cdecl]<nint, nint, nint, int*, nint>)angle.Egl("eglCreateWindowSurface");
            _surface = createWindow(angle.Display, Config, _window.Handle, null);
            int* contextArgs = stackalloc int[] { 0x3098, 2, 0x3483, 0, 0x3038 };
            _context = angle.CreateContext(angle.Display, Config, 0, contextArgs);
            Require(_surface != 0 && _context != 0 &&
                    angle.MakeCurrent(angle.Display, _surface, _surface, _context) != 0,
                "bind the KMS output EGL window");
            // The KMS timer controls frame cadence. Avoid a second vsync wait at EGL swap.
            var interval = (delegate* unmanaged[Cdecl]<nint, int, uint>)angle.Egl("eglSwapInterval");
            interval(angle.Display, 0);
            BuildPipeline();
            var fence = Fence();
            Require(fence != 0, "initialize EGL fence synchronization");
            DestroyFence(fence);
            var glGetString = (delegate* unmanaged[Cdecl]<uint, nint>)Gl("glGetString");
            var renderer = Marshal.PtrToStringUTF8(glGetString(0x1F01 /* GL_RENDERER */));
            // WSL's D3D12 runtime can leave a pthread destructor on this render
            // thread after Dozen closes the module. Keep the system module mapped
            // until process exit so that destructor still has valid code.
            if (OperatingSystem.IsLinux() && renderer?.Contains("Dozen", StringComparison.OrdinalIgnoreCase) == true)
                _ = WslD3d12Core.Value;
            _logger.LogInformation("KMS EGL renderer: {Renderer}", renderer);
            Require(angle.MakeCurrent(angle.Display, 0, 0, 0) != 0, "unbind KMS output EGL context");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal nint CreatePbuffer(int width, int height)
    {
        int* attributes = stackalloc int[] { 0x3057, width, 0x3056, height, 0x3080, 0x305E, 0x3081, 0x305F, 0x3038 };
        return _angle.CreatePbufferSurface(_angle.Display, Config, attributes);
    }

    internal bool InitializePbuffers(ReadOnlySpan<nint> buffers)
    {
        var previousContext = _getContext();
        var draw = _getSurface(0x3059); // EGL_DRAW
        var read = _getSurface(0x305A); // EGL_READ
        var clear = (delegate* unmanaged[Cdecl]<uint, void>)Gl("glClear");
        try
        {
            // SDL KMSDRM can recreate its GBM surface inside SwapWindow and immediately
            // submit it without drawing. Clear with the private context's default color
            // and write masks so the new buffers cannot expose uninitialized pixels.
            foreach (var buffer in buffers)
            {
                if (_angle.MakeCurrent(_angle.Display, buffer, buffer, _context) == 0) return false;
                clear(0x4000 /* GL_COLOR_BUFFER_BIT */);
            }
            // This context owns the clears; wait once per new pool before handing it to guest contexts.
            ((delegate* unmanaged[Cdecl]<void>)Gl("glFinish"))();
            return GlError() == 0;
        }
        finally
        {
            Require(_angle.MakeCurrent(_angle.Display, draw, read, previousContext) != 0, "restore guest EGL binding");
        }
    }

    internal nint Fence()
    {
        var fence = _createSync(_angle.Display, 0x30F9 /* EGL_SYNC_FENCE */, null);
        if (fence != 0) ((delegate* unmanaged[Cdecl]<void>)Gl("glFlush"))();
        return fence;
    }

    internal void DestroyFence(nint fence)
    {
        if (fence != 0) _destroySync(_angle.Display, fence);
    }

    internal bool Present(nint buffer, nint producerFence)
    {
        if (_window.CanPresent?.Invoke() == false) return true;
        var previousContext = _getContext();
        var draw = _getSurface(0x3059); // EGL_DRAW
        var read = _getSurface(0x305A); // EGL_READ
        Require(_angle.MakeCurrent(_angle.Display, _surface, _surface, _context) != 0, "bind KMS output");
        bool bound = false;
        try
        {
            if (producerFence != 0)
                Require(_waitSync(_angle.Display, producerFence, 0) != 0, "wait for game rendering");
            Require(_bindTexture(_angle.Display, buffer, BackBuffer) != 0, "bind GBM pbuffer as a texture");
            bound = true;
            int width = 0, height = 0;
            Require(_querySurface(_angle.Display, _surface, 0x3057, &width) != 0 &&
                    _querySurface(_angle.Display, _surface, 0x3056, &height) != 0, "query KMS output size");
            ((delegate* unmanaged[Cdecl]<int, int, int, int, void>)Gl("glViewport"))(0, 0, width, height);
            ((delegate* unmanaged[Cdecl]<uint, int, int, void>)Gl("glDrawArrays"))(4, 0, 3);
            var error = ((delegate* unmanaged[Cdecl]<uint>)Gl("glGetError"))();
            if (error != 0) throw new InvalidOperationException($"KMS texture draw failed (GL error 0x{error:X}).");
            var fence = Fence();
            Require(fence != 0, "create KMS consumer fence");
            try
            {
                Require(_angle.SwapBuffers(_angle.Display, _surface) != 0, "swap KMS output");
                // Keep the texture bound until the GPU is done sampling the selected BO.
                if (_clientWait(_angle.Display, fence, 0, 250_000_000) != 0x30F6)
                    throw new InvalidOperationException("The ANGLE KMS output fence did not complete.");
            }
            finally
            {
                DestroyFence(fence);
            }
            _logger.LogDebug("KMS EGL presented a GPU frame {Width}x{Height}", width, height);
            return true;
        }
        finally
        {
            try
            {
                if (bound) Require(_releaseTexture(_angle.Display, buffer, BackBuffer) != 0, "release GBM texture");
            }
            finally
            {
                Require(_angle.MakeCurrent(_angle.Display, draw, read, previousContext) != 0,
                    "restore guest EGL binding");
            }
        }
    }

    private nint Gl(string name) => _angle.Gl[Array.IndexOf(AngleDevice.GlNames, name)];

    private void BuildPipeline()
    {
        const uint ArrayBuffer = 0x8892;
        var vertex = Compile(0x8B31, "attribute vec2 p; varying vec2 uv; uniform float ySign; void main(){gl_Position=vec4(p,0.,1.);uv=vec2(p.x,p.y*ySign)*.5+.5;}"u8);
        var fragment = Compile(0x8B30, "precision mediump float; varying vec2 uv; uniform sampler2D image; void main(){gl_FragColor=vec4(texture2D(image,uv).rgb,1.);}"u8);
        var program = ((delegate* unmanaged[Cdecl]<uint>)Gl("glCreateProgram"))();
        var attach = (delegate* unmanaged[Cdecl]<uint, uint, void>)Gl("glAttachShader");
        attach(program, vertex);
        attach(program, fragment);
        fixed (byte* name = "p\0"u8)
            ((delegate* unmanaged[Cdecl]<uint, uint, byte*, void>)Gl("glBindAttribLocation"))(program, 0, name);
        ((delegate* unmanaged[Cdecl]<uint, void>)Gl("glLinkProgram"))(program);
        int linked;
        ((delegate* unmanaged[Cdecl]<uint, uint, int*, void>)Gl("glGetProgramiv"))(program, 0x8B82, &linked);
        var deleteShader = (delegate* unmanaged[Cdecl]<uint, void>)Gl("glDeleteShader");
        deleteShader(vertex);
        deleteShader(fragment);
        if (linked == 0) throw new InvalidOperationException("Could not link the ANGLE KMS presentation shader.");
        ((delegate* unmanaged[Cdecl]<uint, void>)Gl("glUseProgram"))(program);
        // ANGLE Vulkan flips the default framebuffer for GLES, but eglBindTexImage
        // exposes the underlying pbuffer image directly. Only the texture's Y needs correction.
        fixed (byte* name = "ySign\0"u8)
        {
            var location = ((delegate* unmanaged[Cdecl]<uint, byte*, int>)Gl("glGetUniformLocation"))(program, name);
            ((delegate* unmanaged[Cdecl]<int, float, void>)Gl("glUniform1f"))(location, _angle.UsesVulkan ? -1f : 1f);
        }

        uint vertexBuffer, texture;
        ((delegate* unmanaged[Cdecl]<int, uint*, void>)Gl("glGenBuffers"))(1, &vertexBuffer);
        ((delegate* unmanaged[Cdecl]<uint, uint, void>)Gl("glBindBuffer"))(ArrayBuffer, vertexBuffer);
        float* vertices = stackalloc float[] { -1, -1, 3, -1, -1, 3 };
        ((delegate* unmanaged[Cdecl]<uint, nint, void*, uint, void>)Gl("glBufferData"))
            (ArrayBuffer, sizeof(float) * 6, vertices, 0x88E4);
        ((delegate* unmanaged[Cdecl]<uint, int, uint, byte, int, nint, void>)Gl("glVertexAttribPointer"))
            (0, 2, 0x1406, 0, 0, 0);
        ((delegate* unmanaged[Cdecl]<uint, void>)Gl("glEnableVertexAttribArray"))(0);

        ((delegate* unmanaged[Cdecl]<int, uint*, void>)Gl("glGenTextures"))(1, &texture);
        ((delegate* unmanaged[Cdecl]<uint, uint, void>)Gl("glBindTexture"))(Texture2D, texture);
        var parameter = (delegate* unmanaged[Cdecl]<uint, uint, int, void>)Gl("glTexParameteri");
        parameter(Texture2D, 0x2800, 0x2601);
        parameter(Texture2D, 0x2801, 0x2601);
        parameter(Texture2D, 0x2802, 0x812F);
        parameter(Texture2D, 0x2803, 0x812F);
    }

    private uint Compile(uint type, ReadOnlySpan<byte> text)
    {
        var shader = ((delegate* unmanaged[Cdecl]<uint, uint>)Gl("glCreateShader"))(type);
        var size = text.Length;
        fixed (byte* data = text)
        {
            var pointer = data;
            ((delegate* unmanaged[Cdecl]<uint, int, byte**, int*, void>)Gl("glShaderSource"))
                (shader, 1, &pointer, &size);
        }
        ((delegate* unmanaged[Cdecl]<uint, void>)Gl("glCompileShader"))(shader);
        int compiled;
        ((delegate* unmanaged[Cdecl]<uint, uint, int*, void>)Gl("glGetShaderiv"))(shader, 0x8B81, &compiled);
        if (compiled == 0) throw new InvalidOperationException("Could not compile the ANGLE KMS presentation shader.");
        return shader;
    }

    private void Require(bool condition, string operation)
    {
        if (!condition)
            throw new InvalidOperationException($"Could not {operation} (EGL error 0x{_angle.GetError():X}).");
    }

    public void Dispose()
    {
        using (_angle.EnterDisplayLock())
        {
            // The context has its own share group and owns the pipeline objects.
            _angle.MakeCurrent(_angle.Display, 0, 0, 0);
            if (_textureContext != 0) _angle.DestroyContext(_angle.Display, _textureContext);
            if (_context != 0) _angle.DestroyContext(_angle.Display, _context);
            if (_surface != 0) _angle.DestroySurface(_angle.Display, _surface);
            _textureContext = _context = _surface = 0;
        }
    }
}
