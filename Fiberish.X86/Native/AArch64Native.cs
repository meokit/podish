using System.Runtime.InteropServices;

using Fiberish.X86.Native;

namespace Fiberish.Cpu.Native;

internal static unsafe partial class AArch64Native
{
#if FIBER_GUEST_AARCH64
    [LibraryImport(X86Native.LibName, EntryPoint = "AArch64_Reset")]
    internal static partial void Reset(nint state);

    [LibraryImport(X86Native.LibName, EntryPoint = "AArch64_RegRead")]
    internal static partial ulong Read(nint state, int index);

    [LibraryImport(X86Native.LibName, EntryPoint = "AArch64_RegWrite")]
    internal static partial void Write(nint state, int index, ulong value);

    [LibraryImport(X86Native.LibName, EntryPoint = "AArch64_ReadSyscall")]
    internal static partial uint ReadSyscall(nint state, ulong* arguments);

    [LibraryImport(X86Native.LibName, EntryPoint = "AArch64_Run")]
    internal static partial int Run(nint state, ulong endPc, ulong budget, int singleStep);

    [LibraryImport(X86Native.LibName, EntryPoint = "AArch64_MemMapHostPage")]
    internal static partial int MemMapHostPage(nint state, ulong address, void* hostPage, byte perms);

    [LibraryImport(X86Native.LibName, EntryPoint = "AArch64_MemUnmap")]
    internal static partial void MemUnmap(nint state, ulong address, ulong size);

    [LibraryImport(X86Native.LibName, EntryPoint = "AArch64_MemProtect")]
    internal static partial void MemProtect(nint state, ulong address, ulong size, byte perms);

    [LibraryImport(X86Native.LibName, EntryPoint = "AArch64_ResolvePtrForRead")]
    internal static partial void* ResolvePtrForRead(nint state, ulong address);

    [LibraryImport(X86Native.LibName, EntryPoint = "AArch64_ResolvePtrForWrite")]
    internal static partial void* ResolvePtrForWrite(nint state, ulong address);

    [LibraryImport(X86Native.LibName, EntryPoint = "AArch64_MemIsDirty")]
    internal static partial int IsDirty(nint state, ulong address);

    [LibraryImport(X86Native.LibName, EntryPoint = "AArch64_MemHasSlowWrite")]
    internal static partial int HasSlowWrite(nint state, ulong address);

    [LibraryImport(X86Native.LibName, EntryPoint = "AArch64_CollectMappedPages")]
    internal static partial nuint CollectMappedPages(nint state, ulong address, ulong size,
        X86Native.PageMapping64* buffer, nuint capacity);

    [LibraryImport(X86Native.LibName, EntryPoint = "AArch64_ResetCodeCacheByRange")]
    internal static partial void ResetCodeCacheByRange(nint state, ulong address, ulong size);

    [LibraryImport(X86Native.LibName, EntryPoint = "AArch64_SetExtendedFaultCallback")]
    internal static partial void SetExtendedFaultCallback(nint state,
        delegate* unmanaged<nint, ulong, int, nint, int> callback, nint userdata);
#else
    internal static uint ReadSyscall(nint state, ulong* arguments) => throw new PlatformNotSupportedException();
    internal static void Reset(nint state) => throw new PlatformNotSupportedException();
    internal static ulong Read(nint state, int index) => throw new PlatformNotSupportedException();
    internal static void Write(nint state, int index, ulong value) => throw new PlatformNotSupportedException();
    internal static int Run(nint state, ulong endPc, ulong budget, int singleStep) => throw new PlatformNotSupportedException();
    internal static int MemMapHostPage(nint state, ulong address, void* hostPage, byte perms)
        => throw new PlatformNotSupportedException();
    internal static void MemUnmap(nint state, ulong address, ulong size)
        => throw new PlatformNotSupportedException();
    internal static void MemProtect(nint state, ulong address, ulong size, byte perms)
        => throw new PlatformNotSupportedException();
    internal static void* ResolvePtrForRead(nint state, ulong address)
        => throw new PlatformNotSupportedException();
    internal static void* ResolvePtrForWrite(nint state, ulong address)
        => throw new PlatformNotSupportedException();
    internal static int IsDirty(nint state, ulong address)
        => throw new PlatformNotSupportedException();
    internal static int HasSlowWrite(nint state, ulong address)
        => throw new PlatformNotSupportedException();
    internal static nuint CollectMappedPages(nint state, ulong address, ulong size,
        X86Native.PageMapping64* buffer, nuint capacity)
        => throw new PlatformNotSupportedException();
    internal static void ResetCodeCacheByRange(nint state, ulong address, ulong size)
        => throw new PlatformNotSupportedException();
    internal static void SetExtendedFaultCallback(nint state,
        delegate* unmanaged<nint, ulong, int, nint, int> callback, nint userdata)
        => throw new PlatformNotSupportedException();
#endif
}
