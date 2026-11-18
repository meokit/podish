using System.Runtime.InteropServices;
using System.Text;

namespace Podish.Core.Graphics;

internal sealed unsafe partial class AngleDevice
{
    private const uint SamplerExternal = 0x8D66;
    private const uint CurrentProgram = 0x8B8D;

    private sealed record ShaderRewrite(byte[] Source, HashSet<string> Uniforms);
    private readonly record struct SamplerLocation(int Location, bool External);
    private sealed record ExternalProgram(HashSet<uint> UniformIndices, List<SamplerLocation> Samplers);

    private bool IsShader(uint shader) => ((delegate* unmanaged[Cdecl]<uint, byte>)Gl("glIsShader"))(shader) != 0;

    private bool IsProgram(uint program) => ((delegate* unmanaged[Cdecl]<uint, byte>)Gl("glIsProgram"))(program) != 0;

    private bool DispatchExternalProgramGl(Context context, uint op, Span<uint> a, GuestMemory guest, out int value)
    {
        var state = context.Managed!;
        value = 0;
        switch (op)
        {
            case GlGetShaderSource:
                if (!state.ShaderRewrites.TryGetValue(a[0], out var rewrite)) return false;
                value = WriteShaderSource(context, a, guest, rewrite.Source);
                return true;
            case GlCompileShader:
                if (!state.ShaderRewrites.TryGetValue(a[0], out var compilation))
                {
                    state.CompiledExternalUniforms.Remove(a[0]);
                    return false;
                }
                value = DispatchGl(context, op, a, guest);
                int compiled = 0;
                ((delegate* unmanaged[Cdecl]<uint, uint, int*, void>)Gl("glGetShaderiv"))(a[0], 0x8B81, &compiled);
                if (compiled != 0) state.CompiledExternalUniforms[a[0]] = compilation.Uniforms;
                else state.CompiledExternalUniforms.Remove(a[0]);
                return true;
            case GlLinkProgram:
                if (state.CompiledExternalUniforms.Count == 0 && !state.ExternalPrograms.ContainsKey(a[0])) return false;
                value = DispatchGl(context, op, a, guest);
                if (IsProgram(a[0])) LinkExternalProgram(state, a[0]);
                return true;
            case GlUseProgram:
                if (state.DeletedExternalPrograms.Count == 0) return false;
                value = DispatchGl(context, op, a, guest);
                if (state.DeletedExternalPrograms.RemoveWhere(program =>
                {
                    if (IsProgram(program)) return false;
                    state.ExternalPrograms.Remove(program);
                    return true;
                }) != 0)
                    PruneExternalShaders(state);
                return true;
            case GlDeleteShader or GlDetachShader or GlDeleteProgram:
                if (state.ShaderRewrites.Count == 0 && state.CompiledExternalUniforms.Count == 0 &&
                    state.ExternalPrograms.Count == 0)
                    return false;
                value = DispatchGl(context, op, a, guest);
                if (op == GlDeleteProgram && state.ExternalPrograms.ContainsKey(a[0]))
                {
                    // ANGLE keeps a deleted current program alive until it is unbound.
                    if (IsProgram(a[0])) state.DeletedExternalPrograms.Add(a[0]);
                    else state.ExternalPrograms.Remove(a[0]);
                }
                PruneExternalShaders(state);
                return true;
            default:
                return false;
        }
    }

    private void PruneExternalShaders(ManagedFramebufferState state)
    {
        foreach (var shader in state.ShaderRewrites.Keys.Concat(state.CompiledExternalUniforms.Keys).Distinct().ToArray())
        {
            if (IsShader(shader)) continue;
            state.ShaderRewrites.Remove(shader);
            state.CompiledExternalUniforms.Remove(shader);
        }
    }

    private static int WriteShaderSource(Context context, Span<uint> a, GuestMemory guest, byte[] source)
    {
        if ((int)a[1] < 0) return Flag(context, InvalidValue);
        var capacity = Count(a[1], 1);
        var length = Math.Min(source.Length, Math.Max(0, capacity - 1));
        if (capacity != 0)
        {
            var output = new byte[length + 1];
            source.AsSpan(0, length).CopyTo(output);
            guest.Write(a[3], output);
        }
        if (a[2] != 0) guest.Write(a[2], MemoryMarshal.AsBytes(new ReadOnlySpan<int>(in length)));
        return 0;
    }

    private void LinkExternalProgram(ManagedFramebufferState state, uint program)
    {
        var getProgram = (delegate* unmanaged[Cdecl]<uint, uint, int*, void>)Gl("glGetProgramiv");
        int linked = 0, attached = 0;
        getProgram(program, 0x8B82 /* GL_LINK_STATUS */, &linked);
        if (linked == 0) return;
        state.ExternalPrograms.Remove(program);

        getProgram(program, 0x8B85 /* GL_ATTACHED_SHADERS */, &attached);
        if (attached <= 0 || attached > GuestMemory.MaxTransfer / sizeof(uint)) return;
        var shaders = new uint[attached];
        fixed (uint* ptr = shaders)
            ((delegate* unmanaged[Cdecl]<uint, int, int*, uint*, void>)Gl("glGetAttachedShaders"))
                (program, attached, null, ptr);
        var externalNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shader in shaders)
            if (state.CompiledExternalUniforms.TryGetValue(shader, out var uniforms))
                externalNames.UnionWith(uniforms);
        if (externalNames.Count == 0) return;

        int count = 0, maxLength = 0;
        getProgram(program, 0x8B86 /* GL_ACTIVE_UNIFORMS */, &count);
        getProgram(program, 0x8B87 /* GL_ACTIVE_UNIFORM_MAX_LENGTH */, &maxLength);
        if (count <= 0 || maxLength <= 0 || maxLength > GuestMemory.MaxString) return;
        var getUniform = (delegate* unmanaged[Cdecl]<uint, uint, int, int*, int*, uint*, byte*, void>)Gl("glGetActiveUniform");
        var getLocation = (delegate* unmanaged[Cdecl]<uint, byte*, int>)Gl("glGetUniformLocation");
        var external = new ExternalProgram([], []);
        var nameBuffer = new byte[maxLength];
        fixed (byte* ptr = nameBuffer)
        {
            for (uint index = 0; index < count; index++)
            {
                int length = 0, elements = 0;
                uint type = 0;
                getUniform(program, index, maxLength, &length, &elements, &type, ptr);
                // ES 2 supports 2D and cube samplers. External samplers were lowered to 2D.
                if (type is not (0x8B5E or 0x8B60) || length <= 0) continue;
                var name = Encoding.ASCII.GetString(nameBuffer, 0, length);
                var arrayOffset = name.IndexOf("[0]", StringComparison.Ordinal);
                var isExternal = type == 0x8B5E &&
                    externalNames.Contains(arrayOffset < 0 ? name : name[..arrayOffset]);
                if (isExternal) external.UniformIndices.Add(index);
                var location = getLocation(program, ptr);
                if (location >= 0) external.Samplers.Add(new SamplerLocation(location, isExternal));
                for (int element = 1; element < elements && arrayOffset >= 0; element++)
                {
                    var elementName = Encoding.ASCII.GetBytes(
                        $"{name[..arrayOffset]}[{element}]{name[(arrayOffset + 3)..]}\0");
                    fixed (byte* elementPtr = elementName)
                        location = getLocation(program, elementPtr);
                    if (location >= 0) external.Samplers.Add(new SamplerLocation(location, isExternal));
                }
            }
        }
        if (external.UniformIndices.Count != 0) state.ExternalPrograms[program] = external;
    }

    private int GetActiveUniform(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        if ((int)a[2] < 0) return Flag(context, InvalidValue);
        var length = guest.Output(a[3], sizeof(int), nullable: true);
        var size = guest.Output(a[4], sizeof(int));
        var type = guest.Output(a[5], sizeof(uint));
        var capacity = Count(a[2], 1);
        var name = guest.Output(a[6], capacity);
        var external = context.Managed?.ExternalPrograms.GetValueOrDefault(a[0])?.UniformIndices.Contains(a[1]) == true;
        var getError = (delegate* unmanaged[Cdecl]<uint>)Gl("glGetError");
        if (external) SaveNativeErrors(context, getError);
        fixed (byte* len = length, sizePtr = size, typePtr = type, namePtr = name)
            ((delegate* unmanaged[Cdecl]<uint, uint, int, byte*, byte*, byte*, byte*, void>)proc)
                (a[0], a[1], capacity, len, sizePtr, typePtr, namePtr);
        if (external && CheckNativeError(context, getError))
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(type!, SamplerExternal);
        guest.WriteOutput(a[3], length, sizeof(int));
        guest.WriteOutput(a[4], size, sizeof(int));
        guest.WriteOutput(a[5], type, sizeof(uint));
        guest.WriteOutput(a[6], name, capacity);
        return 0;
    }

    private bool TryGetExternalUnits(Context context, out HashSet<uint>? units)
    {
        units = null;
        var state = context.Managed!;
        if (state.ExternalPrograms.Count == 0) return true;
        var program = (uint)GetInteger(CurrentProgram);
        if (!state.ExternalPrograms.TryGetValue(program, out var samplers)) return true;
        var getUniform = (delegate* unmanaged[Cdecl]<uint, int, int*, void>)Gl("glGetUniformiv");
        int capacity = GetInteger(0x8B4D /* GL_MAX_COMBINED_TEXTURE_IMAGE_UNITS */);
        Span<byte> types = stackalloc byte[Math.Clamp(capacity, 0, 1024)];
        types.Clear();
        units = [];
        foreach (var sampler in samplers.Samplers)
        {
            int unit = 0;
            getUniform(program, sampler.Location, &unit);
            if ((uint)unit >= types.Length) return false;
            byte type = sampler.External ? (byte)1 : (byte)2;
            if (types[unit] != 0 && types[unit] != type) return false;
            types[unit] = type;
            if (sampler.External) units.Add(Texture0 + (uint)unit);
        }
        return true;
    }
}
