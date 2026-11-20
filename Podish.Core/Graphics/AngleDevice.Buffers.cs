using System.Runtime.InteropServices;
using Fiberish.Native;

namespace Podish.Core.Graphics;

internal sealed unsafe partial class AngleDevice
{
    private const uint MapRead = 0x0001, MapWrite = 0x0002;
    private const uint MapInvalidate = 0x0004 | 0x0008, MapFlushExplicit = 0x0010, MapUnsynchronized = 0x0020;
    private const uint BufferMapPointer = 0x88BD;

    private sealed record BufferMapping(nint Native, GuestMemory.Staging Staging, int Length, uint Access)
    {
        internal bool WritesOnUnmap => (Access & (MapWrite | MapFlushExplicit)) == MapWrite;
        internal void Release() => Staging.Release();

        internal void Write(GuestMemory guest, int offset, int length) =>
            guest.Read(Staging.Address + (uint)offset, new Span<byte>((byte*)Native + offset, length));
    }

    private static uint BufferBinding(uint target) => target switch
    {
        ArrayBuffer => 0x8894,
        ElementBuffer => 0x8895,
        PixelPackBuffer => PixelPackBufferBinding,
        PixelUnpackBuffer => PixelUnpackBufferBinding,
        0x8F36 or 0x8F37 => target, // Copy read/write buffers
        0x8A11 => 0x8A28, // Uniform buffer
        0x8C8E => 0x8C8F, // Transform feedback buffer
        _ => 0,
    };

    private nint GetNativeBufferPointer(Context context, uint target)
    {
        var proc = context.Version >= 3 ? Gl("glGetBufferPointerv") : _angle.GetBufferPointer;
        if (proc == 0) return 0;
        nint pointer = 0;
        ((delegate* unmanaged[Cdecl]<uint, uint, nint*, void>)proc)(target, BufferMapPointer, &pointer);
        return pointer;
    }

    private BufferMapping? BoundMapping(Context context, uint target, out uint name)
    {
        name = 0;
        var binding = BufferBinding(target);
        if (binding == 0 || context.Shared.BufferMappings.Count == 0) return null;
        if (context.Version < 3 && target is not (ArrayBuffer or ElementBuffer)) return null;
        name = (uint)GetInteger(binding);
        // A name can be reused while a deleted object is still bound in another context.
        if (!context.Shared.BufferMappings.TryGetValue(name, out var mapping)) return null;
        return GetNativeBufferPointer(context, target) == mapping.Native ? mapping : null;
    }

    private int MapBufferRange(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        var target = a[0];
        var binding = BufferBinding(target);
        var offset = (int)a[1];
        var length = (int)a[2];
        var access = a[3];
        if (binding == 0) return Flag(context, InvalidEnum);
        if (offset < 0 || length < 0 || (access & ~0x3Fu) != 0) return Flag(context, InvalidValue);
        if ((access & (MapRead | MapWrite)) == 0 ||
            ((access & MapRead) != 0 && (access & (MapInvalidate | MapUnsynchronized)) != 0) ||
            ((access & MapFlushExplicit) != 0 && (access & MapWrite) == 0))
            return Flag(context, InvalidOperation);

        var name = (uint)GetInteger(binding);
        if (name == 0 || context.Shared.BufferMappings.ContainsKey(name)) return Flag(context, InvalidOperation);
        if (length > GuestMemory.MaxTransfer) return Flag(context, 0x0505);

        var map = (delegate* unmanaged[Cdecl]<uint, nint, nint, uint, void*>)proc;
        var unmap = (delegate* unmanaged[Cdecl]<uint, byte>)Gl("glUnmapBuffer");
        var staging = guest.AllocateStaging(length);
        void* native = null;
        var installed = false;
        try
        {
            // Preserve bytes the guest leaves untouched in write-only mappings. A
            // separate readable map keeps the original access flags on the live map.
            if ((access & (MapRead | MapInvalidate)) == 0)
            {
                var original = map(target, offset, length, MapRead);
                if (original == null) return 0;
                try { guest.Write(staging.Address, new ReadOnlySpan<byte>(original, length)); }
                finally { unmap(target); }
            }
            native = map(target, offset, length, access);
            if (native == null) return 0;
            if ((access & MapRead) != 0)
                guest.Write(staging.Address, new ReadOnlySpan<byte>(native, length));
            uint address = staging.Address;
            guest.Write(a[4], MemoryMarshal.AsBytes(new ReadOnlySpan<uint>(in address)));
            context.Shared.BufferMappings.Add(name, new BufferMapping((nint)native, staging, length, access));
            installed = true;
            return 0;
        }
        finally
        {
            if (!installed)
            {
                if (native != null) unmap(target);
                staging.Release();
            }
        }
    }

    private int FlushMappedBufferRange(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        var offset = (int)a[1];
        var length = (int)a[2];
        var mapping = BoundMapping(context, a[0], out _);
        if (mapping != null && (mapping.Access & MapFlushExplicit) != 0 &&
            offset >= 0 && length >= 0 && (long)offset + length <= mapping.Length)
            mapping.Write(guest, offset, length);
        ((delegate* unmanaged[Cdecl]<uint, nint, nint, void>)proc)(a[0], offset, length);
        return 0;
    }

    private int UnmapBuffer(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        var unmap = (delegate* unmanaged[Cdecl]<uint, byte>)proc;
        var mapping = BoundMapping(context, a[0], out var name);
        if (mapping == null) return unmap(a[0]);
        int result;
        try
        {
            if (mapping.WritesOnUnmap) mapping.Write(guest, 0, mapping.Length);
        }
        finally
        {
            result = unmap(a[0]);
            context.Shared.BufferMappings.Remove(name);
            mapping.Release();
        }
        return result;
    }

    private int GetBufferPointerv(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        nint pointer = -1;
        ((delegate* unmanaged[Cdecl]<uint, uint, nint*, void>)proc)(a[0], a[1], &pointer);
        if (pointer == -1) return 0;
        var mapping = pointer == 0 ? null : BoundMapping(context, a[0], out _);
        var address = mapping?.Staging.Address ?? 0;
        guest.Write(a[2], MemoryMarshal.AsBytes(new ReadOnlySpan<uint>(in address)));
        return 0;
    }

    private int BufferData(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        var length = (int)a[1];
        if (length < 0) return Flag(context, InvalidValue);
        var data = a[2] == 0 ? null : guest.Input(a[2], length);
        var mapping = BoundMapping(context, a[0], out var name);
        fixed (byte* ptr = data)
            ((delegate* unmanaged[Cdecl]<uint, nint, void*, uint, void>)proc)(a[0], length, ptr, a[3]);
        // On success BufferData implicitly unmaps the previous store.
        if (mapping != null && GetNativeBufferPointer(context, a[0]) != mapping.Native)
        {
            context.Shared.BufferMappings.Remove(name);
            mapping.Release();
        }
        return 0;
    }

    private int DeleteBuffers(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        var data = guest.Input(a[1], Count(a[0], 4))!;
        var names = MemoryMarshal.Cast<byte, uint>(data.AsSpan(0, (int)a[0] * 4));
        try
        {
            if (context.Shared.BufferMappings.Count != 0) FlushDeletedMappings(context, names, guest);
        }
        finally
        {
            fixed (uint* ptr = names)
                ((delegate* unmanaged[Cdecl]<int, uint*, void>)proc)(names.Length, ptr);
            foreach (var name in names)
                if (context.Shared.BufferMappings.Remove(name, out var mapping))
                    mapping.Release();
        }
        return 0;
    }

    private void FlushDeletedMappings(Context context, ReadOnlySpan<uint> names, GuestMemory guest)
    {
        var target = context.Version >= 3 ? 0x8F37u : ArrayBuffer;
        var previous = (uint)GetInteger(BufferBinding(target));
        var bind = (delegate* unmanaged[Cdecl]<uint, uint, void>)Gl("glBindBuffer");
        var isBuffer = (delegate* unmanaged[Cdecl]<uint, byte>)Gl("glIsBuffer");
        try
        {
            foreach (var name in names)
            {
                if (!context.Shared.BufferMappings.TryGetValue(name, out var mapping) ||
                    !mapping.WritesOnUnmap || isBuffer(name) == 0) continue;
                bind(target, name);
                if (GetNativeBufferPointer(context, target) == mapping.Native)
                    mapping.Write(guest, 0, mapping.Length);
            }
        }
        finally { bind(target, previous); }
    }
}
