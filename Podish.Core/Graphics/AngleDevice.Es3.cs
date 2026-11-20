using System.Runtime.InteropServices;
using Fiberish.Native;

namespace Podish.Core.Graphics;

internal sealed unsafe partial class AngleDevice
{
    private int FenceSync(Context context, nint proc, Span<uint> a, GuestMemory guest)
    {
        if (_nextSync > int.MaxValue) return Flag(context, 0x0505);
        var native = ((delegate* unmanaged[Cdecl]<uint, uint, nint>)proc)(a[0], a[1]);
        if (native == 0) return 0;
        var id = _nextSync++;
        context.Shared.Syncs.Add(id, native);
        return (int)id;
    }

    private int UniformBlockCount(uint program, uint index, uint pname)
    {
        if (pname != 0x8A43) return 1; // GL_UNIFORM_BLOCK_ACTIVE_UNIFORM_INDICES
        int count = 0;
        ((delegate* unmanaged[Cdecl]<uint, uint, uint, int*, void>)Gl("glGetActiveUniformBlockiv"))
            (program, index, 0x8A42, &count);
        return Count((uint)count, 4) / 4;
    }

    private static int GetUniformIndices(Context context, nint proc, Span<uint> a, GuestMemory guest) =>
        DispatchStringArray(context, proc, a, guest, uniformIndices: true);

    private static int TransformFeedbackVaryings(Context context, nint proc, Span<uint> a, GuestMemory guest) =>
        DispatchStringArray(context, proc, a, guest, uniformIndices: false);

    private static int DispatchStringArray(Context context, nint proc, Span<uint> a, GuestMemory guest, bool uniformIndices)
    {
        var count = (int)a[1];
        if (count < 0) return Flag(context, InvalidValue);
        var bytes = Count(a[1], 4);
        var addresses = guest.Input(a[2], bytes)!;
        var indices = uniformIndices ? guest.Output(a[3], bytes) : null;
        var names = new nint[count];
        var pins = new GCHandle[count];
        try
        {
            var total = 0;
            var pointers = MemoryMarshal.Cast<byte, uint>(addresses.AsSpan(0, bytes));
            for (var i = 0; i < count; i++)
            {
                var text = guest.String(pointers[i]);
                total += text.Length;
                if (total > GuestMemory.MaxTransfer) throw new GuestTransferException(Errno.E2BIG);
                pins[i] = GCHandle.Alloc(text, GCHandleType.Pinned);
                names[i] = pins[i].AddrOfPinnedObject();
            }
            fixed (nint* strings = names)
            fixed (byte* output = indices)
            {
                if (uniformIndices)
                    ((delegate* unmanaged[Cdecl]<uint, int, nint*, byte*, void>)proc)(a[0], count, strings, output);
                else
                    ((delegate* unmanaged[Cdecl]<uint, int, nint*, uint, void>)proc)(a[0], count, strings, a[3]);
            }
            if (uniformIndices) guest.WriteOutput(a[3], indices, bytes);
        }
        finally
        {
            foreach (var pin in pins)
                if (pin.IsAllocated) pin.Free();
        }
        return 0;
    }
}
