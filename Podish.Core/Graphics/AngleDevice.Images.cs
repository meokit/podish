using System.Runtime.InteropServices;
using Fiberish.Core;
using Fiberish.VFS;

namespace Podish.Core.Graphics;

internal sealed unsafe partial class AngleDevice
{
    private const uint FramebufferTarget = 0x8D40, RenderbufferTarget = 0x8D41, ColorAttachment = 0x8CE0;
    private const uint TextureObject = 0x1702;
    private Session? _managedOwner;

    private readonly record struct ImageAttachment(uint Type, uint Name, KmsEglOutput.TextureBo Buffer,
        ImageTexture? Texture = null);

    private sealed class ManagedFramebufferState(KmsEglOutput output) : IDisposable
    {
        internal readonly Dictionary<uint, KmsEglOutput.TextureBo> Renderbuffers = [];
        internal readonly Dictionary<uint, ImageAttachment> Framebuffers = [];
        internal readonly Dictionary<uint, uint> TextureAttachments = [];
        internal readonly Dictionary<uint, ImageTexture> Textures = [];
        internal readonly Dictionary<uint, uint> TextureUnits = [];
        internal readonly Dictionary<uint, ExternalTexture> ExternalTextures = [];
        internal readonly Dictionary<uint, uint> ExternalTextureUnits = [];
        internal readonly ExternalTexture DefaultExternalTexture = new();
        internal uint EmptyExternalTexture;
        internal readonly Dictionary<uint, ShaderRewrite> ShaderRewrites = [];
        internal readonly Dictionary<uint, HashSet<string>> CompiledExternalUniforms = [];
        internal readonly Dictionary<uint, ExternalProgram> ExternalPrograms = [];
        internal readonly HashSet<uint> DeletedExternalPrograms = [];

        public void Dispose()
        {
            var external = ExternalTextures.Values.Append(DefaultExternalTexture);
            output.ReleaseImageSamplers(Textures.Values.Select(texture => texture.Sampler)
                .Concat(external.Select(texture => texture.Sampler).Where(sampler => sampler != 0)).ToArray());
            foreach (var texture in Textures.Values) texture.Buffer.Release();
            foreach (var texture in external) texture.Buffer?.Release();
            foreach (var image in Renderbuffers.Values) image.Release();
            foreach (var attachment in Framebuffers.Values) attachment.Buffer.Release();
        }

        internal void Detach(uint framebuffer)
        {
            if (Framebuffers.Remove(framebuffer, out var attachment)) attachment.Buffer.Release();
        }

        internal void RemoveRenderbuffer(uint name)
        {
            if (Renderbuffers.Remove(name, out var image)) image.Release();
        }

        internal void RemoveTexture(uint name)
        {
            if (!Textures.Remove(name, out var texture)) return;
            output.DeleteImageSampler(texture.Sampler);
            texture.Buffer.Release();
        }
    }

    internal DrmScanoutBuffer? AllocateTextureBo(uint width, uint height, uint format) =>
        _kms?.AllocateTexture(width, height, format);

    private int ImportImage(Session session, FiberTask task, Span<uint> args)
    {
        if (session != _managedOwner) return -0x3009;
        if (session.Images.Count >= MaxObjects) return -0x3003;
        var file = task.Process.Syscalls.GetFD((int)args[0], task);
        if (DrmCardInode.GetGpuPrimeBuffer(file, args[1], args[2], args[3], args[4])
                is not KmsEglOutput.TextureBo buffer || buffer.Owner != _kms)
            return -0x300C;
        var id = session.NextId++;
        buffer.Retain();
        session.Images.Add(id, buffer);
        return (int)id;
    }

    private static int DestroyImage(Session session, uint image)
    {
        if (!session.Images.Remove(image, out var buffer)) return -0x300C;
        buffer.Release();
        return 0;
    }

    private int BindImageRenderbuffer(Session session, FiberTask task, uint target, uint image)
    {
        if (!session.Bindings.TryGetValue(task, out var binding) || !Bind(session, binding))
            return 0;
        var context = session.Contexts[binding.Context];
        if (target != RenderbufferTarget) return Flag(context, InvalidEnum);
        if (context.Managed is not { } state || !session.Images.TryGetValue(image, out var buffer))
            return Flag(context, InvalidValue);

        var name = (uint)GetInteger(0x8CA7);
        if (name == 0 || state.Framebuffers.Values.Any(item => item.Type == RenderbufferTarget && item.Name == name))
            return Flag(context, InvalidOperation);
        buffer.Retain();
        state.RemoveRenderbuffer(name);
        state.Renderbuffers.Add(name, buffer);
        return 0;
    }

    private bool DispatchManagedGl(Context context, uint op, Span<uint> a, GuestMemory guest, out int value)
    {
        var state = context.Managed!;
        value = 0;
        switch (op)
        {
            case GlFinish:
            case GlFlush:
                if (state.Framebuffers.TryGetValue((uint)GetInteger(0x8CA6), out var target))
                    target.Buffer.Submit();
                return false;
            case GlFramebufferRenderbuffer:
                if (a[0] != FramebufferTarget || a[1] != ColorAttachment || a[2] != RenderbufferTarget)
                    return false;
                var framebuffer = (uint)GetInteger(0x8CA6);
                if (framebuffer == 0) return false;
                if (!state.Renderbuffers.TryGetValue(a[3], out var image))
                    return UpdateFramebufferAttachment(context, op, a, guest, framebuffer, out value);
                SetImageAttachment(state, framebuffer, new ImageAttachment(RenderbufferTarget, a[3], image));
                return true;
            case GlFramebufferTexture2D:
                if (a[2] == TextureExternal)
                {
                    value = Flag(context, InvalidEnum);
                    return true;
                }
                if (state.ExternalTextures.ContainsKey(a[3]) || (a[3] != 0 && a[3] == state.EmptyExternalTexture))
                {
                    value = Flag(context, InvalidOperation);
                    return true;
                }
                if (_kms!.OwnsTexture(a[3]))
                {
                    value = Flag(context, InvalidOperation);
                    return true;
                }
                if (state.Textures.TryGetValue(a[3], out var texture))
                {
                    if (a[0] != FramebufferTarget || a[1] != ColorAttachment || a[2] != Texture2D || a[4] != 0)
                    {
                        value = Flag(context, InvalidOperation);
                        return true;
                    }
                    var framebufferName = (uint)GetInteger(0x8CA6);
                    if (framebufferName == 0)
                    {
                        value = Flag(context, InvalidOperation);
                        return true;
                    }
                    SetImageAttachment(state, framebufferName,
                        new ImageAttachment(TextureObject, a[3], texture.Buffer, texture));
                    return true;
                }
                if (a[0] == FramebufferTarget && a[1] == ColorAttachment)
                    return UpdateFramebufferAttachment(context, op, a, guest, (uint)GetInteger(0x8CA6), out value);
                return false;
            case GlDeleteFramebuffers:
                value = DispatchGl(context, op, a, guest);
                foreach (uint name in MemoryMarshal.Cast<byte, uint>(guest.Input(a[1], Count(a[0], 4))))
                {
                    state.Detach(name);
                    state.TextureAttachments.Remove(name);
                }
                return true;
            case GlDeleteRenderbuffers:
                var currentFramebuffer = (uint)GetInteger(0x8CA6);
                foreach (uint name in MemoryMarshal.Cast<byte, uint>(guest.Input(a[1], Count(a[0], 4))))
                {
                    if (state.Framebuffers.TryGetValue(currentFramebuffer, out var entry) &&
                        entry.Type == RenderbufferTarget && entry.Name == name)
                    {
                        AttachTexture(0);
                        state.Detach(currentFramebuffer);
                    }
                    state.RemoveRenderbuffer(name);
                }
                return false;
            case GlGetFramebufferAttachmentParameteriv:
                if (a[0] != FramebufferTarget || a[1] != ColorAttachment ||
                    !state.Framebuffers.TryGetValue((uint)GetInteger(0x8CA6), out var attachment))
                    return false;
                uint result;
                switch (a[2])
                {
                    case 0x8CD0: result = attachment.Type; break;
                    case 0x8CD1: result = attachment.Name; break;
                    case 0x8CD2 or 0x8CD3 when attachment.Type == TextureObject: result = 0; break;
                    default: value = Flag(context, InvalidEnum); return true;
                }
                guest.Write(a[3], MemoryMarshal.AsBytes(new ReadOnlySpan<uint>(in result)));
                return true;
            case GlGetRenderbufferParameteriv:
                if (a[0] != RenderbufferTarget ||
                    !state.Renderbuffers.TryGetValue((uint)GetInteger(0x8CA7), out var renderbuffer))
                    return false;
                var parameter = a[1] switch
                {
                    0x8D42 => renderbuffer.Width, 0x8D43 => renderbuffer.Height, 0x8D44 => 0x8058u,
                    0x8D50 or 0x8D51 or 0x8D52 or 0x8D53 => 8u,
                    _ => 0u,
                };
                guest.Write(a[2], MemoryMarshal.AsBytes(new ReadOnlySpan<uint>(in parameter)));
                return true;
            case GlRenderbufferStorage:
                if (a[0] != RenderbufferTarget) return false;
                var renderbufferName = (uint)GetInteger(0x8CA7);
                if (state.Framebuffers.Values.Any(item => item.Type == RenderbufferTarget && item.Name == renderbufferName))
                {
                    value = Flag(context, InvalidOperation);
                    return true;
                }
                state.RemoveRenderbuffer(renderbufferName);
                return false;
            default:
                return DispatchExternalProgramGl(context, op, a, guest, out value) ||
                       DispatchExternalTextureGl(context, op, a, guest, out value) ||
                       DispatchImageTextureGl(context, op, a, guest, out value);
        }
    }

    private void SetImageAttachment(ManagedFramebufferState state, uint framebuffer, ImageAttachment attachment)
    {
        attachment.Buffer.Retain();
        AttachTexture(attachment.Buffer.Texture);
        state.Detach(framebuffer);
        state.TextureAttachments.Remove(framebuffer);
        state.Framebuffers.Add(framebuffer, attachment);
    }

    private bool UpdateFramebufferAttachment(Context context, uint op, Span<uint> args, GuestMemory guest,
        uint framebuffer, out int value)
    {
        value = 0;
        if (framebuffer == 0) return false;

        value = DispatchGl(context, op, args, guest);
        var query = (delegate* unmanaged[Cdecl]<uint, uint, uint, int*, void>)Gl("glGetFramebufferAttachmentParameteriv");
        int type = 0, name = 0, level = 0;
        query(FramebufferTarget, ColorAttachment, 0x8CD0, &type);
        if (type == 0x1702) query(FramebufferTarget, ColorAttachment, 0x8CD1, &name);
        var state = context.Managed!;
        if (state.Framebuffers.TryGetValue(framebuffer, out var before) && (uint)name == before.Buffer.Texture)
            return true;

        state.Detach(framebuffer);
        state.TextureAttachments.Remove(framebuffer);
        if (type == TextureObject && name != 0 && !_kms!.OwnsTexture((uint)name))
        {
            query(FramebufferTarget, ColorAttachment, 0x8CD2, &level);
            if (level == 0) state.TextureAttachments[framebuffer] = (uint)name;
        }
        return true;
    }

    private void AttachTexture(uint texture) =>
        ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int, void>)Gl("glFramebufferTexture2D"))
        (FramebufferTarget, ColorAttachment, 0x0DE1, texture, 0);
}
