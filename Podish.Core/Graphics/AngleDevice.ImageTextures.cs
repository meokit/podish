using System.Runtime.InteropServices;
using Fiberish.Core;

namespace Podish.Core.Graphics;

internal sealed unsafe partial class AngleDevice
{
    private const uint Texture2D = 0x0DE1, Texture0 = 0x84C0;
    private const uint TextureBinding2D = 0x8069, ActiveTexture = 0x84E0;
    private const uint MagFilter = 0x2800, MinFilter = 0x2801, WrapS = 0x2802, WrapT = 0x2803;
    private const uint ReadFramebuffer = 0x8CA8, ReadFramebufferBinding = 0x8CAA;

    private sealed record ImageTexture(KmsEglOutput.TextureBo Buffer, uint Sampler);

    private int BindImageTexture(Session session, FiberTask task, uint target, uint image)
    {
        if (!session.Bindings.TryGetValue(task, out var binding) || !Bind(session, binding))
            return 0;
        var context = session.Contexts[binding.Context];
        if (target is not (Texture2D or TextureExternal)) return Flag(context, InvalidEnum);
        if (context.Managed is not { } state || !session.Images.TryGetValue(image, out var buffer))
            return Flag(context, InvalidValue);
        if (target == TextureExternal) return AttachExternalImage(context, state, buffer);

        var name = (uint)GetInteger(TextureBinding2D);
        if (name == 0) return Flag(context, InvalidOperation);
        var previous = state.Textures.GetValueOrDefault(name);
        if (previous?.Buffer == buffer) return 0;
        if (previous == null && state.Textures.Count >= MaxObjects) return Flag(context, 0x0505);
        var sampler = previous?.Sampler ?? _kms!.CreateImageSampler();
        if (sampler == 0) return Flag(context, 0x0505);

        // Guest texture objects keep their own parameter state while sharing image storage.
        CopyImageSamplerParameter(sampler, MagFilter);
        CopyImageSamplerParameter(sampler, MinFilter);
        CopyImageSamplerParameter(sampler, WrapS);
        CopyImageSamplerParameter(sampler, WrapT);
        buffer.Retain();
        var replacement = new ImageTexture(buffer, sampler);
        state.Textures[name] = replacement;
        RestoreImageAttachments(state, name, previous, replacement);
        previous?.Buffer.Release();
        state.TextureUnits[(uint)GetInteger(ActiveTexture)] = name;
        return 0;
    }

    private bool DispatchImageTextureGl(Context context, uint op, Span<uint> a, GuestMemory guest, out int value)
    {
        var state = context.Managed!;
        value = 0;
        switch (op)
        {
            case GlBindTexture:
                if (_kms!.OwnsTexture(a[1]))
                {
                    value = Flag(context, InvalidOperation);
                    return true;
                }
                if (a[0] != Texture2D) return false;
                value = DispatchGl(context, op, a, guest);
                var unit = (uint)GetInteger(ActiveTexture);
                var name = (uint)GetInteger(TextureBinding2D);
                if (name == 0) state.TextureUnits.Remove(unit);
                else state.TextureUnits[unit] = name;
                return true;
            case GlIsTexture:
                return _kms!.OwnsTexture(a[0]);
            case GlDeleteTextures:
                return DeleteGuestTextures(context, a, guest, out value);
            case GlDrawArrays or GlDrawElements:
                if ((state.Textures.Count == 0 || state.TextureUnits.Count == 0) &&
                    state.ExternalPrograms.Count == 0) return false;
                value = SampleImageTextures(context, op, a, guest);
                return true;
        }

        if (state.Textures.Count == 0 || a[0] != Texture2D ||
            op is not (GlTexParameterf or GlTexParameterfv or GlTexParameteri or GlTexParameteriv or
                       GlTexSubImage2D or GlCopyTexSubImage2D or GlCompressedTexSubImage2D or GlTexImage2D or
                       GlCopyTexImage2D or GlCompressedTexImage2D or GlGenerateMipmap))
            return false;

        var bound = (uint)GetInteger(TextureBinding2D);
        if (!state.Textures.TryGetValue(bound, out var texture)) return false;
        switch (op)
        {
            case GlTexParameterf or GlTexParameterfv or GlTexParameteri or GlTexParameteriv:
                if (a[1] is not (MagFilter or MinFilter or WrapS or WrapT)) return false;
                value = DispatchGl(context, op, a, guest);
                CopyImageSamplerParameter(texture.Sampler, a[1]);
                return true;
            case GlTexSubImage2D or GlCopyTexSubImage2D or GlCompressedTexSubImage2D:
                BindTexture2D(texture.Buffer.Texture);
                try
                {
                    if (ForwardTextureOperation(context, op, a, guest))
                        texture.Buffer.Submit();
                }
                finally
                {
                    BindTexture2D(bound);
                }
                return true;
            default:
                RespecifyImageTexture(context, op, a, guest, bound, texture);
                return true;
        }
    }

    private int SampleImageTextures(Context context, uint op, Span<uint> args, GuestMemory guest)
    {
        var state = context.Managed!;
        if (!TryGetExternalUnits(context, out var externalUnits)) return Flag(context, InvalidOperation);
        if (externalUnits == null && (state.Textures.Count == 0 || state.TextureUnits.Count == 0))
            return DispatchGl(context, op, args, guest);
        var previousUnit = (uint)GetInteger(ActiveTexture);
        try
        {
            foreach (var (unit, name) in state.TextureUnits)
            {
                if (externalUnits?.Contains(unit) == true || !state.Textures.TryGetValue(name, out var texture)) continue;
                if (!texture.Buffer.WaitForSample()) return Flag(context, InvalidOperation);
                SelectTextureUnit(unit);
                BindTexture2D(texture.Buffer.Texture);
                _kms!.BindImageSampler(unit - Texture0, texture.Sampler);
            }
            if (externalUnits != null)
            {
                foreach (var unit in externalUnits)
                {
                    var texture = ExternalTextureOnUnit(state, unit);
                    if (texture.Buffer?.WaitForSample() == false) return Flag(context, InvalidOperation);
                    SelectTextureUnit(unit);
                    var name = texture.Buffer?.Texture ?? EmptyExternalTexture(state);
                    if (name == 0) return Flag(context, 0x0505);
                    BindTexture2D(name);
                    _kms!.BindImageSampler(unit - Texture0, texture.Sampler);
                }
            }
            var result = DispatchGl(context, op, args, guest);
            foreach (var (unit, name) in state.TextureUnits)
                if (externalUnits?.Contains(unit) != true && state.Textures.TryGetValue(name, out var texture))
                    texture.Buffer.MarkSampled();
            if (externalUnits != null)
                foreach (var unit in externalUnits)
                    ExternalTextureOnUnit(state, unit).Buffer?.MarkSampled();
            return result;
        }
        finally
        {
            foreach (var (unit, name) in state.TextureUnits)
            {
                if (externalUnits?.Contains(unit) == true || !state.Textures.ContainsKey(name)) continue;
                SelectTextureUnit(unit);
                _kms!.BindImageSampler(unit - Texture0, 0);
                BindTexture2D(name);
            }
            if (externalUnits != null)
            {
                foreach (var unit in externalUnits)
                {
                    SelectTextureUnit(unit);
                    _kms!.BindImageSampler(unit - Texture0, 0);
                    BindTexture2D(state.TextureUnits.GetValueOrDefault(unit));
                }
            }
            SelectTextureUnit(previousUnit);
        }
    }

    private bool DeleteGuestTextures(Context context, Span<uint> args, GuestMemory guest, out int value)
    {
        var names = MemoryMarshal.Cast<byte, uint>(guest.Input(args[1], Count(args[0], 4)));
        value = 0;
        var state = context.Managed!;
        foreach (uint name in names)
        {
            if (!_kms!.OwnsTexture(name) && (name == 0 || name != state.EmptyExternalTexture)) continue;
            value = Flag(context, InvalidOperation);
            return true;
        }

        if (state.TextureUnits.Count == 0 && state.Textures.Count == 0 && state.ExternalTextures.Count == 0) return false;
        value = DispatchGl(context, GlDeleteTextures, args, guest);
        DeleteExternalTextures(state, names);
        foreach (var (unit, name) in state.TextureUnits.ToArray())
            if (names.Contains(name)) state.TextureUnits.Remove(unit);
        foreach (var (fbo, name) in state.TextureAttachments.ToArray())
            if (names.Contains(name)) state.TextureAttachments.Remove(fbo);

        var framebuffer = (uint)GetInteger(0x8CA6);
        if (state.Framebuffers.TryGetValue(framebuffer, out var attachment) &&
            attachment.Type == TextureObject && names.Contains(attachment.Name) &&
            state.Textures.TryGetValue(attachment.Name, out var texture) && ReferenceEquals(texture, attachment.Texture))
        {
            AttachTexture(0);
            state.Detach(framebuffer);
        }
        foreach (uint name in names) state.RemoveTexture(name);
        return true;
    }

    private void RestoreImageAttachments(ManagedFramebufferState state, uint name, ImageTexture? previous,
        ImageTexture? replacement)
    {
        var attachments = previous == null
            ? state.TextureAttachments.Where(entry => entry.Value == name).Select(entry => entry.Key).ToArray()
            : state.Framebuffers.Where(entry => ReferenceEquals(entry.Value.Texture, previous))
                .Select(entry => entry.Key).ToArray();
        if (attachments.Length == 0) return;

        var bound = (uint)GetInteger(0x8CA6);
        var bind = (delegate* unmanaged[Cdecl]<uint, uint, void>)Gl("glBindFramebuffer");
        foreach (uint framebuffer in attachments)
        {
            bind(FramebufferTarget, framebuffer);
            if (replacement == null)
            {
                AttachTexture(name);
                state.Detach(framebuffer);
                state.TextureAttachments[framebuffer] = name;
            }
            else
                SetImageAttachment(state, framebuffer, new ImageAttachment(TextureObject, name, replacement.Buffer, replacement));
        }
        bind(FramebufferTarget, bound);
    }

    private void RespecifyImageTexture(Context context, uint op, Span<uint> args, GuestMemory guest, uint name,
        ImageTexture texture)
    {
        var preserveBaseLevel = op == GlGenerateMipmap || args[1] != 0;
        if (preserveBaseLevel)
        {
            ClearPreviousMipLevels();
            if (!CopyImageBaseLevel(context, texture.Buffer)) return;
        }
        if (!ForwardTextureOperation(context, op, args, guest)) return;
        if (!preserveBaseLevel) ClearPreviousMipLevels();

        RestoreImageAttachments(context.Managed!, name, texture, null);
        context.Managed!.RemoveTexture(name);
    }

    private void ClearPreviousMipLevels()
    {
        var texImage = (delegate* unmanaged[Cdecl]<uint, int, int, int, int, int, uint, uint, void*, void>)
            Gl("glTexImage2D");
        for (int level = 1, width = GetInteger(0x0D33) >> 1; width != 0; level++, width >>= 1)
            texImage(Texture2D, level, 0x1908, 0, 0, 0, 0x1908, 0x1401, null);
    }

    private bool CopyImageBaseLevel(Context context, KmsEglOutput.TextureBo buffer)
    {
        var previousRead = (uint)GetInteger(ReadFramebufferBinding);
        var bind = (delegate* unmanaged[Cdecl]<uint, uint, void>)Gl("glBindFramebuffer");
        uint framebuffer = 0;
        ((delegate* unmanaged[Cdecl]<int, uint*, void>)Gl("glGenFramebuffers"))(1, &framebuffer);
        if (framebuffer == 0)
        {
            Flag(context, 0x0505);
            return false;
        }
        try
        {
            bind(ReadFramebuffer, framebuffer);
            ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int, void>)Gl("glFramebufferTexture2D"))
                (ReadFramebuffer, ColorAttachment, Texture2D, buffer.Texture, 0);
            if (((delegate* unmanaged[Cdecl]<uint, uint>)Gl("glCheckFramebufferStatus"))(ReadFramebuffer) != 0x8CD5)
            {
                Flag(context, InvalidOperation);
                return false;
            }
            var getError = (delegate* unmanaged[Cdecl]<uint>)Gl("glGetError");
            SaveNativeErrors(context, getError);
            ((delegate* unmanaged[Cdecl]<uint, int, uint, int, int, int, int, int, void>)Gl("glCopyTexImage2D"))
                (Texture2D, 0, buffer.PrimeFormat == 0x34325258 ? 0x1907u : 0x1908u, 0, 0,
                    (int)buffer.Width, (int)buffer.Height, 0);
            return CheckNativeError(context, getError);
        }
        finally
        {
            bind(ReadFramebuffer, previousRead);
            ((delegate* unmanaged[Cdecl]<int, uint*, void>)Gl("glDeleteFramebuffers"))(1, &framebuffer);
        }
    }

    private bool ForwardTextureOperation(Context context, uint op, Span<uint> args, GuestMemory guest)
    {
        var getError = (delegate* unmanaged[Cdecl]<uint>)Gl("glGetError");
        SaveNativeErrors(context, getError);
        DispatchGl(context, op, args, guest);
        return CheckNativeError(context, getError);
    }

    private static void SaveNativeErrors(Context context, delegate* unmanaged[Cdecl]<uint> getError)
    {
        for (var i = 0; i < 8; i++)
        {
            var prior = getError();
            if (prior == 0) break;
            Flag(context, prior);
        }
    }

    private static bool CheckNativeError(Context context, delegate* unmanaged[Cdecl]<uint> getError)
    {
        var error = getError();
        if (error == 0) return true;
        Flag(context, error);
        return false;
    }

    private void CopyImageSamplerParameter(uint sampler, uint parameter)
    {
        int value = 0;
        ((delegate* unmanaged[Cdecl]<uint, uint, int*, void>)Gl("glGetTexParameteriv"))(Texture2D, parameter, &value);
        _kms!.SetImageSamplerParameter(sampler, parameter, value);
    }

    private void SelectTextureUnit(uint unit) => ((delegate* unmanaged[Cdecl]<uint, void>)Gl("glActiveTexture"))(unit);

    private void BindTexture2D(uint texture) =>
        ((delegate* unmanaged[Cdecl]<uint, uint, void>)Gl("glBindTexture"))(Texture2D, texture);
}
