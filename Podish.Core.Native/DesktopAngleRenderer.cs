using System.Runtime.InteropServices;
using System.Text;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Core.Graphics;
using Podish.Display;
using Podish.Wayland;

namespace Podish.Core.Native;

/// <summary>
/// GLES3 ANGLE compositor renderer targeting an Apple CAMetalLayer directly via EGL.
/// Supports dynamic resize for macOS windows and iOS orientation/split-screen changes.
/// </summary>
internal sealed unsafe class DesktopAngleRenderer : IDisposable
{
    private const uint Texture2D = 0x0DE1, Rgba = 0x1908, Rgba8 = 0x8058, UnsignedByte = 0x1401;
    private const uint Framebuffer = 0x8D40, ColorAttachment0 = 0x8CE0, FramebufferComplete = 0x8CD5;
    private const uint ReadFramebuffer = 0x8CA8, DrawFramebuffer = 0x8CA9, ColorBufferBit = 0x4000;

    private const string VertexShader = """
        #version 300 es
        precision highp float;

        uniform vec2 viewport;
        uniform vec4 destination;
        out vec2 uv;
        out vec2 localPosition;
        flat out vec2 size;

        void main()
        {
            uv = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1));
            size = destination.zw;
            localPosition = uv * size;
            vec2 p = (destination.xy + localPosition) / viewport;
            gl_Position = vec4(p.x * 2.0 - 1.0, 1.0 - p.y * 2.0, 0.0, 1.0);
        }
        """;

    private const string FragmentShader = """
        #version 300 es
        precision highp float;

        uniform sampler2D image;
        uniform int mode;
        uniform vec4 color;
        uniform vec4 titleColor;
        uniform vec4 chrome;
        uniform vec2 textureOrigin;
        uniform vec4 textureBasis;
        in vec2 uv;
        in vec2 localPosition;
        flat in vec2 size;
        out vec4 pixel;

        vec3 wallpaper()
        {
            // Plasma Mobile ("Kay" / "Next" inspired) layered luminous geometric aurora wallpaper
            vec2 p = uv - vec2(0.5);
            float aspect = size.x / max(size.y, 1.0);
            vec2 q = vec2(p.x * aspect, p.y);

            // Deep twilight sky gradient (top deep navy -> mid teal-indigo -> bottom royal violet)
            vec3 topCol = vec3(0.055, 0.095, 0.215);
            vec3 midCol = vec3(0.065, 0.175, 0.295);
            vec3 botCol = vec3(0.115, 0.085, 0.245);
            vec3 col = mix(topCol, midCol, smoothstep(0.0, 0.55, uv.y));
            col = mix(col, botCol, smoothstep(0.45, 1.0, uv.y));

            // Soft Plasma cyan & magenta aurora glows
            float cyanGlow = exp(-2.4 * dot(q - vec2(-0.22 * aspect, -0.18), q - vec2(-0.22 * aspect, -0.18)));
            float tealGlow = exp(-2.8 * dot(q - vec2(0.28 * aspect, 0.05), q - vec2(0.28 * aspect, 0.05)));
            float magentaGlow = exp(-2.6 * dot(q - vec2(0.18 * aspect, 0.36), q - vec2(0.18 * aspect, 0.36)));
            col += cyanGlow * vec3(0.06, 0.24, 0.34);
            col += tealGlow * vec3(0.04, 0.22, 0.28);
            col += magentaGlow * vec3(0.24, 0.08, 0.26);

            // Layered Plasma geometric wave ridges
            float wave1 = uv.y - (0.58 + 0.075 * sin(uv.x * 3.1 + 0.4) - 0.06 * uv.x);
            float layer1 = smoothstep(-0.004, 0.004, wave1);
            vec3 ridge1 = mix(vec3(0.08, 0.26, 0.38), vec3(0.07, 0.14, 0.26), smoothstep(0.0, 0.35, wave1));
            col = mix(col, ridge1, layer1 * 0.72);
            col += exp(-55.0 * abs(wave1)) * vec3(0.18, 0.52, 0.68) * 0.55;

            float wave2 = uv.y - (0.72 + 0.065 * sin(uv.x * 2.6 + 2.3) + 0.05 * uv.x);
            float layer2 = smoothstep(-0.004, 0.004, wave2);
            vec3 ridge2 = mix(vec3(0.14, 0.13, 0.32), vec3(0.06, 0.08, 0.18), smoothstep(0.0, 0.30, wave2));
            col = mix(col, ridge2, layer2 * 0.85);
            col += exp(-50.0 * abs(wave2)) * vec3(0.45, 0.28, 0.62) * 0.50;

            // Diagonal Plasma light ribbon
            float ribbon = (uv.y - 0.32) + 0.22 * (uv.x - 0.5) + 0.05 * sin(uv.x * 4.2);
            col += exp(-12.0 * abs(ribbon)) * vec3(0.09, 0.28, 0.40) * 0.50;
            col += exp(-75.0 * abs(ribbon)) * vec3(0.22, 0.58, 0.76) * 0.38;

            // Subtle vignette + dither grain to eliminate banding
            float grain = fract(52.9829189 * fract(dot(floor(gl_FragCoord.xy), vec2(0.06711056, 0.00583715))));
            return col * (1.0 - 0.18 * smoothstep(0.35, 1.05, length(p))) + (grain - 0.5) / 255.0;
        }

        float sdRoundedBox(vec2 p, vec2 b, float r)
        {
            vec2 q = abs(p) - b + vec2(r);
            return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
        }

        float sdSegment(vec2 p, vec2 a, vec2 b)
        {
            vec2 pa = p - a, ba = b - a;
            float h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-5), 0.0, 1.0);
            return length(pa - ba * h);
        }

        vec4 windowChrome()
        {
            float radius = chrome.x;
            float left = chrome.y;
            float titleHeight = chrome.z;
            float right = chrome.w;
            vec2 p = localPosition;
            vec2 corner = vec2(radius - min(p.x, size.x - p.x), radius - p.y);
            float distanceToCorner = length(max(corner, 0.0)) - radius;
            float aa = max(0.5 * fwidth(distanceToCorner), 0.01);
            float coverage = radius > 0.0 ? 1.0 - smoothstep(-aa, aa, distanceToCorner) : 1.0;

            bool title = p.y < titleHeight && p.x >= left && p.x < right;
            return (title ? titleColor : color) * coverage;
        }

        vec4 roundedSolid()
        {
            float r = min(chrome.x, min(size.x, size.y) * 0.5);
            float d = sdRoundedBox(localPosition - size * 0.5, size * 0.5, r);
            float aa = max(0.75 * fwidth(d), 0.5);
            float cov = 1.0 - smoothstep(-aa, aa, d);
            return color * cov;
        }

        vec4 roundedTexture(vec2 sampleUv)
        {
            float r = min(chrome.x, min(size.x, size.y) * 0.5);
            float d = sdRoundedBox(localPosition - size * 0.5, size * 0.5, r);
            float aa = max(0.75 * fwidth(d), 0.5);
            float cov = (1.0 - smoothstep(-aa, aa, d)) * chrome.z;
            int fmt = int(chrome.y + 0.5);
            vec4 tex;
            if (fmt == 0)
                tex = texture(image, sampleUv).bgra;
            else if (fmt == 1)
                tex = vec4(texture(image, sampleUv).bgr, 1.0);
            else if (fmt == 2)
                tex = texture(image, sampleUv);
            else
                tex = vec4(texture(image, sampleUv).rgb, 1.0);
            return tex * cov;
        }

        vec4 vectorIcon()
        {
            int kind = int(chrome.x + 0.5);
            vec2 p = (localPosition / max(min(size.x, size.y), 1.0)) * 2.0 - vec2(size.x / max(min(size.x, size.y), 1.0), size.y / max(min(size.x, size.y), 1.0));
            float stroke = 0.12;
            float d = 10.0;

            if (kind == 0)
            {
                // Android Back (classic left-pointing triangle outline)
                vec2 v0 = vec2(-0.55, 0.0);
                vec2 v1 = vec2(0.45, -0.56);
                vec2 v2 = vec2(0.45, 0.56);
                d = min(sdSegment(p, v0, v1), min(sdSegment(p, v1, v2), sdSegment(p, v2, v0))) - stroke * 0.65;
            }
            else if (kind == 1)
            {
                // Android Home (classic circle ring)
                d = abs(length(p) - 0.54) - stroke * 0.65;
            }
            else if (kind == 2)
            {
                // Android Recents (classic rounded square outline)
                float box = sdRoundedBox(p, vec2(0.50, 0.50), 0.14);
                d = abs(box) - stroke * 0.65;
            }
            else if (kind == 3)
            {
                // Close (X) icon
                vec2 a = vec2(-0.36, -0.36), b = vec2(0.36, 0.36);
                vec2 c = vec2(0.36, -0.36), e = vec2(-0.36, 0.36);
                d = min(sdSegment(p, a, b), sdSegment(p, c, e)) - stroke * 0.68;
            }
            else if (kind == 4)
            {
                // Crisp mouse cursor arrow (white fill with dark outline, tip at top-left (0.08, 0.05))
                vec2 u = localPosition / max(min(size.x, size.y), 1.0);
                // Arrow triangle + stem in normalized [0..1] space
                vec2 a = vec2(0.08, 0.05);
                vec2 b = vec2(0.08, 0.82);
                vec2 c = vec2(0.34, 0.60);
                vec2 e = vec2(0.68, 0.58);
                // Distance to two triangles: (a,b,c) and (a,c,e)
                vec2 e0 = b - a, e1 = c - b, e2 = a - c;
                vec2 p0 = u - a, p1 = u - b, p2 = u - c;
                float s0 = sign(e0.x * e2.y - e0.y * e2.x);
                vec2 pq0 = p0 - e0 * clamp(dot(p0, e0) / dot(e0, e0), 0.0, 1.0);
                vec2 pq1 = p1 - e1 * clamp(dot(p1, e1) / dot(e1, e1), 0.0, 1.0);
                vec2 pq2 = p2 - e2 * clamp(dot(p2, e2) / dot(e2, e2), 0.0, 1.0);
                float d1 = min(min(dot(pq0, pq0), dot(pq1, pq1)), dot(pq2, pq2));
                float in1 = min(min(s0 * (p0.x * e0.y - p0.y * e0.x), s0 * (p1.x * e1.y - p1.y * e1.x)), s0 * (p2.x * e2.y - p2.y * e2.x));
                float sd1 = -sqrt(d1) * sign(in1);

                vec2 f0 = c - a, f1 = e - c, f2 = a - e;
                vec2 q0 = u - a, q1 = u - c, q2 = u - e;
                float s1 = sign(f0.x * f2.y - f0.y * f2.x);
                vec2 rq0 = q0 - f0 * clamp(dot(q0, f0) / dot(f0, f0), 0.0, 1.0);
                vec2 rq1 = q1 - f1 * clamp(dot(q1, f1) / dot(f1, f1), 0.0, 1.0);
                vec2 rq2 = q2 - f2 * clamp(dot(q2, f2) / dot(f2, f2), 0.0, 1.0);
                float d2 = min(min(dot(rq0, rq0), dot(rq1, rq1)), dot(rq2, rq2));
                float in2 = min(min(s1 * (q0.x * f0.y - q0.y * f0.x), s1 * (q1.x * f1.y - q1.y * f1.x)), s1 * (q2.x * f2.y - q2.y * f2.x));
                float sd2 = -sqrt(d2) * sign(in2);

                float stem = sdSegment(u, vec2(0.32, 0.56), vec2(0.50, 0.90)) - 0.055;
                float arrow = min(min(sd1, sd2), stem);
                float aa = max(fwidth(arrow), 0.015);
                float fillCov = 1.0 - smoothstep(-aa, aa, arrow);
                float borderCov = 1.0 - smoothstep(-aa, aa, arrow - 0.065);
                vec4 borderCol = vec4(0.05, 0.07, 0.12, 0.92);
                vec4 fillCol = color;
                return mix(borderCol * borderCov, fillCol, fillCov);
            }

            float aa = max(fwidth(d), 0.02);
            float cov = 1.0 - smoothstep(-aa, aa, d);
            return color * cov;
        }

        void main()
        {
            vec2 sampleUv = textureOrigin + textureBasis.xy * uv.x + textureBasis.zw * uv.y;
            if (mode == 0)
                pixel = color;
            else if (mode == 1)
                pixel = texture(image, sampleUv).bgra;
            else if (mode == 2)
                pixel = vec4(texture(image, sampleUv).bgr, 1.0);
            else if (mode == 3)
                pixel = vec4(wallpaper(), 1.0);
            else if (mode == 4)
                pixel = windowChrome();
            else if (mode == 5)
                pixel = texture(image, sampleUv);
            else if (mode == 6)
                pixel = vec4(texture(image, sampleUv).rgb, 1.0);
            else if (mode == 7)
                pixel = roundedSolid();
            else if (mode == 8)
                pixel = roundedTexture(sampleUv);
            else
                pixel = vectorIcon();
        }
        """;

    private readonly AngleLibrary _angle;
    private readonly KmsEglWindow _window;
    private readonly delegate* unmanaged[Cdecl]<int, uint*, void> _glGenTextures;
    private readonly delegate* unmanaged[Cdecl]<int, uint*, void> _glDeleteTextures;
    private readonly delegate* unmanaged[Cdecl]<uint, uint, void> _glBindTexture;
    private readonly delegate* unmanaged[Cdecl]<uint, uint, int, void> _glTexParameteri;
    private readonly delegate* unmanaged[Cdecl]<uint, int, int, int, int, int, uint, uint, void*, void> _glTexImage2D;
    private readonly delegate* unmanaged[Cdecl]<uint, int, int, int, int, int, uint, uint, void*, void> _glTexSubImage2D;
    private readonly delegate* unmanaged[Cdecl]<uint, int, void> _glPixelStorei;
    private readonly delegate* unmanaged[Cdecl]<uint, uint> _glCreateShader;
    private readonly delegate* unmanaged[Cdecl]<uint, int, byte**, int*, void> _glShaderSource;
    private readonly delegate* unmanaged[Cdecl]<uint, void> _glCompileShader;
    private readonly delegate* unmanaged[Cdecl]<uint, uint, int*, void> _glGetShaderiv;
    private readonly delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void> _glGetShaderInfoLog;
    private readonly delegate* unmanaged[Cdecl]<uint, void> _glDeleteShader;
    private readonly delegate* unmanaged[Cdecl]<uint> _glCreateProgram;
    private readonly delegate* unmanaged[Cdecl]<uint, uint, void> _glAttachShader;
    private readonly delegate* unmanaged[Cdecl]<uint, void> _glLinkProgram;
    private readonly delegate* unmanaged[Cdecl]<uint, uint, int*, void> _glGetProgramiv;
    private readonly delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void> _glGetProgramInfoLog;
    private readonly delegate* unmanaged[Cdecl]<uint, void> _glDeleteProgram;
    private readonly delegate* unmanaged[Cdecl]<uint, void> _glUseProgram;
    private readonly delegate* unmanaged[Cdecl]<uint, byte*, int> _glGetUniformLocation;
    private readonly delegate* unmanaged[Cdecl]<int, int, void> _glUniform1i;
    private readonly delegate* unmanaged[Cdecl]<int, float, float, void> _glUniform2f;
    private readonly delegate* unmanaged[Cdecl]<int, float, float, float, float, void> _glUniform4f;
    private readonly delegate* unmanaged[Cdecl]<int, uint*, void> _glGenFramebuffers;
    private readonly delegate* unmanaged[Cdecl]<uint, uint, void> _glBindFramebuffer;
    private readonly delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int, void> _glFramebufferTexture2D;
    private readonly delegate* unmanaged[Cdecl]<uint, uint> _glCheckFramebufferStatus;
    private readonly delegate* unmanaged[Cdecl]<int, uint*, void> _glDeleteFramebuffers;
    private readonly delegate* unmanaged[Cdecl]<int, int, int, int, void> _glViewport;
    private readonly delegate* unmanaged[Cdecl]<uint, int*, void> _glGetIntegerv;
    private readonly delegate* unmanaged[Cdecl]<uint> _glGetError;
    private readonly delegate* unmanaged[Cdecl]<uint, nint> _glGetString;
    private readonly delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int, int, uint, uint, void> _glBlitFramebuffer;
    private readonly delegate* unmanaged[Cdecl]<void> _glFlush, _glFinish;
    private readonly delegate* unmanaged[Cdecl]<uint, nint, void> _glImageTargetTexture;
    private readonly delegate* unmanaged[Cdecl]<uint, void> _glEnable;
    private readonly delegate* unmanaged[Cdecl]<uint, uint, void> _glBlendFunc;
    private readonly delegate* unmanaged[Cdecl]<uint, int, int, void> _glDrawArrays;

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
    internal bool HasPendingUploads => _uploads.Count != 0;

    private DesktopAngleRenderer(AngleLibrary angle, KmsEglWindow window)
    {
        _angle = angle;
        _window = window;
        _glGenTextures = (delegate* unmanaged[Cdecl]<int, uint*, void>)angle.Gles("glGenTextures");
        _glDeleteTextures = (delegate* unmanaged[Cdecl]<int, uint*, void>)angle.Gles("glDeleteTextures");
        _glBindTexture = (delegate* unmanaged[Cdecl]<uint, uint, void>)angle.Gles("glBindTexture");
        _glTexParameteri = (delegate* unmanaged[Cdecl]<uint, uint, int, void>)angle.Gles("glTexParameteri");
        _glTexImage2D = (delegate* unmanaged[Cdecl]<uint, int, int, int, int, int, uint, uint, void*, void>)angle.Gles("glTexImage2D");
        _glTexSubImage2D = (delegate* unmanaged[Cdecl]<uint, int, int, int, int, int, uint, uint, void*, void>)angle.Gles("glTexSubImage2D");
        _glPixelStorei = (delegate* unmanaged[Cdecl]<uint, int, void>)angle.Gles("glPixelStorei");
        _glCreateShader = (delegate* unmanaged[Cdecl]<uint, uint>)angle.Gles("glCreateShader");
        _glShaderSource = (delegate* unmanaged[Cdecl]<uint, int, byte**, int*, void>)angle.Gles("glShaderSource");
        _glCompileShader = (delegate* unmanaged[Cdecl]<uint, void>)angle.Gles("glCompileShader");
        _glGetShaderiv = (delegate* unmanaged[Cdecl]<uint, uint, int*, void>)angle.Gles("glGetShaderiv");
        _glGetShaderInfoLog = (delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void>)angle.Gles("glGetShaderInfoLog");
        _glDeleteShader = (delegate* unmanaged[Cdecl]<uint, void>)angle.Gles("glDeleteShader");
        _glCreateProgram = (delegate* unmanaged[Cdecl]<uint>)angle.Gles("glCreateProgram");
        _glAttachShader = (delegate* unmanaged[Cdecl]<uint, uint, void>)angle.Gles("glAttachShader");
        _glLinkProgram = (delegate* unmanaged[Cdecl]<uint, void>)angle.Gles("glLinkProgram");
        _glGetProgramiv = (delegate* unmanaged[Cdecl]<uint, uint, int*, void>)angle.Gles("glGetProgramiv");
        _glGetProgramInfoLog = (delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void>)angle.Gles("glGetProgramInfoLog");
        _glDeleteProgram = (delegate* unmanaged[Cdecl]<uint, void>)angle.Gles("glDeleteProgram");
        _glUseProgram = (delegate* unmanaged[Cdecl]<uint, void>)angle.Gles("glUseProgram");
        _glGetUniformLocation = (delegate* unmanaged[Cdecl]<uint, byte*, int>)angle.Gles("glGetUniformLocation");
        _glUniform1i = (delegate* unmanaged[Cdecl]<int, int, void>)angle.Gles("glUniform1i");
        _glUniform2f = (delegate* unmanaged[Cdecl]<int, float, float, void>)angle.Gles("glUniform2f");
        _glUniform4f = (delegate* unmanaged[Cdecl]<int, float, float, float, float, void>)angle.Gles("glUniform4f");
        _glGenFramebuffers = (delegate* unmanaged[Cdecl]<int, uint*, void>)angle.Gles("glGenFramebuffers");
        _glBindFramebuffer = (delegate* unmanaged[Cdecl]<uint, uint, void>)angle.Gles("glBindFramebuffer");
        _glFramebufferTexture2D = (delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int, void>)angle.Gles("glFramebufferTexture2D");
        _glCheckFramebufferStatus = (delegate* unmanaged[Cdecl]<uint, uint>)angle.Gles("glCheckFramebufferStatus");
        _glDeleteFramebuffers = (delegate* unmanaged[Cdecl]<int, uint*, void>)angle.Gles("glDeleteFramebuffers");
        _glViewport = (delegate* unmanaged[Cdecl]<int, int, int, int, void>)angle.Gles("glViewport");
        _glGetIntegerv = (delegate* unmanaged[Cdecl]<uint, int*, void>)angle.Gles("glGetIntegerv");
        _glGetError = (delegate* unmanaged[Cdecl]<uint>)angle.Gles("glGetError");
        _glGetString = (delegate* unmanaged[Cdecl]<uint, nint>)angle.Gles("glGetString");
        _glBlitFramebuffer = (delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int, int, uint, uint, void>)angle.Gles("glBlitFramebuffer");
        _glFlush = (delegate* unmanaged[Cdecl]<void>)angle.Gles("glFlush");
        _glFinish = (delegate* unmanaged[Cdecl]<void>)angle.Gles("glFinish");
        var getProc = (delegate* unmanaged[Cdecl]<byte*, nint>)angle.Egl("eglGetProcAddress");
        fixed (byte* name = "glEGLImageTargetTexture2DOES\0"u8)
            _glImageTargetTexture = (delegate* unmanaged[Cdecl]<uint, nint, void>)getProc(name);
        _glEnable = (delegate* unmanaged[Cdecl]<uint, void>)angle.Gles("glEnable");
        _glBlendFunc = (delegate* unmanaged[Cdecl]<uint, uint, void>)angle.Gles("glBlendFunc");
        _glDrawArrays = (delegate* unmanaged[Cdecl]<uint, int, int, void>)angle.Gles("glDrawArrays");
    }

    internal static DesktopAngleRenderer Create(string angleDirectory, KmsEglWindow window, VirtualDrmScanout scanout,
        DisplaySize logicalSize, bool enableGles, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(angleDirectory))
            throw new InvalidOperationException("ANGLE library directory is required.");

        var angle = AngleLibrary.Acquire(Path.GetFullPath(angleDirectory), window, hardwareCompositor: true);
        DesktopAngleRenderer? renderer = null;
        try
        {
            renderer = new DesktopAngleRenderer(angle, window);
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
        RequireEgl(_surface != 0, "create the Metal EGL surface");
        nint shareContext = 0;
        if (enableGles)
        {
            // Share textures directly with KmsEglOutput because ANGLE Metal does not
            // implement EGL_KHR_gl_texture_2D_image for eglCreateImage(EGL_GL_TEXTURE_2D).
            GuestTextures = new KmsEglOutput(_angle, window: null, logger);
            shareContext = GuestTextures.TextureContext();
        }
        int* contextArgs = stackalloc int[] { 0x3098, 3, 0x3038 };
        _context = _angle.CreateContext(_angle.Display, config, shareContext, contextArgs);
        RequireEgl(_context != 0, "create a GLES3 context");
        RequireEgl(_angle.MakeCurrent(_angle.Display, _surface, _surface, _context) != 0, "bind the GLES3 context");

        int version = 0;
        _glGetIntegerv(0x821B, &version);
        string renderer = Marshal.PtrToStringUTF8(_glGetString(0x1F01)) ?? "";
        if (version < 3 || renderer.Length == 0)
            throw new InvalidOperationException($"A GLES3 ANGLE renderer is required. Reported '{renderer}' (v{version}).");

        var interval = (delegate* unmanaged[Cdecl]<nint, int, uint>)_angle.Egl("eglSwapInterval");
        interval(_angle.Display, 0);

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

        if (GuestTextures != null)
        {
            _glGenFramebuffers(1, &framebuffer);
            _importFramebuffer = framebuffer;
        }

        _glEnable(0x0BE2 /* GL_BLEND */);
        _glBlendFunc(1 /* GL_ONE */, 0x0303 /* GL_ONE_MINUS_SRC_ALPHA */);
        CheckGl("initialize the compositor");
        scanout.SetGpuBuffers(_scanout[0], _scanout[1]);
        logger.LogInformation("Apple Wayland ANGLE compositor: {Renderer}, {Version}", renderer,
            Marshal.PtrToStringUTF8(_glGetString(0x1F02)));
    }

    internal void Resize(DisplaySize logicalSize, DisplaySize pixelSize)
    {
        if (logicalSize.Width <= 0 || logicalSize.Height <= 0 || pixelSize.Width <= 0 || pixelSize.Height <= 0)
            return;
        if (_logicalSize == logicalSize && _size == pixelSize)
            return;

        _logicalSize = logicalSize;
        if (_size != pixelSize)
        {
            _size = pixelSize;
            foreach (var buffer in _scanout)
            {
                _glBindTexture(Texture2D, buffer.Name);
                _glTexImage2D(Texture2D, 0, (int)Rgba8, pixelSize.Width, pixelSize.Height, 0, Rgba, UnsignedByte, null);
                buffer.CurrentSize = pixelSize;
            }
            _glBindTexture(Texture2D, 0);
            CheckGl("resize scanout textures");
        }
    }

    internal bool CanImport(DrmScanoutBuffer buffer) =>
        GuestTextures != null && buffer is KmsEglOutput.TextureBo texture && texture.Owner == GuestTextures;

    internal bool UploadGpuBuffer(IDisplayTexture target, DrmScanoutBuffer buffer, bool yInverted, ulong lease)
    {
        var destination = (Texture)target;
        var source = (KmsEglOutput.TextureBo)buffer;
        if (!source.WaitForProducer())
        {
            _logger?.LogWarning("UploadGpuBuffer failed: producer fence wait failed (eglError=0x{Err:X})", _angle.GetError());
            return false;
        }

        try
        {
            _glBindFramebuffer(ReadFramebuffer, _importFramebuffer);
            _glFramebufferTexture2D(ReadFramebuffer, ColorAttachment0, Texture2D, source.Texture, 0);
            _glBindFramebuffer(DrawFramebuffer, _framebuffer);
            _glFramebufferTexture2D(DrawFramebuffer, ColorAttachment0, Texture2D, destination.Name, 0);

            uint readStatus = _glCheckFramebufferStatus(ReadFramebuffer);
            uint drawStatus = _glCheckFramebufferStatus(DrawFramebuffer);
            uint preErr = _glGetError();
            if (preErr != 0 || readStatus != FramebufferComplete || drawStatus != FramebufferComplete)
            {
                _logger?.LogWarning("UploadGpuBuffer failed: glErr=0x{Err:X} readStatus=0x{R:X} drawStatus=0x{D:X}",
                    preErr, readStatus, drawStatus);
                return false;
            }

            int width = destination.Width, height = destination.Height;
            _glBlitFramebuffer(0, yInverted ? height : 0, width, yInverted ? 0 : height,
                0, 0, width, height, ColorBufferBit, 0x2600);
            uint blitErr = _glGetError();
            if (blitErr != 0)
            {
                _logger?.LogWarning("UploadGpuBuffer blit failed: glErr=0x{Err:X}", blitErr);
                return false;
            }
            destination.NativeRgba = true;
        }
        finally
        {
            _glFramebufferTexture2D(ReadFramebuffer, ColorAttachment0, Texture2D, 0, 0);
            _glFramebufferTexture2D(DrawFramebuffer, ColorAttachment0, Texture2D, 0, 0);
            _glBindFramebuffer(ReadFramebuffer, 0);
        }

        var create = (delegate* unmanaged[Cdecl]<nint, uint, nint*, nint>)_angle.Egl("eglCreateSync");
        nint fence = create(_angle.Display, 0x30F9, null);
        if (fence == 0)
            _glFinish();
        else
            _glFlush();
        buffer.Retain();
        _uploads.Enqueue((fence, lease, buffer));
        return true;
    }

    internal void TakeCompletedUploads(List<ulong> consumed)
    {
        if (_uploads.Count == 0)
            return;
        var wait = (delegate* unmanaged[Cdecl]<nint, nint, int, ulong, int>)_angle.Egl("eglClientWaitSync");
        while (_uploads.TryPeek(out var upload))
        {
            if (upload.Fence != 0)
            {
                int status = wait(_angle.Display, upload.Fence, 0, 0);
                if (status == 0x30F5)
                    return;
                RequireEgl(status == 0x30F6, "wait for client GPU buffer copy");
                DestroyFence(upload.Fence);
            }
            _uploads.Dequeue();
            upload.Buffer.Release();
            consumed.Add(upload.Lease);
        }
    }

    private void DestroyFence(nint fence)
    {
        if (fence == 0) return;
        var destroy = (delegate* unmanaged[Cdecl]<nint, nint, uint>)_angle.Egl("eglDestroySync");
        destroy(_angle.Display, fence);
    }

    private void BindScanout(ScanoutBuffer buffer)
    {
        _glBindFramebuffer(Framebuffer, _framebuffer);
        _glFramebufferTexture2D(Framebuffer, ColorAttachment0, Texture2D, buffer.Name, 0);
    }

    internal void BeginScene(DrmScanoutBuffer buffer)
    {
        var scanout = (ScanoutBuffer)buffer;
        BindScanout(scanout);
        var s = scanout.CurrentSize;
        _glViewport(0, 0, s.Width, s.Height);
        _glUniform2f(_viewport, s.Width, s.Height);
        Draw(3, new DisplayRect(0, 0, s.Width, s.Height));
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
        if (query(_angle.Display, _surface, 0x3057, &width) == 0 ||
            query(_angle.Display, _surface, 0x3056, &height) == 0)
            return false;
        if (width <= 0 || height <= 0)
            return true;

        var previewSize = new DisplaySize(width, height);
        if (_previewSize != previewSize)
        {
            _previewSize = previewSize;
            _logger?.LogInformation("Wayland EGL Metal drawable {Width}x{Height} for {ScanoutWidth}x{ScanoutHeight} scanout",
                width, height, scanout.CurrentSize.Width, scanout.CurrentSize.Height);
        }

        BindScanout(scanout);
        _glBindFramebuffer(DrawFramebuffer, 0);
        var srcSize = scanout.CurrentSize;
        uint filter = Math.Abs(width - srcSize.Width) <= 1 && Math.Abs(height - srcSize.Height) <= 1
            ? 0x2600u : 0x2601u;
        _glBlitFramebuffer(0, 0, srcSize.Width, srcSize.Height, 0, 0, width, height, ColorBufferBit, filter);
        CheckGl("blit scanout framebuffer");
        if (_angle.SwapBuffers(_angle.Display, _surface) == 0)
            return false;
        return true;
    }

    internal void DrawChrome(DisplayRect bounds, DisplayColor border, DisplayColor title, int radius, int inset, int titleHeight)
    {
        var pixels = GetPixelBounds(bounds);
        var titlePixels = GetPixelBounds(new DisplayRect(bounds.X + inset, bounds.Y, bounds.Width - 2 * inset, titleHeight));
        SetColor(_color, border);
        SetColor(_titleColor, title);
        _glUniform4f(_chrome, radius * (float)_size.Height / Math.Max(1, _logicalSize.Height),
            titlePixels.X - pixels.X, titlePixels.Height, titlePixels.X + titlePixels.Width - pixels.X);
        Draw(4, pixels);
    }

    internal void FillRect(DisplayRect rect, DisplayColor color)
    {
        SetColor(_color, color);
        Draw(0, GetPixelBounds(rect));
    }

    internal void DrawRoundedRect(DisplayRect rect, DisplayColor color, float radiusLogical)
    {
        var pixels = GetPixelBounds(rect);
        if (pixels.Width <= 0 || pixels.Height <= 0) return;
        float radiusPx = radiusLogical * (float)_size.Height / Math.Max(1, _logicalSize.Height);
        SetColor(_color, color);
        _glUniform4f(_chrome, radiusPx, 0, 0, 0);
        Draw(7, pixels);
    }

    internal void BlitRounded(IDisplayTexture texture, DisplayRect destination, float radiusLogical, float alpha = 1f,
        WaylandSurfaceMapping? mapping = null)
    {
        var pixels = GetPixelBounds(destination);
        if (pixels.Width <= 0 || pixels.Height <= 0 || alpha <= 0.001f) return;
        var image = (Texture)texture;
        var sample = mapping ?? new WaylandSurfaceMapping(0, 0, 0, 0, 1, 0, 0, 1);
        float radiusPx = radiusLogical * (float)_size.Height / Math.Max(1, _logicalSize.Height);
        int fmt = image.NativeRgba
            ? (image.Format == DisplayPixelFormat.Argb8888 ? 2 : 3)
            : (image.Format == DisplayPixelFormat.Argb8888 ? 0 : 1);
        _glUniform2f(_textureOrigin, sample.U, sample.V);
        _glUniform4f(_textureBasis, sample.Ux, sample.Vx, sample.Uy, sample.Vy);
        _glUniform4f(_chrome, radiusPx, fmt, Math.Clamp(alpha, 0f, 1f), 0);
        _glBindTexture(Texture2D, image.Name);
        Draw(8, pixels);
    }

    internal void DrawVectorIcon(DisplayRect rect, int iconKind, DisplayColor color)
    {
        var pixels = GetPixelBounds(rect);
        if (pixels.Width <= 0 || pixels.Height <= 0) return;
        SetColor(_color, color);
        _glUniform4f(_chrome, iconKind, 0, 0, 0);
        Draw(9, pixels);
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

    internal DisplayRect GetPixelBounds(DisplayRect logical)
    {
        static int Snap(long edge, int source, int target) =>
            (int)Math.Clamp(Math.Floor((double)edge * target / Math.Max(1, source) + 0.5), int.MinValue, int.MaxValue);

        int left = Snap(logical.X, _logicalSize.Width, _size.Width);
        int top = Snap(logical.Y, _logicalSize.Height, _size.Height);
        int right = Snap((long)logical.X + logical.Width, _logicalSize.Width, _size.Width);
        int bottom = Snap((long)logical.Y + logical.Height, _logicalSize.Height, _size.Height);
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
        int filter = nearest ? 0x2600 : 0x2601;
        _glTexParameteri(Texture2D, 0x2801, filter);
        _glTexParameteri(Texture2D, 0x2800, filter);
        _glTexParameteri(Texture2D, 0x2802, 0x812F);
        _glTexParameteri(Texture2D, 0x2803, 0x812F);
        _glTexImage2D(Texture2D, 0, (int)Rgba8, width, height, 0, Rgba, UnsignedByte, null);
        return texture;
    }

    private void DeleteTexture(uint texture) => _glDeleteTextures(1, &texture);

    private uint BuildProgram()
    {
        uint vertex = Compile(0x8B31, VertexShader);
        uint fragment = 0;
        uint program = 0;
        try
        {
            fragment = Compile(0x8B30, FragmentShader);
            program = _glCreateProgram();
            _glAttachShader(program, vertex);
            _glAttachShader(program, fragment);
            _glLinkProgram(program);
            int linked;
            _glGetProgramiv(program, 0x8B82, &linked);
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
        _glGetShaderiv(shader, 0x8B81, &compiled);
        if (compiled != 0) return shader;
        byte* log = stackalloc byte[2048];
        int logLength;
        _glGetShaderInfoLog(shader, 2048, &logLength, log);
        _glDeleteShader(shader);
        throw new InvalidOperationException($"Compositor shader compile failed: {Encoding.UTF8.GetString(log, logLength)}");
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
            _angle.MakeCurrent(_angle.Display, _surface, _surface, _context);
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

    private sealed class ScanoutBuffer(DesktopAngleRenderer owner, uint name, DisplaySize size)
        : DrmScanoutBuffer((uint)size.Width, (uint)size.Height, (uint)size.Width * 4)
    {
        internal uint Name => name;
        internal DisplaySize CurrentSize { get; set; } = size;
        public override bool Ready => true;
        public override bool Present() => owner.Present(this);
        protected override void Destroy() => owner.DeleteTexture(name);
    }

    private sealed class Texture(DesktopAngleRenderer owner, uint name, DisplayTextureDescriptor descriptor) : IDisplayTexture
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
                    owner._glPixelStorei(0x0CF2, pitch / 4);
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
            if (Name == 0) return;
            owner.DeleteTexture(Name);
            Name = 0;
        }
    }
}
