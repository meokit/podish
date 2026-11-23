using Podish.Core.Graphics;

namespace Podish.Cli.Display;

internal sealed unsafe partial class AngleDisplayRenderer
{
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

    private AngleDisplayRenderer(AngleLibrary angle, KmsEglWindow window)
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
}
