using Fiberish.VFS;
using Podish.Core.Graphics;
using Podish.Display;

namespace Podish.Cli.Display;

internal sealed unsafe partial class AngleDisplayRenderer
{
    internal bool CanImport(DrmScanoutBuffer buffer) =>
        GuestTextures != null && buffer is KmsEglOutput.TextureBo texture && texture.Owner == GuestTextures;

    internal bool UploadGpuBuffer(IDisplayTexture target, DrmScanoutBuffer buffer, bool yInverted, ulong lease)
    {
        var destination = (Texture)target;
        var source = (KmsEglOutput.TextureBo)buffer;
        nint image = source.ImportForCompositor();
        if (image == 0)
            return false;

        uint texture = 0;
        try
        {
            _glGenTextures(1, &texture);
            _glBindTexture(Texture2D, texture);
            _glImageTargetTexture(Texture2D, image);
            _glBindFramebuffer(ReadFramebuffer, _importFramebuffer);
            _glFramebufferTexture2D(ReadFramebuffer, ColorAttachment0, Texture2D, texture, 0);
            _glBindFramebuffer(DrawFramebuffer, _framebuffer);
            _glFramebufferTexture2D(DrawFramebuffer, ColorAttachment0, Texture2D, destination.Name, 0);

            uint readStatus = _glCheckFramebufferStatus(ReadFramebuffer);
            uint drawStatus = _glCheckFramebufferStatus(DrawFramebuffer);
            if (_glGetError() != 0 || readStatus != FramebufferComplete || drawStatus != FramebufferComplete)
                return false;

            int width = destination.Width, height = destination.Height;
            _glBlitFramebuffer(0, yInverted ? height : 0, width, yInverted ? 0 : height,
                0, 0, width, height, ColorBufferBit, 0x2600 /* GL_NEAREST */);
            if (_glGetError() != 0)
                return false;
            destination.NativeRgba = true;
        }
        finally
        {
            _glFramebufferTexture2D(ReadFramebuffer, ColorAttachment0, Texture2D, 0, 0);
            _glFramebufferTexture2D(DrawFramebuffer, ColorAttachment0, Texture2D, 0, 0);
            DeleteTexture(texture);
        }

        // Keep wl_buffer busy until the GPU has finished copying from the client's BO.
        var create = (delegate* unmanaged[Cdecl]<nint, uint, nint*, nint>)_angle.Egl("eglCreateSync");
        nint fence = create(_angle.Display, 0x30F9 /* EGL_SYNC_FENCE */, null);
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
                if (status == 0x30F5 /* EGL_TIMEOUT_EXPIRED */)
                    return;
                RequireEgl(status == 0x30F6 /* EGL_CONDITION_SATISFIED */, "wait for the client's GPU buffer copy");
                DestroyFence(upload.Fence);
            }
            _uploads.Dequeue();
            upload.Buffer.Release();
            consumed.Add(upload.Lease);
        }
    }

    internal bool HasPendingUploads => _uploads.Count != 0;

    private void DestroyFence(nint fence)
    {
        if (fence == 0)
            return;
        var destroy = (delegate* unmanaged[Cdecl]<nint, nint, uint>)_angle.Egl("eglDestroySync");
        destroy(_angle.Display, fence);
    }
}
