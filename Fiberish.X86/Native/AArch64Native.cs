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
#else
    internal static uint ReadSyscall(nint state, ulong* arguments) => throw new PlatformNotSupportedException();
    internal static void Reset(nint state) => throw new PlatformNotSupportedException();
    internal static ulong Read(nint state, int index) => throw new PlatformNotSupportedException();
    internal static void Write(nint state, int index, ulong value) => throw new PlatformNotSupportedException();
    internal static int Run(nint state, ulong endPc, ulong budget, int singleStep) => throw new PlatformNotSupportedException();
#endif
}
