using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Podish.Core.Graphics;

internal sealed unsafe partial class KmsEglOutput
{
    private const uint ReadFramebuffer = 0x8CA8;
    private const uint ColorAttachment = 0x8CE0;
    private readonly HashSet<uint> _textures = [];
    private long _texturePixels;
    private nint _textureContext;
    private uint _readFramebuffer;
    private delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int, int, uint, uint, void> _blit;

    internal nint TextureContext()
    {
        if (_textureContext != 0) return _textureContext;
        var getProc = (delegate* unmanaged[Cdecl]<byte*, nint>)_angle.Egl("eglGetProcAddress");
        fixed (byte* name = "glBlitFramebuffer\0"u8)
            _blit = (delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int, int, uint, uint, void>)getProc(name);
        if (_blit == null) return 0;
        int* args = stackalloc int[] { 0x3098, 3, 0x3483, 0, 0x3038 };
        _textureContext = _angle.CreateContext(_angle.Display, Config, 0, args);
        return _textureContext;
    }

    internal bool OwnsTexture(uint name) => _textures.Contains(name);

    internal TextureBo? AllocateTexture(uint width, uint height, uint format)
    {
        if (_textureContext == 0 || width is 0 or > FramebufferDevice.DimensionLimit ||
            height is 0 or > FramebufferDevice.DimensionLimit || _textures.Count >= 32 ||
            _texturePixels + (long)width * height > 16 * 1024 * 1024)
            return null;

        using var binding = BindTextureContext(0);
        uint name;
        ((delegate* unmanaged[Cdecl]<int, uint*, void>)Gl("glGenTextures"))(1, &name);
        ((delegate* unmanaged[Cdecl]<uint, uint, void>)Gl("glBindTexture"))(Texture2D, name);
        ((delegate* unmanaged[Cdecl]<uint, int, int, int, int, int, uint, uint, void*, void>)Gl("glTexImage2D"))
            (Texture2D, 0, 0x8058 /* RGBA8 */, (int)width, (int)height, 0, 0x1908, 0x1401, null);
        ((delegate* unmanaged[Cdecl]<uint, uint, void>)Gl("glBindTexture"))(Texture2D, 0);
        if (name == 0 || GlError() != 0)
        {
            DeleteTexture(name);
            return null;
        }

        _textures.Add(name);
        _texturePixels += (long)width * height;
        return new TextureBo(this, name, width, height, format);
    }

    private bool PresentTexture(TextureBo buffer)
    {
        if (_window.CanPresent?.Invoke() == false) return true;
        using var binding = BindTextureContext(_surface);
        Require(_waitSync(_angle.Display, buffer.ProducerFence, 0) != 0, "wait for compositor rendering");
        if (_readFramebuffer == 0)
        {
            uint framebuffer;
            ((delegate* unmanaged[Cdecl]<int, uint*, void>)Gl("glGenFramebuffers"))(1, &framebuffer);
            _readFramebuffer = framebuffer;
        }
        ((delegate* unmanaged[Cdecl]<uint, uint, void>)Gl("glBindFramebuffer"))(ReadFramebuffer, _readFramebuffer);
        var attach = (delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int, void>)Gl("glFramebufferTexture2D");
        attach(ReadFramebuffer, ColorAttachment, Texture2D, buffer.Texture, 0);
        try
        {
            int width = 0, height = 0;
            Require(_querySurface(_angle.Display, _surface, 0x3057, &width) != 0 &&
                    _querySurface(_angle.Display, _surface, 0x3056, &height) != 0, "query texture scanout size");
            _blit(0, 0, (int)buffer.Width, (int)buffer.Height, 0, 0, width, height, 0x4000, 0x2600);
            Require(GlError() == 0, "blit the compositor texture");
            var fence = Fence();
            Require(fence != 0, "create texture scanout fence");
            try
            {
                Require(_angle.SwapBuffers(_angle.Display, _surface) != 0, "swap texture scanout");
                Require(_clientWait(_angle.Display, fence, 0, 250_000_000) == 0x30F6, "wait for texture scanout");
            }
            finally
            {
                DestroyFence(fence);
            }
            _logger.LogDebug("KMS EGL presented a compositor texture {Width}x{Height}", width, height);
            return true;
        }
        finally
        {
            attach(ReadFramebuffer, ColorAttachment, Texture2D, 0, 0);
        }
    }

    private uint GlError() => ((delegate* unmanaged[Cdecl]<uint>)Gl("glGetError"))();

    private void DeleteTexture(uint texture) =>
        ((delegate* unmanaged[Cdecl]<int, uint*, void>)Gl("glDeleteTextures"))(1, &texture);

    private TextureBinding BindTextureContext(nint surface)
    {
        var previous = new TextureBinding(this, _getContext(), _getSurface(0x3059), _getSurface(0x305A));
        Require(_angle.MakeCurrent(_angle.Display, surface, surface, _textureContext) != 0, "bind texture scanout");
        return previous;
    }

    private readonly record struct TextureBinding(KmsEglOutput Output, nint Context, nint Draw, nint Read) : IDisposable
    {
        public void Dispose() => Output.Require(
            Output._angle.MakeCurrent(Output._angle.Display, Draw, Read, Context) != 0, "restore EGL binding");
    }

    internal sealed class TextureBo(KmsEglOutput owner, uint texture, uint width, uint height, uint format)
        : DrmScanoutBuffer(width, height, width * 4)
    {
        internal KmsEglOutput Owner => owner;
        internal uint Texture => texture;
        internal nint ProducerFence { get; private set; }
        public override uint PrimeFormat => format;
        public override bool Ready => ProducerFence != 0;
        public override bool Present() => owner.PresentTexture(this);

        internal void Submit()
        {
            var fence = owner.Fence();
            if (fence == 0) return;
            owner.DestroyFence(ProducerFence);
            ProducerFence = fence;
        }

        protected override void Destroy()
        {
            owner._textures.Remove(texture);
            owner._texturePixels -= (long)Width * Height;
            if (owner._textureContext == 0) return;
            using var binding = owner.BindTextureContext(0);
            owner.DestroyFence(ProducerFence);
            owner.DeleteTexture(texture);
        }
    }
}
