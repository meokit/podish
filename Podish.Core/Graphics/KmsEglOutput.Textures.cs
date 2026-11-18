using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Podish.Core.Graphics;

internal sealed unsafe partial class KmsEglOutput
{
    private const uint ReadFramebuffer = 0x8CA8;
    private const uint DrawFramebuffer = 0x8CA9;
    private const uint ColorAttachment = 0x8CE0;
    private readonly HashSet<uint> _textures = [];
    private long _texturePixels;
    private nint _textureContext;
    private uint _readFramebuffer;
    private uint _waylandFramebuffer;
    private delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int, int, uint, uint, void> _blit;
    private delegate* unmanaged[Cdecl]<int, uint*, void> _genSamplers;
    private delegate* unmanaged[Cdecl]<int, uint*, void> _deleteSamplers;
    private delegate* unmanaged[Cdecl]<uint, uint, void> _bindSampler;
    private delegate* unmanaged[Cdecl]<uint, uint, int, void> _samplerParameteri;

    internal nint TextureContext()
    {
        if (_textureContext != 0) return _textureContext;
        _blit = (delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int, int, uint, uint, void>)
            GetProc("glBlitFramebuffer\0"u8);
        _genSamplers = (delegate* unmanaged[Cdecl]<int, uint*, void>)GetProc("glGenSamplers\0"u8);
        _deleteSamplers = (delegate* unmanaged[Cdecl]<int, uint*, void>)GetProc("glDeleteSamplers\0"u8);
        _bindSampler = (delegate* unmanaged[Cdecl]<uint, uint, void>)GetProc("glBindSampler\0"u8);
        _samplerParameteri = (delegate* unmanaged[Cdecl]<uint, uint, int, void>)GetProc("glSamplerParameteri\0"u8);
        if (_blit == null || _genSamplers == null || _deleteSamplers == null ||
            _bindSampler == null || _samplerParameteri == null)
            return 0;
        int* args = stackalloc int[] { 0x3098, 3, 0x3483, 0, 0x3038 };
        _textureContext = _angle.CreateContext(_angle.Display, Config, 0, args);
        return _textureContext;
    }

    internal bool OwnsTexture(uint name) => _textures.Contains(name);

    private nint GetProc(ReadOnlySpan<byte> name)
    {
        var getProc = (delegate* unmanaged[Cdecl]<byte*, nint>)_angle.Egl("eglGetProcAddress");
        fixed (byte* ptr = name) return getProc(ptr);
    }

    internal uint CreateImageSampler()
    {
        uint sampler = 0;
        _genSamplers(1, &sampler);
        return sampler;
    }

    internal void BindImageSampler(uint unit, uint sampler) => _bindSampler(unit, sampler);

    internal void SetImageSamplerParameter(uint sampler, uint parameter, int value) =>
        _samplerParameteri(sampler, parameter, value);

    internal void DeleteImageSampler(uint sampler) => _deleteSamplers(1, &sampler);

    internal void ReleaseImageSamplers(uint[] samplers)
    {
        if (samplers.Length == 0) return;
        using var binding = BindTextureContext(0);
        fixed (uint* ptr = samplers) _deleteSamplers(samplers.Length, ptr);
    }

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
        if (format == 0x34325258) // XRGB has a constant opaque alpha when sampled.
            ((delegate* unmanaged[Cdecl]<uint, uint, int, void>)Gl("glTexParameteri"))(Texture2D, 0x8E45, 1);
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
            // DRM scanout puts BO row zero at the top. The EGL window uses GL's bottom-left origin.
            _blit(0, (int)buffer.Height, (int)buffer.Width, 0, 0, 0, width, height, 0x4000, 0x2600);
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

    internal bool CopyWaylandFrame(nint surface, TextureBo buffer)
    {
        var rendered = Fence();
        if (rendered == 0) return false;
        try
        {
            using (BindTextureContext(surface))
            {
                if (!WaitForFence(rendered) || !buffer.WaitForConsumer()) return false;
                if (_waylandFramebuffer == 0)
                {
                    uint framebuffer;
                    ((delegate* unmanaged[Cdecl]<int, uint*, void>)Gl("glGenFramebuffers"))(1, &framebuffer);
                    if (framebuffer == 0) return false;
                    _waylandFramebuffer = framebuffer;
                }
                var bind = (delegate* unmanaged[Cdecl]<uint, uint, void>)Gl("glBindFramebuffer");
                var attach = (delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int, void>)Gl("glFramebufferTexture2D");
                bind(ReadFramebuffer, 0);
                bind(DrawFramebuffer, _waylandFramebuffer);
                attach(DrawFramebuffer, ColorAttachment, Texture2D, buffer.Texture, 0);
                try
                {
                    int width = (int)buffer.Width, height = (int)buffer.Height;
                    // DMA-BUF consumers treat row zero as the top of the image.
                    _blit(0, height, width, 0, 0, 0, width, height, 0x4000, 0x2600);
                    if (GlError() != 0) return false;
                    if (!buffer.SubmitWaylandFrame())
                    {
                        ((delegate* unmanaged[Cdecl]<void>)Gl("glFinish"))();
                        return false;
                    }
                }
                finally
                {
                    attach(DrawFramebuffer, ColorAttachment, Texture2D, 0, 0);
                    bind(DrawFramebuffer, 0);
                }
            }
            // The GLES client can start rendering into this pbuffer again after the copy.
            if (!WaitForFence(buffer.ProducerFence)) return false;
            _logger.LogDebug("Wayland EGL submitted a GPU buffer {Width}x{Height}", buffer.Width, buffer.Height);
            return true;
        }
        finally
        {
            DestroyFence(rendered);
        }
    }

    private bool WaitForFence(nint fence) => fence == 0 || _waitSync(_angle.Display, fence, 0) != 0;

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
        private nint _consumerFence;
        private bool _waylandFrame;
        private bool _sampleWaitPending;
        public override uint PrimeFormat => format;
        public override bool Ready => ProducerFence != 0;
        public override bool Present() => owner.PresentTexture(this);

        internal bool Submit()
        {
            var fence = owner.Fence();
            if (fence == 0) return false;
            owner.DestroyFence(ProducerFence);
            ProducerFence = fence;
            return true;
        }

        internal bool SubmitWaylandFrame()
        {
            if (!Submit()) return false;
            _waylandFrame = _sampleWaitPending = true;
            return true;
        }

        internal bool WaitForSample()
        {
            if (!_sampleWaitPending) return true;
            if (!owner.WaitForFence(ProducerFence)) return false;
            _sampleWaitPending = false;
            return true;
        }

        internal void MarkSampled()
        {
            if (!_waylandFrame) return;
            var fence = owner.Fence();
            if (fence == 0)
            {
                ((delegate* unmanaged[Cdecl]<void>)owner.Gl("glFinish"))();
                return;
            }
            owner.DestroyFence(_consumerFence);
            _consumerFence = fence;
        }

        internal bool WaitForConsumer()
        {
            if (!owner.WaitForFence(_consumerFence)) return false;
            owner.DestroyFence(_consumerFence);
            _consumerFence = 0;
            return true;
        }

        protected override void Destroy()
        {
            owner._textures.Remove(texture);
            owner._texturePixels -= (long)Width * Height;
            if (owner._textureContext == 0) return;
            using var binding = owner.BindTextureContext(0);
            owner.DestroyFence(ProducerFence);
            owner.DestroyFence(_consumerFence);
            owner.DeleteTexture(texture);
        }
    }
}
