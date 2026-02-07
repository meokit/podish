using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Bifrost.Native;

public enum EmuStatus
{
    Running = 0,
    Stopped = 1,
    Fault = 2,
    Yield = 3
}

public enum Reg
{
    EAX = 0,
    ECX = 1,
    EDX = 2,
    EBX = 3,
    ESP = 4,
    EBP = 5,
    ESI = 6,
    EDI = 7,
}

public enum Seg
{
    ES = 0,
    CS = 1,
    SS = 2,
    DS = 3,
    FS = 4,
    GS = 5,
}

public unsafe partial class X86Native
{
    private const string LibName = "fibercpu";

    [LibraryImport(LibName, EntryPoint = "X86_Create")]
    public static partial IntPtr Create();

    [LibraryImport(LibName, EntryPoint = "X86_Clone")]
    public static partial IntPtr Clone(IntPtr parent, int shareMem);

    [LibraryImport(LibName, EntryPoint = "X86_Destroy")]
    public static partial void Destroy(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_RegRead")]
    [SuppressGCTransition]
    public static partial uint RegRead(IntPtr state, int regIndex);

    [LibraryImport(LibName, EntryPoint = "X86_RegWrite")]
    [SuppressGCTransition]
    public static partial void RegWrite(IntPtr state, int regIndex, uint val);

    [LibraryImport(LibName, EntryPoint = "X86_GetEIP")]
    [SuppressGCTransition]
    public static partial uint GetEIP(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_SetEIP")]
    [SuppressGCTransition]
    public static partial void SetEIP(IntPtr state, uint eip);

    [LibraryImport(LibName, EntryPoint = "X86_GetEFLAGS")]
    [SuppressGCTransition]
    public static partial uint GetEFLAGS(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_SetEFLAGS")]
    [SuppressGCTransition]
    public static partial void SetEFLAGS(IntPtr state, uint val);

    [LibraryImport(LibName, EntryPoint = "X86_ReadXMM")]
    [SuppressGCTransition]
    public static partial void ReadXMM(IntPtr state, int idx, byte* val);

    [LibraryImport(LibName, EntryPoint = "X86_WriteXMM")]
    [SuppressGCTransition]
    public static partial void WriteXMM(IntPtr state, int idx, byte* val);

    [LibraryImport(LibName, EntryPoint = "X86_SegBaseRead")]
    [SuppressGCTransition]
    public static partial uint SegBaseRead(IntPtr state, int segIndex);

    [LibraryImport(LibName, EntryPoint = "X86_SegBaseWrite")]
    [SuppressGCTransition]
    public static partial void SegBaseWrite(IntPtr state, int segIndex, uint baseAddr);

    [LibraryImport(LibName, EntryPoint = "X86_MemMap")]
    [SuppressGCTransition]
    public static partial void MemMap(IntPtr state, uint addr, uint size, byte perms);

    [LibraryImport(LibName, EntryPoint = "X86_MemUnmap")]
    [SuppressGCTransition]
    public static partial void MemUnmap(IntPtr state, uint addr, uint size);

    [LibraryImport(LibName, EntryPoint = "X86_MemWrite")]
    public static partial void MemWrite(IntPtr state, uint addr, byte* data, uint size);

    [LibraryImport(LibName, EntryPoint = "X86_MemRead")]
    public static partial void MemRead(IntPtr state, uint addr, byte* val, uint size);

    [LibraryImport(LibName, EntryPoint = "X86_Run")]
    public static partial void Run(IntPtr state, uint endEip, ulong maxInsts);

    [LibraryImport(LibName, EntryPoint = "X86_EmuStop")]
    [SuppressGCTransition]
    public static partial void EmuStop(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_EmuFault")]
    [SuppressGCTransition]
    public static partial void EmuFault(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_EmuYield")]
    [SuppressGCTransition]
    public static partial void EmuYield(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_Step")]
    public static partial int Step(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_GetStatus")]
    [SuppressGCTransition]
    public static partial int GetStatus(IntPtr state);

    // Callbacks
    // public delegate void FaultHandler(IntPtr state, uint addr, int isWrite, IntPtr userdata);
    // public delegate void MemHook(IntPtr state, uint addr, uint size, int isWrite, ulong val, IntPtr userdata);
    // public delegate int InterruptHandler(IntPtr state, uint vector, IntPtr userdata);

    [LibraryImport(LibName, EntryPoint = "X86_SetFaultCallback")]
    public static partial void SetFaultCallback(IntPtr state, delegate* unmanaged<IntPtr, uint, int, IntPtr, void> handler, IntPtr userdata);

    [LibraryImport(LibName, EntryPoint = "X86_SetMemHook")]
    public static partial void SetMemHook(IntPtr state, delegate* unmanaged<IntPtr, uint, uint, int, ulong, IntPtr, void> hook, IntPtr userdata);

    [LibraryImport(LibName, EntryPoint = "X86_SetInterruptHook")]
    public static partial void SetInterruptHook(IntPtr state, byte vector, delegate* unmanaged<IntPtr, uint, IntPtr, int> hook, IntPtr userdata);

    [LibraryImport(LibName, EntryPoint = "X86_GetFaultVector")]
    [SuppressGCTransition]
    public static partial int GetFaultVector(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_MemIsDirty")]
    [SuppressGCTransition]
    public static partial int IsDirty(IntPtr state, uint addr);

    [LibraryImport(LibName, EntryPoint = "X86_ResolvePtr")]
    [SuppressGCTransition]
    public static partial void* ResolvePtr(IntPtr state, uint addr, int isWrite);

    [LibraryImport(LibName, EntryPoint = "X86_AllocatePage")]
    [SuppressGCTransition]
    public static partial void* AllocatePage(IntPtr state, uint addr, byte perms);

    [LibraryImport(LibName, EntryPoint = "X86_MapExternalPage")]
    [SuppressGCTransition]
    public static partial int MapExternalPage(IntPtr state, uint addr, void* externalPage, byte perms);

    [LibraryImport(LibName, EntryPoint = "X86_FlushCache")]
    public static partial void FlushCache(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_InvalidateRange")]
    public static partial void InvalidateRange(IntPtr state, uint addr, uint size);

    [LibraryImport(LibName, EntryPoint = "X86_DumpStats")]
    public static partial int DumpStats(IntPtr state, byte* buffer, nuint bufferSize);
}
