using System.Buffers.Binary;

namespace Podish.Core.Graphics;

internal sealed unsafe partial class AngleDevice
{
    private const uint TextureExternal = 0x8D65, TextureBindingExternal = 0x8D67, RequiredTextureUnits = 0x8D68;
    private const int Linear = 0x2601, Nearest = 0x2600, ClampToEdge = 0x812F;

    private sealed class ExternalTexture
    {
        internal int Min = Linear, Mag = Linear;
        internal uint Sampler;
        internal KmsEglOutput.TextureBo? Buffer;
    }

    private bool DispatchExternalTextureGl(Context context, uint op, Span<uint> a, GuestMemory guest, out int value)
    {
        var state = context.Managed!;
        value = 0;
        if (op is GlGetBooleanv or GlGetFloatv or GlGetIntegerv && a[0] == TextureBindingExternal)
        {
            uint name = state.ExternalTextureUnits.GetValueOrDefault((uint)GetInteger(ActiveTexture));
            WriteTextureQuery(op, guest, a[1], (int)name);
            return true;
        }
        if (op == GlIsTexture && a[0] != 0 &&
            (a[0] == state.EmptyExternalTexture || state.ExternalTextures.ContainsKey(a[0])))
        {
            value = a[0] == state.EmptyExternalTexture ? 0 : 1;
            return true;
        }
        if (op == GlBindTexture)
        {
            if (a[0] == TextureExternal)
            {
                value = BindExternalTexture(context, a[1]);
                return true;
            }
            if (a[1] != 0 && (a[1] == state.EmptyExternalTexture || state.ExternalTextures.ContainsKey(a[1])))
            {
                value = Flag(context, InvalidOperation);
                return true;
            }
        }
        if (a[0] != TextureExternal) return false;
        switch (op)
        {
            case GlGetTexParameterfv or GlGetTexParameteriv or
                 GlTexParameterf or GlTexParameterfv or GlTexParameteri or GlTexParameteriv:
                value = ExternalTextureParameter(context, op, a, guest);
                return true;
            case GlTexImage2D or GlTexSubImage2D or GlCopyTexImage2D or GlCopyTexSubImage2D or
                 GlCompressedTexImage2D or GlCompressedTexSubImage2D or GlGenerateMipmap:
                value = Flag(context, InvalidEnum);
                return true;
            default:
                return false;
        }
    }

    private static void WriteTextureQuery(uint op, GuestMemory guest, uint address, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        if (op == GlGetBooleanv)
        {
            bytes[0] = value != 0 ? (byte)1 : (byte)0;
            guest.Write(address, bytes[..1]);
            return;
        }
        if (op is GlGetFloatv or GlGetTexParameterfv)
            BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        else
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        guest.Write(address, bytes);
    }

    private int BindExternalTexture(Context context, uint name)
    {
        var state = context.Managed!;
        var unit = (uint)GetInteger(ActiveTexture);
        if (name == 0)
        {
            state.ExternalTextureUnits.Remove(unit);
            return 0;
        }
        if (_kms!.OwnsTexture(name) || name == state.EmptyExternalTexture) return Flag(context, InvalidOperation);
        if (!state.ExternalTextures.ContainsKey(name))
        {
            if (((delegate* unmanaged[Cdecl]<uint, byte>)Gl("glIsTexture"))(name) != 0)
                return Flag(context, InvalidOperation);
            if (state.ExternalTextures.Count >= MaxObjects) return Flag(context, 0x0505);
            // Reserve the name in ANGLE's namespace with an incomplete 2D backing.
            // Its binding stays private and the guest sees it only on the external target.
            var previous = (uint)GetInteger(TextureBinding2D);
            var getError = (delegate* unmanaged[Cdecl]<uint>)Gl("glGetError");
            SaveNativeErrors(context, getError);
            BindTexture2D(name);
            if (!CheckNativeError(context, getError)) return 0;
            BindTexture2D(previous);
            state.ExternalTextures.Add(name, new ExternalTexture());
        }
        state.ExternalTextureUnits[unit] = name;
        return 0;
    }

    private static ExternalTexture ExternalTextureOnUnit(ManagedFramebufferState state, uint unit)
    {
        var name = state.ExternalTextureUnits.GetValueOrDefault(unit);
        return state.ExternalTextures.GetValueOrDefault(name) ?? state.DefaultExternalTexture;
    }

    private int ExternalTextureParameter(Context context, uint op, Span<uint> a, GuestMemory guest)
    {
        var texture = ExternalTextureOnUnit(context.Managed!, (uint)GetInteger(ActiveTexture));
        var parameter = a[1];
        if (op is GlGetTexParameterfv or GlGetTexParameteriv)
        {
            int value = parameter switch
            {
                MinFilter => texture.Min,
                MagFilter => texture.Mag,
                WrapS or WrapT => ClampToEdge,
                RequiredTextureUnits => 1, // The managed RGB image uses one texture.
                _ => -1,
            };
            if (value < 0) return Flag(context, InvalidEnum);
            WriteTextureQuery(op, guest, a[2], value);
            return 0;
        }

        uint raw = a[2];
        if (op is GlTexParameteriv or GlTexParameterfv)
        {
            Span<byte> data = stackalloc byte[sizeof(uint)];
            guest.Read(a[2], data);
            raw = BinaryPrimitives.ReadUInt32LittleEndian(data);
        }
        var numeric = op is GlTexParameterf or GlTexParameterfv
            ? BitConverter.UInt32BitsToSingle(raw) : (int)raw;
        switch (parameter)
        {
            case MinFilter when numeric is Linear or Nearest:
                texture.Min = (int)numeric;
                break;
            case MagFilter when numeric is Linear or Nearest:
                texture.Mag = (int)numeric;
                break;
            case WrapS or WrapT when numeric == ClampToEdge:
                break;
            default:
                return Flag(context, InvalidEnum);
        }
        if (texture.Sampler != 0) _kms!.SetImageSamplerParameter(texture.Sampler, parameter, (int)numeric);
        return 0;
    }

    private int AttachExternalImage(Context context, ManagedFramebufferState state, KmsEglOutput.TextureBo buffer)
    {
        var texture = ExternalTextureOnUnit(state, (uint)GetInteger(ActiveTexture));
        if (texture.Buffer == buffer) return 0;
        if (texture.Sampler == 0)
        {
            var sampler = _kms!.CreateImageSampler();
            if (sampler == 0) return Flag(context, 0x0505);
            _kms.SetImageSamplerParameter(sampler, MinFilter, texture.Min);
            _kms.SetImageSamplerParameter(sampler, MagFilter, texture.Mag);
            _kms.SetImageSamplerParameter(sampler, WrapS, ClampToEdge);
            _kms.SetImageSamplerParameter(sampler, WrapT, ClampToEdge);
            texture.Sampler = sampler;
        }
        buffer.Retain();
        var previous = texture.Buffer;
        texture.Buffer = buffer;
        previous?.Release();
        return 0;
    }

    private uint EmptyExternalTexture(ManagedFramebufferState state)
    {
        if (state.EmptyExternalTexture != 0) return state.EmptyExternalTexture;
        uint name = 0;
        ((delegate* unmanaged[Cdecl]<int, uint*, void>)Gl("glGenTextures"))(1, &name);
        return state.EmptyExternalTexture = name;
    }

    private void DeleteExternalTextures(ManagedFramebufferState state, ReadOnlySpan<uint> names)
    {
        if (state.ExternalTextures.Count == 0) return;
        foreach (var name in names)
        {
            if (!state.ExternalTextures.Remove(name, out var texture)) continue;
            if (texture.Sampler != 0) _kms!.DeleteImageSampler(texture.Sampler);
            texture.Buffer?.Release();
        }
        foreach (var (unit, name) in state.ExternalTextureUnits.ToArray())
            if (names.Contains(name)) state.ExternalTextureUnits.Remove(unit);
    }
}
