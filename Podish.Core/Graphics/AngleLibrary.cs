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

    internal readonly nint Display;
    internal readonly nint Config;
    internal readonly nint[] Gl;
    internal readonly nint MapBufferRange;
    internal readonly nint UnmapBuffer;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, nint, int*, nint> CreateContext;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, uint> DestroyContext;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, int*, nint> CreatePbufferSurface;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, uint> DestroySurface;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint> MakeCurrent;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, uint> SwapBuffers;
    internal readonly delegate* unmanaged[Cdecl]<int> GetError;

    internal static AngleLibrary Acquire(string directory)
    {
        lock (Gate)
        {
            if (_active == null)
                _active = new AngleLibrary(directory);
            else if (_active._directory != directory)
                throw new InvalidOperationException("Active Podish containers must use the same ANGLE directory.");
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

    private AngleLibrary(string directory)
    {
        _directory = directory;
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
            Gl = Array.ConvertAll(AngleDevice.GlNames, name => NativeLibrary.GetExport(_gles, name));

            var getProc = (delegate* unmanaged[Cdecl]<byte*, nint>)Egl("eglGetProcAddress");
            fixed (byte* map = "glMapBufferRangeEXT\0"u8, unmap = "glUnmapBufferOES\0"u8)
            {
                MapBufferRange = getProc(map);
                UnmapBuffer = getProc(unmap);
            }
            fixed (byte* name = "eglGetPlatformDisplayEXT\0"u8)
            {
                var platformDisplay = (delegate* unmanaged[Cdecl]<uint, nint, int*, nint>)getProc(name);
                if (platformDisplay == null)
                    throw new InvalidOperationException("ANGLE does not export eglGetPlatformDisplayEXT.");
                // ANGLE uses Metal on macOS and its default backend on other hosts.
                int* attribs = stackalloc int[] { 0x3203, 0x3489, 0x3038 };
                Display = platformDisplay(0x3202, 0, OperatingSystem.IsMacOS() ? attribs : null);
            }

            var initialize = (delegate* unmanaged[Cdecl]<nint, int*, int*, uint>)Egl("eglInitialize");
            if (Display == 0 || initialize(Display, null, null) == 0)
                throw new InvalidOperationException($"ANGLE eglInitialize failed (0x{GetError():X}).");

            var select = (delegate* unmanaged[Cdecl]<nint, int*, nint*, int, int*, uint>)Egl("eglChooseConfig");
            int* configAttrs = stackalloc int[]
            {
                0x3033, 0x0001, // Pbuffer
                0x3040, 0x0004, // OpenGL ES 2
                0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8,
                0x3025, 24, 0x3026, 8, 0x3038,
            };
            nint config;
            int count;
            if (select(Display, configAttrs, &config, 1, &count) == 0 || count == 0)
                throw new InvalidOperationException($"ANGLE has no RGBA8 GLES 2 pbuffer config (0x{GetError():X}).");
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

    private nint Egl(string name) => NativeLibrary.GetExport(_egl, name);
    private void Terminate(nint display) =>
        ((delegate* unmanaged[Cdecl]<nint, uint>)Egl("eglTerminate"))(display);
}
