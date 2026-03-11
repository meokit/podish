using System.Runtime.InteropServices;
using Fiberish.Native;

namespace Fiberish.Memory;

internal static class ZeroPageProvider
{
    private static nint _sharedZeroPagePtr;
    private static readonly object Gate = new();

    public static IntPtr GetPointer()
    {
        if (_sharedZeroPagePtr != 0)
            return (IntPtr)_sharedZeroPagePtr;

        lock (Gate)
        {
            if (_sharedZeroPagePtr != 0)
                return (IntPtr)_sharedZeroPagePtr;

            unsafe
            {
                var ptr = (nint)NativeMemory.AlignedAlloc((nuint)LinuxConstants.PageSize, (nuint)LinuxConstants.PageSize);
                if (ptr == 0) return IntPtr.Zero;
                new Span<byte>((void*)ptr, LinuxConstants.PageSize).Clear();
                _sharedZeroPagePtr = ptr;
                return (IntPtr)ptr;
            }
        }
    }

    public static bool IsZeroPage(IntPtr ptr)
    {
        return ptr != IntPtr.Zero && (nint)ptr == _sharedZeroPagePtr;
    }
}
