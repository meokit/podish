using System.Runtime.InteropServices;

namespace Podish.Core.Graphics;

/// <summary>Links the host ABI of ANGLE's EGL and GLES libraries.</summary>
internal sealed unsafe class AngleLibrary
{
    private static readonly Lock Gate = new();
    private static AngleLibrary? _active;
    private int _users;
    private readonly string _directory;
    private readonly nint _egl;
    private readonly nint _gles;
    private readonly nint _nativeDisplay;
    private readonly int _nativePlatform;

    internal readonly nint Display;
    internal readonly nint Config;
    internal readonly bool SupportsEs3;
    internal const int Es2Bit = 0x0004, Es3Bit = 0x0040;
    internal readonly nint[] Gl;
    internal readonly nint MapBufferRange;
    internal readonly nint UnmapBuffer;
    internal readonly nint GetBufferPointer;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, nint, int*, nint> CreateContext;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, uint> DestroyContext;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, int*, nint> CreatePbufferSurface;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, uint> DestroySurface;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint> MakeCurrent;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, uint> SwapBuffers;
    internal readonly delegate* unmanaged[Cdecl]<int> GetError;

    internal static AngleLibrary Acquire(string directory, KmsEglWindow? window = null)
    {
        lock (Gate)
        {
            if (_active == null)
                _active = new AngleLibrary(directory, window);
            else if (_active._directory != directory ||
                     (window is { } requested && (_active._nativeDisplay != requested.Display ||
                                                 _active._nativePlatform != requested.Platform)))
                throw new InvalidOperationException("Active Podish containers must use the same ANGLE directory and native display.");
            _active._users++;
            return _active;
        }
    }

    internal void Release()
    {
        lock (Gate)
        {
            if (--_users != 0) return;
            Terminate(Display);
            NativeLibrary.Free(_gles);
            NativeLibrary.Free(_egl);
            _active = null;
        }
    }

    private AngleLibrary(string directory, KmsEglWindow? window)
    {
        _directory = directory;
        _nativeDisplay = window?.Display ?? 0;
        _nativePlatform = window?.Platform ?? 0;
        var eglName = OperatingSystem.IsMacOS() ? "libEGL.dylib" :
            OperatingSystem.IsWindows() ? "libEGL.dll" : "libEGL.so";
        var glesName = OperatingSystem.IsMacOS() ? "libGLESv2.dylib" :
            OperatingSystem.IsWindows() ? "libGLESv2.dll" : "libGLESv2.so";
        try
        {
            _egl = NativeLibrary.Load(Path.Combine(directory, eglName));
            _gles = NativeLibrary.Load(Path.Combine(directory, glesName));
            CreateContext = (delegate* unmanaged[Cdecl]<nint, nint, nint, int*, nint>)Egl("eglCreateContext");
            DestroyContext = (delegate* unmanaged[Cdecl]<nint, nint, uint>)Egl("eglDestroyContext");
            CreatePbufferSurface = (delegate* unmanaged[Cdecl]<nint, nint, int*, nint>)Egl("eglCreatePbufferSurface");
            DestroySurface = (delegate* unmanaged[Cdecl]<nint, nint, uint>)Egl("eglDestroySurface");
            MakeCurrent = (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint>)Egl("eglMakeCurrent");
            SwapBuffers = (delegate* unmanaged[Cdecl]<nint, nint, uint>)Egl("eglSwapBuffers");
            GetError = (delegate* unmanaged[Cdecl]<int>)Egl("eglGetError");
            Gl = new nint[AngleDevice.GlNames.Length];
            for (var i = 0; i < Gl.Length; i++)
            {
                var name = AngleDevice.GlNames[i];
                Gl[i] = i < AngleDevice.GlEs3First - AngleDevice.GlFirst
                    ? NativeLibrary.GetExport(_gles, name)
                    : NativeLibrary.TryGetExport(_gles, name, out var entry) ? entry : 0;
            }

            var getProc = (delegate* unmanaged[Cdecl]<byte*, nint>)Egl("eglGetProcAddress");
            fixed (byte* map = "glMapBufferRangeEXT\0"u8, unmap = "glUnmapBufferOES\0"u8,
                         pointer = "glGetBufferPointervOES\0"u8)
            {
                MapBufferRange = getProc(map);
                UnmapBuffer = getProc(unmap);
                GetBufferPointer = getProc(pointer);
            }
            fixed (byte* name = "eglGetPlatformDisplayEXT\0"u8)
            {
                var platformDisplay = (delegate* unmanaged[Cdecl]<uint, nint, int*, nint>)getProc(name);
                if (platformDisplay == null)
                    throw new InvalidOperationException("ANGLE does not export eglGetPlatformDisplayEXT.");
                // KMS scanout needs a window config that supports texture-bound
                // pbuffers and EGL fence sync. ANGLE's default D3D11 (Windows)
                // and GLX (Linux/X11) backends do not provide both.
                int* attribs = stackalloc int[5];
                var i = 0;
                if (OperatingSystem.IsMacOS())
                {
                    attribs[i++] = 0x3203; // EGL_PLATFORM_ANGLE_TYPE_ANGLE
                    attribs[i++] = 0x3489; // Metal
                }
                else if (window != null && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
                {
                    attribs[i++] = 0x3203;
                    attribs[i++] = 0x3450; // Vulkan
                }
                if (_nativePlatform != 0)
                {
                    attribs[i++] = 0x348F; // EGL_PLATFORM_ANGLE_NATIVE_PLATFORM_TYPE_ANGLE
                    attribs[i++] = _nativePlatform;
                }
                attribs[i] = 0x3038;
                Display = platformDisplay(0x3202, _nativeDisplay, attribs);
            }

            var initialize = (delegate* unmanaged[Cdecl]<nint, int*, int*, uint>)Egl("eglInitialize");
            if (Display == 0 || initialize(Display, null, null) == 0)
                throw new InvalidOperationException($"ANGLE eglInitialize failed (0x{GetError():X}).");

            var select = (delegate* unmanaged[Cdecl]<nint, int*, nint*, int, int*, uint>)Egl("eglChooseConfig");
            int* configAttrs = stackalloc int[]
            {
                0x3033, 0x0001, // Pbuffer
                0x3040, Es2Bit | Es3Bit,
                0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8,
                0x3025, 24, 0x3026, 8, 0x3038,
            };
            nint config = 0;
            int count;
            SupportsEs3 = Array.IndexOf(Gl, (nint)0) == -1 &&
                select(Display, configAttrs, &config, 1, &count) != 0 && count > 0;
            if (!SupportsEs3)
            {
                configAttrs[3] = Es2Bit;
                if (select(Display, configAttrs, &config, 1, &count) == 0 || count == 0)
                    throw new InvalidOperationException($"ANGLE has no RGBA8 GLES pbuffer config (0x{GetError():X}).");
            }
            Config = config;
        }
        catch
        {
            if (Display != 0) Terminate(Display);
            if (_gles != 0) NativeLibrary.Free(_gles);
            if (_egl != 0) NativeLibrary.Free(_egl);
            throw;
        }
    }

    internal nint Egl(string name) => NativeLibrary.GetExport(_egl, name);
    private void Terminate(nint display) =>
        ((delegate* unmanaged[Cdecl]<nint, uint>)Egl("eglTerminate"))(display);
}
