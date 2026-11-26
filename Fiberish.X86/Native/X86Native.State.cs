using System.Runtime.InteropServices;

namespace Fiberish.X86.Native;

public unsafe partial class X86Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PageMapping64
    {
        public ulong GuestPage;
        public byte Perms;
        public PageMappingFlags Flags;
        public ushort Reserved;
        public IntPtr HostPage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BlockDumpHeader
    {
        public ulong ExecCount;
        public uint StartEip;
        public uint EndEip;
        public uint InstCount;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BlockDumpOp
    {
        public ulong Handler;
        public ulong PackedMemory;
        public uint NextEip;
        public uint Immediate;
        public byte Length;
        public byte ModRm;
        public byte Prefixes;
        public byte Meta;
        public uint Reserved;
    }

    [LibraryImport(LibName, EntryPoint = "X86_ReadBlockDump")]
    public static partial int ReadBlockDump(IntPtr block, BlockDumpHeader* header, BlockDumpOp* ops, uint capacity);

    /// <summary>Creates an engine without process-wide signal handlers when options bit 0 is set.</summary>
    [LibraryImport(LibName, EntryPoint = "X86_CreateWithOptions")]
    public static partial IntPtr CreateWithOptions(uint options);

    [LibraryImport(LibName, EntryPoint = "X86_CreateRun")]
    public static partial IntPtr CreateRun(byte* args, nuint size, byte* error, nuint errorSize);

    [LibraryImport(LibName, EntryPoint = "X86_CreateForRun")]
    public static partial IntPtr CreateForRun(IntPtr run);

    [LibraryImport(LibName, EntryPoint = "X86_FinishRun")]
    public static partial int FinishRun(IntPtr run, byte* error, nuint errorSize);

    [LibraryImport(LibName, EntryPoint = "X86_DestroyRun")]
    public static partial void DestroyRun(IntPtr run);

    [LibraryImport(LibName, EntryPoint = "X86_GetFCW")]
    public static partial ushort GetFCW(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_SetFCW")]
    public static partial void SetFCW(IntPtr state, ushort value);

    [LibraryImport(LibName, EntryPoint = "X86_GetFSW")]
    public static partial ushort GetFSW(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_SetFSW")]
    public static partial void SetFSW(IntPtr state, ushort value);

    [LibraryImport(LibName, EntryPoint = "X86_GetFTW")]
    public static partial ushort GetFTW(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_SetFTW")]
    public static partial void SetFTW(IntPtr state, ushort value);

    [LibraryImport(LibName, EntryPoint = "X86_ReadFPUReg")]
    public static partial void ReadFPUReg(IntPtr state, int logicalIndex, byte* value);

    [LibraryImport(LibName, EntryPoint = "X86_WriteFPUReg")]
    public static partial void WriteFPUReg(IntPtr state, int logicalIndex, byte* value);

    [LibraryImport(LibName, EntryPoint = "X86_GetMXCSR")]
    public static partial uint GetMXCSR(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_SetMXCSR")]
    public static partial void SetMXCSR(IntPtr state, uint value);

    [LibraryImport(LibName, EntryPoint = "X86_GetFaultAddress")]
    public static partial uint GetFaultAddress(IntPtr state);

    // Diagnostics for verifying calling-thread floating environment isolation.
    // Low word packs SoftFloat rounding/flags/tininess/precision; high word is
    // host MXCSR on SSE hosts (zero elsewhere). Restore the original in finally.
    [LibraryImport(LibName, EntryPoint = "X86_DebugGetFloatEnvironment")]
    public static partial ulong DebugGetFloatEnvironment();

    [LibraryImport(LibName, EntryPoint = "X86_DebugSetFloatEnvironment")]
    public static partial void DebugSetFloatEnvironment(ulong environment);

    [LibraryImport(LibName, EntryPoint = "X86_SetTscFrequency")]
    public static partial void SetTscFrequency(IntPtr state, ulong frequency);

    [LibraryImport(LibName, EntryPoint = "X86_SetTscMode")]
    public static partial void SetTscMode(IntPtr state, int mode);

    [LibraryImport(LibName, EntryPoint = "X86_SetTscOffset")]
    public static partial void SetTscOffset(IntPtr state, ulong offset);
}
