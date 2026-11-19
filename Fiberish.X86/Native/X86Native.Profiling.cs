using System.Runtime.InteropServices;

namespace Fiberish.X86.Native;

public unsafe partial class X86Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SequenceProfileEntry
    {
        public IntPtr First;
        public IntPtr Second;
        public ulong Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NGramProfileEntry
    {
        public IntPtr First, Second, Third, Fourth;
        public ulong Count;
    }

    [LibraryImport(LibName, EntryPoint = "X86_GetTrainingHandlerId")]
    public static partial uint GetTrainingHandlerId(IntPtr handler);

    [LibraryImport(LibName, EntryPoint = "X86_GetTrainingOperationIdAt")]
    public static partial uint GetTrainingOperationIdAt(uint index);

    [LibraryImport(LibName, EntryPoint = "X86_GetTrainingOperationName")]
    private static partial IntPtr GetTrainingOperationNameUtf8(uint id);

    public static string? GetTrainingOperationName(uint id) =>
        Marshal.PtrToStringUTF8(GetTrainingOperationNameUtf8(id));

    [LibraryImport(LibName, EntryPoint = "X86_GetNGramProfile")]
    public static partial nuint GetNGramProfile(IntPtr state, NGramProfileEntry* entries, nuint capacity);

    [LibraryImport(LibName, EntryPoint = "X86_GetBuildFeatures")]
    public static partial uint GetBuildFeatures();

    [LibraryImport(LibName, EntryPoint = "X86_GetSuperOpcodeSetHash")]
    private static partial IntPtr GetSuperOpcodeSetHashPointer();

    public static string GetSuperOpcodeSetHash() => Marshal.PtrToStringUTF8(GetSuperOpcodeSetHashPointer())!;

    [LibraryImport(LibName, EntryPoint = "X86_GetOptimizationConfig")]
    private static partial IntPtr GetOptimizationConfigPointer();

    public static string GetOptimizationConfig() => Marshal.PtrToStringUTF8(GetOptimizationConfigPointer())!;

    [LibraryImport(LibName, EntryPoint = "X86_GetSequenceProfile")]
    public static partial nuint GetSequenceProfile(IntPtr state, SequenceProfileEntry* entries, nuint capacity);

    [LibraryImport(LibName, EntryPoint = "X86_ResetSequenceProfile")]
    public static partial void ResetSequenceProfile(IntPtr state);
}
