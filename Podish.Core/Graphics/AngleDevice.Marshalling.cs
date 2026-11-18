using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Fiberish.Native;

namespace Podish.Core.Graphics;

internal sealed unsafe partial class AngleDevice
{
    private const uint InvalidEnum = 0x0500;
    private const uint InvalidValue = 0x0501;
    private const uint InvalidOperation = 0x0502;
    private const uint ArrayBuffer = 0x8892;
    private const uint ElementBuffer = 0x8893;

    private readonly record struct Attribute(int Size, uint Type, byte Normalized, int Stride, uint Address, bool Client);

    private nint Gl(string name) => _angle.Gl[Array.IndexOf(GlNames, name)];

    private int GetInteger(uint name)
    {
        int value = 0;
        ((delegate* unmanaged[Cdecl]<uint, int*, void>)Gl("glGetIntegerv"))(name, &value);
        return value;
    }

    private static int Flag(Context context, uint error)
    {
        if (context.Error == 0) context.Error = error;
        return 0;
    }

    private static int Count(uint count, uint elementBytes)
    {
        var length = (ulong)count * elementBytes;
        if (length > GuestMemory.MaxTransfer) throw new GuestTransferException(Errno.E2BIG);
        return (int)length;
    }

    private int PixelBytes(Context context, uint width, uint height, uint format, uint type, uint alignment)
    {
        if ((int)width < 0 || (int)height < 0) throw new GuestTransferException(Errno.EINVAL);
        var channels = format switch
        {
            0x1902 or 0x1903 or 0x1906 or 0x1909 => 1, // depth, red, alpha, luminance
            0x190A or 0x8227 => 2, // luminance-alpha, RG
            0x1907 => 3,
            0x1908 or 0x80E1 => 4, // RGBA, BGRA
            0x84F9 => 1, // depth-stencil packed
            _ => 0,
        };
        var pixelSize = type switch
        {
            0x1401 => channels,
            0x1403 or 0x140B or 0x8D61 => 2 * channels,
            0x1405 or 0x1406 => 4 * channels,
            0x8033 or 0x8034 or 0x8363 => channels == 0 ? 0 : 2,
            0x84FA or 0x8368 or 0x8C3B or 0x8C3E => channels == 0 ? 0 : 4,
            _ => 0,
        };
        if (pixelSize == 0)
        {
            Flag(context, InvalidEnum);
            throw new GuestTransferException(Errno.EINVAL);
        }
        if (width == 0 || height == 0) return 0;
        var row = Count(width, (uint)pixelSize);
        var align = GetInteger(alignment);
        var unpack = context.Managed != null && alignment == 0x0CF5;
        var rowLength = unpack ? GetInteger(0x0CF2) : 0;
        var storedRow = rowLength == 0 ? row : Count((uint)rowLength, (uint)pixelSize);
        var stride = (storedRow + align - 1) & -align;
        var skipRows = unpack ? (uint)GetInteger(0x0CF3) : 0;
        var skipPixels = unpack ? (uint)GetInteger(0x0CF4) : 0;
        var bytes = (ulong)(height - 1 + skipRows) * (uint)stride + (ulong)skipPixels * (uint)pixelSize + (uint)row;
        if (bytes > GuestMemory.MaxTransfer) throw new GuestTransferException(Errno.E2BIG);
        return (int)bytes;
    }

    private int StateCount(Context context, uint name) => name switch
    {
        0x846D or 0x846E or 0x0B70 or 0x0D3A => 2,
        0x0C10 or 0x0BA2 or 0x0C22 or 0x0C23 or 0x8005 => 4,
        0x86A3 => Math.Max(0, GetInteger(0x86A2)), // COMPRESSED_TEXTURE_FORMATS
        0x8DF8 => Math.Max(0, GetInteger(0x8DF9)), // SHADER_BINARY_FORMATS
        0x87FF => Math.Max(0, GetInteger(0x87FE)), // PROGRAM_BINARY_FORMATS_OES
        _ => 1,
    };

    private int UniformCount(Context context, uint program, int location)
    {
        var getProgram = (delegate* unmanaged[Cdecl]<uint, uint, int*, void>)Gl("glGetProgramiv");
        var getUniform = (delegate* unmanaged[Cdecl]<uint, uint, int, int*, int*, uint*, byte*, void>)Gl("glGetActiveUniform");
        var getLocation = (delegate* unmanaged[Cdecl]<uint, byte*, int>)Gl("glGetUniformLocation");
        int count = 0, maxLength = 0;
        getProgram(program, 0x8B86, &count);
        getProgram(program, 0x8B87, &maxLength);
        if (location < 0 || count == 0 || maxLength <= 0 || maxLength > 65536) return 1;
        var name = new byte[maxLength + 20];
        fixed (byte* ptr = name)
        {
            for (uint i = 0; i < count; i++)
            {
                int length, elements;
                uint type;
                getUniform(program, i, maxLength, &length, &elements, &type, ptr);
                var components = type switch
                {
                    0x8B50 or 0x8B53 or 0x8B57 => 2,
                    0x8B51 or 0x8B54 or 0x8B58 => 3,
                    0x8B52 or 0x8B55 or 0x8B59 or 0x8B5A => 4,
                    0x8B5B => 9,
                    0x8B5C => 16,
                    _ => 1,
                };
                if (getLocation(program, ptr) == location) return components;
                if (elements <= 1) continue;

                // ANGLE names array uniforms with [0], followed by any struct field suffix.
                var zeroIndex = name.AsSpan(0, length).LastIndexOf("[0]"u8);
                if (zeroIndex < 0) continue;
                var suffix = name.AsSpan(zeroIndex + 3, length - zeroIndex - 3).ToArray();
                for (var element = 1; element < elements; element++)
                {
                    var digits = System.Text.Encoding.ASCII.GetBytes(element.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var end = zeroIndex + 1 + digits.Length;
                    digits.CopyTo(name.AsSpan(zeroIndex + 1));
                    name[end++] = (byte)']';
                    suffix.CopyTo(name.AsSpan(end));
                    name[end + suffix.Length] = 0;
                    if (getLocation(program, ptr) == location) return components;
                }
            }
        }
        return 1;
    }

    private int PixelStorei(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        if (a[0] is not (0x0D05 or 0x0CF5) && (context.Managed == null || a[0] is not (0x0CF2 or 0x0CF3 or 0x0CF4)))
            return Flag(context, InvalidEnum);
        ((delegate* unmanaged[Cdecl]<uint, int, void>)proc)(a[0], (int)a[1]);
        return 0;
    }

    private int BindBuffer(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        // Pixel pack/unpack buffers require a different wire representation for pixel transfers.
        if (a[0] is not (ArrayBuffer or ElementBuffer)) return Flag(context, InvalidEnum);
        ((delegate* unmanaged[Cdecl]<uint, uint, void>)proc)(a[0], a[1]);
        return 0;
    }

    private int GetString(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        if (a[2] == 0) return -(int)Errno.EINVAL;
        if (a[0] == 0x1F03)
        {
            ReadOnlySpan<byte> extensions = context.Managed == null ? "\0"u8
                : "GL_OES_EGL_image GL_OES_EGL_image_external GL_EXT_texture_format_BGRA8888 GL_EXT_unpack_subimage\0"u8;
            if (a[2] < extensions.Length) return -(int)Errno.EINVAL;
            guest.Write(a[1], extensions);
            return 0;
        }
        var text = ((delegate* unmanaged[Cdecl]<uint, byte*>)proc)(a[0]);
        if (text == null) return -(int)Errno.EINVAL;
        var length = 0;
        var maximum = (int)Math.Min(a[2] - 1, 511);
        while (length < maximum && text[length] != 0) length++;
        if ((ulong)a[1] + (uint)length > uint.MaxValue) throw new GuestTransferException(Errno.EFAULT);
        guest.Write(a[1], new ReadOnlySpan<byte>(text, length));
        guest.Write(a[1] + (uint)length, "\0"u8);
        return 0;
    }

    private int ShaderSource(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        if (a[1] > 1024) return Flag(context, InvalidValue);
        var count = (int)a[1];
        Span<uint> addresses = stackalloc uint[count];
        Span<int> lengths = stackalloc int[count];
        guest.Read(a[2], MemoryMarshal.AsBytes(addresses));
        if (a[3] == 0) lengths.Fill(-1);
        else guest.Read(a[3], MemoryMarshal.AsBytes(lengths));
        var pins = new GCHandle[count];
        var strings = stackalloc nint[count];
        var segments = context.Managed == null ? null : new byte[count][];
        var total = 0;
        var sourceLength = 0;
        try
        {
            for (var i = 0; i < count; i++)
            {
                if (lengths[i] > GuestMemory.MaxString) throw new GuestTransferException(Errno.E2BIG);
                var text = lengths[i] < 0 ? guest.String(addresses[i]) : guest.Input(addresses[i], lengths[i]);
                if (text == null) throw new GuestTransferException(Errno.EFAULT);
                total += text.Length;
                if (total > GuestMemory.MaxTransfer) throw new GuestTransferException(Errno.E2BIG);
                if (segments != null)
                {
                    segments[i] = text;
                    sourceLength += lengths[i] < 0 ? text.Length - 1 : lengths[i];
                }
                pins[i] = GCHandle.Alloc(text, GCHandleType.Pinned);
                strings[i] = pins[i].AddrOfPinnedObject();
            }
            if (context.Managed is { } state && segments != null && IsShader(a[0]))
            {
                var source = new byte[sourceLength];
                var offset = 0;
                for (int i = 0; i < segments.Length; i++)
                {
                    var length = lengths[i] < 0 ? segments[i].Length - 1 : lengths[i];
                    segments[i].AsSpan(0, length).CopyTo(source.AsSpan(offset));
                    offset += length;
                }
                if (ExternalImageShader.TryRewrite(source, out var rewritten, out var externalUniforms))
                {
                    fixed (byte* ptr = rewritten!)
                    {
                        var text = (nint)ptr;
                        var length = rewritten!.Length;
                        ((delegate* unmanaged[Cdecl]<uint, int, nint*, int*, void>)proc)(a[0], 1, &text, &length);
                    }
                    state.ShaderRewrites[a[0]] = new ShaderRewrite(source, externalUniforms!);
                    return 0;
                }
                state.ShaderRewrites.Remove(a[0]);
            }
            fixed (int* size = lengths)
                ((delegate* unmanaged[Cdecl]<uint, int, nint*, int*, void>)proc)(a[0], count, strings, size);
        }
        finally
        {
            foreach (var pin in pins)
                if (pin.IsAllocated) pin.Free();
        }
        return 0;
    }

    private int VertexAttribPointer(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        var size = (int)a[1];
        var stride = (int)a[4];
        if (a[0] >= GetInteger(0x8869) || size is < 1 or > 4 || stride < 0)
            return Flag(context, InvalidValue);
        if (AttributeTypeSize(a[2]) == 0) return Flag(context, InvalidEnum);
        var client = GetInteger(0x8894) == 0;
        var attribute = new Attribute(size, a[2], (byte)a[3], stride, a[5], client);
        SetAttribute(proc, a[0], attribute, client ? 0 : (nint)a[5]);
        context.Attributes[a[0]] = attribute;
        return 0;
    }

    private int GetVertexAttribPointerv(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        if (a[1] != 0x8645) return Flag(context, InvalidEnum);
        if (a[0] >= GetInteger(0x8869)) return Flag(context, InvalidValue);
        Span<uint> address = [context.Attributes.GetValueOrDefault(a[0]).Address];
        guest.Write(a[2], MemoryMarshal.AsBytes(address));
        return 0;
    }

    private int DrawArrays(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        var draw = (delegate* unmanaged[Cdecl]<uint, int, int, void>)proc;
        var first = (int)a[1];
        var count = (int)a[2];
        if (first < 0 || count < 0 || (long)first + count > int.MaxValue)
            return Flag(context, InvalidValue);
        var arrays = ClientArrays(context, guest, first + count - 1, count);
        try { draw(a[0], first, count); }
        finally { ResetClientArrays(arrays); }
        return 0;
    }

    private int DrawElements(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        var draw = (delegate* unmanaged[Cdecl]<uint, int, uint, void*, void>)proc;
        var count = (int)a[1];
        if (count < 0) return Flag(context, InvalidValue);
        var elementSize = a[2] switch { 0x1401 => 1, 0x1403 => 2, _ => 0 };
        if (elementSize == 0) return Flag(context, InvalidEnum);
        if (count == 0)
        {
            draw(a[0], 0, a[2], null);
            return 0;
        }
        var hasElementBuffer = GetInteger(0x8895) != 0;
        if (hasElementBuffer && !HasClientArrays(context))
        {
            draw(a[0], count, a[2], (void*)(nuint)a[3]);
            return 0;
        }
        var length = Count(a[1], (uint)elementSize);
        byte[]? indices;
        if (hasElementBuffer)
        {
            // ANGLE exposes the GLES2 map-buffer-range extension, used here only to size
            // the guest memory copies for mixed buffer and client-side array draws.
            var map = (delegate* unmanaged[Cdecl]<uint, nint, nint, uint, void*>)_angle.MapBufferRange;
            var unmap = (delegate* unmanaged[Cdecl]<uint, byte>)_angle.UnmapBuffer;
            if (map == null || unmap == null) return Flag(context, InvalidOperation);
            var mapped = map(ElementBuffer, (nint)a[3], length, 0x0001);
            if (mapped == null) return Flag(context, InvalidOperation);
            try { indices = new ReadOnlySpan<byte>(mapped, length).ToArray(); }
            finally { unmap(ElementBuffer); }
        }
        else
            indices = guest.Input(a[3], length);
        if (indices == null) return Flag(context, InvalidOperation);
        var max = 0;
        for (var i = 0; i < length; i += elementSize)
            max = Math.Max(max, elementSize == 1 ? indices[i] : BinaryPrimitives.ReadUInt16LittleEndian(indices.AsSpan(i, 2)));
        var arrays = ClientArrays(context, guest, max, count);
        try
        {
            fixed (byte* data = indices)
                draw(a[0], count, a[2], hasElementBuffer ? (void*)(nuint)a[3] : data);
        }
        finally { ResetClientArrays(arrays); }
        return 0;
    }

    private static int AttributeTypeSize(uint type) => type switch
    {
        0x1400 or 0x1401 => 1,
        0x1402 or 0x1403 => 2,
        0x1406 or 0x140C => 4,
        _ => 0,
    };

    private static void SetAttribute(nint proc, uint index, Attribute a, nint address) =>
        ((delegate* unmanaged[Cdecl]<uint, int, uint, byte, int, nint, void>)proc)
        (index, a.Size, a.Type, a.Normalized, a.Stride, address);

    private bool AttributeEnabled(uint index)
    {
        int enabled = 0;
        ((delegate* unmanaged[Cdecl]<uint, uint, int*, void>)Gl("glGetVertexAttribiv"))(index, 0x8622, &enabled);
        return enabled != 0;
    }

    private bool HasClientArrays(Context context) =>
        context.Attributes.Any(entry => entry.Value.Client && AttributeEnabled(entry.Key));

    private List<(uint Index, Attribute Layout, GCHandle Pin)>? ClientArrays(
        Context context, GuestMemory guest, int maxVertex, int count)
    {
        if (count == 0) return null;
        List<(uint, Attribute, GCHandle)>? arrays = null;
        var total = 0;
        try
        {
            foreach (var (index, attribute) in context.Attributes)
            {
                if (!attribute.Client || !AttributeEnabled(index)) continue;
                var size = attribute.Size * AttributeTypeSize(attribute.Type);
                var stride = attribute.Stride == 0 ? size : attribute.Stride;
                var bytes = Count((uint)maxVertex, (uint)stride) + size;
                total += bytes;
                if (total > GuestMemory.MaxTransfer) throw new GuestTransferException(Errno.E2BIG);
                var data = guest.Input(attribute.Address, bytes) ?? throw new GuestTransferException(Errno.EFAULT);
                arrays ??= [];
                arrays.Add((index, attribute, GCHandle.Alloc(data, GCHandleType.Pinned)));
            }
            if (arrays == null) return null;

            SetClientArrays(arrays, reset: false);
            return arrays;
        }
        catch
        {
            if (arrays != null)
            {
                foreach (var (_, _, pin) in arrays) pin.Free();
            }
            throw;
        }
    }

    private void SetClientArrays(List<(uint Index, Attribute Layout, GCHandle Pin)> arrays, bool reset)
    {
        var bind = (delegate* unmanaged[Cdecl]<uint, uint, void>)Gl("glBindBuffer");
        var pointer = Gl("glVertexAttribPointer");
        var previous = (uint)GetInteger(0x8894);
        if (previous != 0) bind(ArrayBuffer, 0);
        foreach (var (index, layout, pin) in arrays)
            SetAttribute(pointer, index, layout, reset ? 0 : pin.AddrOfPinnedObject());
        if (previous != 0) bind(ArrayBuffer, previous);
    }

    private void ResetClientArrays(List<(uint Index, Attribute Layout, GCHandle Pin)>? arrays)
    {
        if (arrays == null) return;
        SetClientArrays(arrays, reset: true);
        foreach (var (_, _, pin) in arrays) pin.Free();
    }
}
