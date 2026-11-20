using System.Buffers.Binary;
using System.Numerics;
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
    private const uint PixelPackBuffer = 0x88EB, PixelUnpackBuffer = 0x88EC;
    private const uint PixelPackBufferBinding = 0x88ED, PixelUnpackBufferBinding = 0x88EF;
    private const uint PackAlignment = 0x0D05, UnpackAlignment = 0x0CF5;

    private readonly record struct Attribute(int Size, uint Type, byte Normalized, int Stride, uint Address, bool Integer);

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
        if ((int)count < 0) throw new GuestTransferException(Errno.EINVAL);
        var length = (ulong)count * elementBytes;
        if (length > GuestMemory.MaxTransfer) throw new GuestTransferException(Errno.E2BIG);
        return (int)length;
    }

    private int PixelBytes(Context context, uint width, uint height, uint depth, uint format, uint type,
        bool pack, bool volume = false)
    {
        if ((int)width < 0 || (int)height < 0 || (int)depth < 0) throw new GuestTransferException(Errno.EINVAL);
        var channels = format switch
        {
            0x1902 or 0x1903 or 0x1906 or 0x1909 or 0x8D94 or 0x84F9 => 1,
            0x190A or 0x8227 or 0x8228 => 2,
            0x1907 or 0x8D98 => 3,
            0x1908 or 0x80E1 or 0x8D99 => 4,
            _ => 0,
        };
        var pixelSize = type switch
        {
            0x1400 or 0x1401 => channels,
            0x1402 or 0x1403 or 0x140B or 0x8D61 => 2 * channels,
            0x1404 or 0x1405 or 0x1406 => 4 * channels,
            0x8033 or 0x8034 or 0x8363 => channels == 0 ? 0 : 2,
            0x84FA or 0x8368 or 0x8C3B or 0x8C3E => channels == 0 ? 0 : 4,
            0x8DAD => channels == 0 ? 0 : 8, // FLOAT_32_UNSIGNED_INT_24_8_REV
            _ => 0,
        };
        if (pixelSize == 0)
        {
            Flag(context, InvalidEnum);
            throw new GuestTransferException(Errno.EINVAL);
        }
        if (width == 0 || height == 0 || depth == 0) return 0;
        var row = Count(width, (uint)pixelSize);
        var align = GetInteger(pack ? PackAlignment : UnpackAlignment);
        var subimage = context.Version >= 3 || (!pack && context.Managed != null);
        var rowLength = subimage ? GetInteger(pack ? 0x0D02u : 0x0CF2u) : 0;
        var storedRow = rowLength == 0 ? row : Count((uint)rowLength, (uint)pixelSize);
        var stride = (storedRow + align - 1) & -align;
        var skipRows = subimage ? (uint)GetInteger(pack ? 0x0D03u : 0x0CF3u) : 0;
        var skipPixels = subimage ? (uint)GetInteger(pack ? 0x0D04u : 0x0CF4u) : 0;
        var imageHeight = volume ? (uint)GetInteger(0x806E) : 0;
        var skipImages = volume ? (uint)GetInteger(0x806D) : 0;
        var imageRows = (ulong)(depth - 1 + skipImages) * (imageHeight == 0 ? height : imageHeight);
        if (imageRows > (uint)GuestMemory.MaxTransfer / (uint)stride)
            throw new GuestTransferException(Errno.E2BIG);
        var bytes = (imageRows + height - 1 + skipRows) * (uint)stride +
                    (ulong)skipPixels * (uint)pixelSize + (uint)row;
        if (bytes > GuestMemory.MaxTransfer) throw new GuestTransferException(Errno.E2BIG);
        return (int)bytes;
    }

    private int StateCount(uint name) => name switch
    {
        0x846D or 0x846E or 0x0B70 or 0x0D3A => 2,
        0x0C10 or 0x0BA2 or 0x0C22 or 0x0C23 or 0x8005 => 4,
        0x86A3 => Math.Max(0, GetInteger(0x86A2)), // COMPRESSED_TEXTURE_FORMATS
        0x8DF8 => Math.Max(0, GetInteger(0x8DF9)), // SHADER_BINARY_FORMATS
        0x87FF => Math.Max(0, GetInteger(0x87FE)), // PROGRAM_BINARY_FORMATS_OES
        _ => 1,
    };

    private int UniformCount(uint program, int location)
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
                    0x8B50 or 0x8B53 or 0x8B57 or 0x8DC6 => 2,
                    0x8B51 or 0x8B54 or 0x8B58 or 0x8DC7 => 3,
                    0x8B52 or 0x8B55 or 0x8B59 or 0x8B5A or 0x8DC8 => 4,
                    0x8B65 or 0x8B67 => 6,
                    0x8B66 or 0x8B69 => 8,
                    0x8B5B => 9,
                    0x8B68 or 0x8B6A => 12,
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
        var valid = a[0] is PackAlignment or UnpackAlignment ||
                    ((context.Version >= 3 || context.Managed != null) && a[0] is 0x0CF2 or 0x0CF3 or 0x0CF4) ||
                    (context.Version >= 3 && a[0] is 0x0D02 or 0x0D03 or 0x0D04 or 0x806D or 0x806E);
        if (!valid)
            return Flag(context, InvalidEnum);
        ((delegate* unmanaged[Cdecl]<uint, int, void>)proc)(a[0], (int)a[1]);
        return 0;
    }

    private static int BindBuffer(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        if (context.Version < 3 && a[0] is not (ArrayBuffer or ElementBuffer)) return Flag(context, InvalidEnum);
        ((delegate* unmanaged[Cdecl]<uint, uint, void>)proc)(a[0], a[1]);
        return 0;
    }

    private static readonly byte[][] ManagedExtensions =
    [
        "GL_OES_EGL_image\0"u8.ToArray(),
        "GL_OES_EGL_image_external\0"u8.ToArray(),
        "GL_EXT_texture_format_BGRA8888\0"u8.ToArray(),
        "GL_EXT_unpack_subimage\0"u8.ToArray(),
    ];

    private static readonly byte[] ManagedExtensionString = System.Text.Encoding.ASCII.GetBytes(
        string.Join(' ', ManagedExtensions.Select(name => System.Text.Encoding.ASCII.GetString(name[..^1]))) + '\0');

    private static int WriteExtensionCount<T>(Context context, uint address, GuestMemory guest)
        where T : unmanaged, INumberBase<T>
    {
        var count = context.Managed == null ? 0 : typeof(T) == typeof(byte) ? 1 : ManagedExtensions.Length;
        T value = T.CreateChecked(count);
        guest.Write(address, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));
        return 0;
    }

    private static int UnsupportedGl(Context context, uint op)
    {
        Flag(context, InvalidOperation);
        return op switch
        {
            GlClientWaitSync => 0x911D, // GL_WAIT_FAILED
            GlGetStringi => -(int)Errno.EINVAL,
            _ => 0,
        };
    }

    private static int GetString(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        if (a[2] == 0) return -(int)Errno.EINVAL;
        if (a[0] == 0x1F03)
        {
            ReadOnlySpan<byte> extensions = context.Managed == null ? "\0"u8 : ManagedExtensionString;
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

    private static int GetStringi(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        if (a[0] != 0x1F03 || context.Managed == null || a[1] >= ManagedExtensions.Length)
        {
            Flag(context, a[0] != 0x1F03 ? InvalidEnum : InvalidValue);
            return -(int)Errno.EINVAL;
        }
        var text = ManagedExtensions[a[1]];
        if (a[3] < text.Length) return -(int)Errno.EINVAL;
        guest.Write(a[2], text);
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

    private int VertexAttribPointer(Context context, nint proc, Span<uint> a, GuestMemory guest) =>
        VertexAttribPointer(context, proc, a[0], (int)a[1], a[2], (byte)a[3], (int)a[4], a[5], integer: false);

    private int VertexAttribIPointer(Context context, nint proc, Span<uint> a, GuestMemory guest) =>
        VertexAttribPointer(context, proc, a[0], (int)a[1], a[2], 0, (int)a[3], a[4], integer: true);

    private int VertexAttribPointer(Context context, nint proc, uint index, int size, uint type,
        byte normalized, int stride, uint address, bool integer)
    {
        if (index >= GetInteger(0x8869) || size is < 1 or > 4 || stride < 0)
            return Flag(context, InvalidValue);
        if (AttributeTypeSize(type) == 0 ||
            (context.Version < 3 && type is not (0x1400 or 0x1401 or 0x1402 or 0x1403 or 0x1406 or 0x140C)) ||
            (integer && type is not (0x1400 or 0x1401 or 0x1402 or 0x1403 or 0x1404 or 0x1405)))
            return Flag(context, InvalidEnum);
        if (type is 0x8368 or 0x8D9F && size != 4) return Flag(context, InvalidOperation);

        var attribute = new Attribute(size, type, normalized, stride, address, integer);
        var client = GetInteger(0x8894) == 0;
        if (!DefaultVertexArray(context))
        {
            // A non-default VAO never stores client pointers.
            if (client && address != 0) return Flag(context, InvalidOperation);
            SetAttribute(proc, index, attribute, (nint)address);
        }
        else
        {
            SetAttribute(proc, index, attribute, client ? 0 : (nint)address);
            if (client) context.Attributes[index] = attribute;
            else context.Attributes.Remove(index);
        }
        return 0;
    }

    private int GetVertexAttribPointerv(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        nint pointer = -1;
        ((delegate* unmanaged[Cdecl]<uint, uint, nint*, void>)proc)(a[0], a[1], &pointer);
        if (pointer == -1) return 0;
        var address = DefaultVertexArray(context) && context.Attributes.TryGetValue(a[0], out var attribute)
            ? attribute.Address : (uint)pointer;
        guest.Write(a[2], MemoryMarshal.AsBytes(new ReadOnlySpan<uint>(in address)));
        return 0;
    }

    private int DrawArrays(Context context, nint proc, Span<uint> a, GuestMemory guest) =>
        DrawArrays(context, proc, a[0], (int)a[1], (int)a[2], 1, instanced: false, guest);

    private int DrawArraysInstanced(Context context, nint proc, Span<uint> a, GuestMemory guest) =>
        DrawArrays(context, proc, a[0], (int)a[1], (int)a[2], (int)a[3], instanced: true, guest);

    private int DrawArrays(Context context, nint proc, uint mode, int first, int count, int instances,
        bool instanced, GuestMemory guest)
    {
        if (first < 0 || count < 0 || instances < 0 || (long)first + count > int.MaxValue)
            return Flag(context, InvalidValue);
        var arrays = ClientArrays(context, guest, first + count - 1, count, instances);
        try
        {
            if (instanced) ((delegate* unmanaged[Cdecl]<uint, int, int, int, void>)proc)(mode, first, count, instances);
            else ((delegate* unmanaged[Cdecl]<uint, int, int, void>)proc)(mode, first, count);
        }
        finally { ResetClientArrays(arrays); }
        return 0;
    }

    private int DrawElements(Context context, nint proc, Span<uint> a, GuestMemory guest) =>
        DrawElements(context, proc, GlDrawElements, a[0], (int)a[1], a[2], a[3], 1, 0, 0, guest);

    private int DrawElementsInstanced(Context context, nint proc, Span<uint> a, GuestMemory guest) =>
        DrawElements(context, proc, GlDrawElementsInstanced, a[0], (int)a[1], a[2], a[3], (int)a[4], 0, 0, guest);

    private int DrawRangeElements(Context context, nint proc, Span<uint> a, GuestMemory guest) =>
        DrawElements(context, proc, GlDrawRangeElements, a[0], (int)a[3], a[4], a[5], 1, a[1], a[2], guest);

    private int DrawElements(Context context, nint proc, int op, uint mode, int count, uint type,
        uint address, int instances, uint start, uint end, GuestMemory guest)
    {
        if (count < 0 || instances < 0 || end < start) return Flag(context, InvalidValue);
        var elementSize = type switch { 0x1401 => 1, 0x1403 => 2, 0x1405 when context.Version >= 3 => 4, _ => 0 };
        if (elementSize == 0) return Flag(context, InvalidEnum);
        var buffered = GetInteger(0x8895) != 0;
        var clientArrays = HasClientArrays(context);
        if (count == 0 || instances == 0 || (buffered && !clientArrays))
        {
            CallDrawElements(proc, op, mode, count, type, buffered ? (void*)(nuint)address : null, instances, start, end);
            return 0;
        }

        var length = Count((uint)count, (uint)elementSize);
        byte[] indices;
        if (buffered)
        {
            // Read the indices to determine how much client vertex data to copy.
            var map = (delegate* unmanaged[Cdecl]<uint, nint, nint, uint, void*>)
                (context.Version >= 3 ? Gl("glMapBufferRange") : _angle.MapBufferRange);
            var unmap = (delegate* unmanaged[Cdecl]<uint, byte>)
                (context.Version >= 3 ? Gl("glUnmapBuffer") : _angle.UnmapBuffer);
            if (map == null || unmap == null) return Flag(context, InvalidOperation);
            var mapped = map(ElementBuffer, (nint)address, length, 0x0001);
            if (mapped == null) return Flag(context, InvalidOperation);
            try { indices = new ReadOnlySpan<byte>(mapped, length).ToArray(); }
            finally { unmap(ElementBuffer); }
        }
        else
            indices = guest.Input(address, length)!;

        List<(uint Index, Attribute Layout, GCHandle Pin)>? arrays = null;
        if (clientArrays)
        {
            var restartEnabled = context.Version >= 3 &&
                ((delegate* unmanaged[Cdecl]<uint, byte>)Gl("glIsEnabled"))(0x8D69) != 0;
            var restart = uint.MaxValue >> (32 - elementSize * 8);
            long max = -1;
            for (var i = 0; i < length; i += elementSize)
            {
                uint index = elementSize switch
                {
                    1 => indices[i],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(indices.AsSpan(i, 2)),
                    _ => BinaryPrimitives.ReadUInt32LittleEndian(indices.AsSpan(i, 4)),
                };
                if (!restartEnabled || index != restart) max = Math.Max(max, index);
            }
            arrays = ClientArrays(context, guest, max, count, instances);
        }
        try
        {
            fixed (byte* data = indices)
                CallDrawElements(proc, op, mode, count, type, buffered ? (void*)(nuint)address : data, instances, start, end);
        }
        finally { ResetClientArrays(arrays); }
        return 0;
    }

    private static void CallDrawElements(nint proc, int op, uint mode, int count, uint type, void* indices,
        int instances, uint start, uint end)
    {
        switch (op)
        {
            case GlDrawElementsInstanced:
                ((delegate* unmanaged[Cdecl]<uint, int, uint, void*, int, void>)proc)(mode, count, type, indices, instances);
                break;
            case GlDrawRangeElements:
                ((delegate* unmanaged[Cdecl]<uint, uint, uint, int, uint, void*, void>)proc)(mode, start, end, count, type, indices);
                break;
            default:
                ((delegate* unmanaged[Cdecl]<uint, int, uint, void*, void>)proc)(mode, count, type, indices);
                break;
        }
    }

    private static int AttributeTypeSize(uint type) => type switch
    {
        0x1400 or 0x1401 => 1,
        0x1402 or 0x1403 or 0x140B => 2,
        0x1404 or 0x1405 or 0x1406 or 0x140C or 0x8368 or 0x8D9F => 4,
        _ => 0,
    };

    private static void SetAttribute(nint proc, uint index, Attribute a, nint address)
    {
        if (a.Integer)
            ((delegate* unmanaged[Cdecl]<uint, int, uint, int, nint, void>)proc)(index, a.Size, a.Type, a.Stride, address);
        else
            ((delegate* unmanaged[Cdecl]<uint, int, uint, byte, int, nint, void>)proc)
                (index, a.Size, a.Type, a.Normalized, a.Stride, address);
    }

    private int GetAttributeInteger(uint index, uint pname)
    {
        int value = 0;
        ((delegate* unmanaged[Cdecl]<uint, uint, int*, void>)Gl("glGetVertexAttribiv"))(index, pname, &value);
        return value;
    }

    private bool DefaultVertexArray(Context context) => context.Version < 3 || GetInteger(0x85B5) == 0;

    private bool HasClientArrays(Context context) =>
        context.Attributes.Count != 0 && DefaultVertexArray(context) &&
        context.Attributes.Any(entry => GetAttributeInteger(entry.Key, 0x8622) != 0);

    private List<(uint Index, Attribute Layout, GCHandle Pin)>? ClientArrays(
        Context context, GuestMemory guest, long maxVertex, int count, int instances)
    {
        if (count == 0 || instances == 0 || maxVertex < 0 || context.Attributes.Count == 0 ||
            !DefaultVertexArray(context)) return null;
        List<(uint, Attribute, GCHandle)>? arrays = null;
        var total = 0L;
        try
        {
            foreach (var (index, attribute) in context.Attributes)
            {
                if (GetAttributeInteger(index, 0x8622) == 0) continue;
                var size = attribute.Type is 0x8368 or 0x8D9F ? 4 : attribute.Size * AttributeTypeSize(attribute.Type);
                var stride = attribute.Stride == 0 ? size : attribute.Stride;
                var divisor = context.Version >= 3 ? (uint)GetAttributeInteger(index, 0x88FE) : 0;
                var last = divisor == 0 ? maxVertex : (instances - 1) / divisor;
                var bytes = last * stride + size;
                total += bytes;
                if (total > GuestMemory.MaxTransfer) throw new GuestTransferException(Errno.E2BIG);
                var data = guest.Input(attribute.Address, (int)bytes)!;
                arrays ??= [];
                arrays.Add((index, attribute, GCHandle.Alloc(data, GCHandleType.Pinned)));
            }
            if (arrays != null) SetClientArrays(arrays, reset: false);
            return arrays;
        }
        catch
        {
            if (arrays != null)
                foreach (var (_, _, pin) in arrays) pin.Free();
            throw;
        }
    }

    private void SetClientArrays(List<(uint Index, Attribute Layout, GCHandle Pin)> arrays, bool reset)
    {
        var bind = (delegate* unmanaged[Cdecl]<uint, uint, void>)Gl("glBindBuffer");
        var previous = (uint)GetInteger(0x8894);
        if (previous != 0) bind(ArrayBuffer, 0);
        foreach (var (index, layout, pin) in arrays)
            SetAttribute(Gl(layout.Integer ? "glVertexAttribIPointer" : "glVertexAttribPointer"),
                index, layout, reset ? 0 : pin.AddrOfPinnedObject());
        if (previous != 0) bind(ArrayBuffer, previous);
    }

    private void ResetClientArrays(List<(uint Index, Attribute Layout, GCHandle Pin)>? arrays)
    {
        if (arrays == null) return;
        SetClientArrays(arrays, reset: true);
        foreach (var (_, _, pin) in arrays) pin.Free();
    }
}
