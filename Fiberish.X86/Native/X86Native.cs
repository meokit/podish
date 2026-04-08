#if !FIBERISH_BROWSER_WASM
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Fiberish.X86.Native;

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
    EDI = 7
}

public enum Seg
{
    ES = 0,
    CS = 1,
    SS = 2,
    DS = 3,
    FS = 4,
    GS = 5
}

public unsafe partial class X86Native
{
    [Flags]
    public enum PageMappingFlags : byte
    {
        None = 0,
        Dirty = 1 << 0,
        External = 1 << 1
    }

    private const string LibName =
#if FIBERISH_BROWSER_WASM
        "__Internal";
#else
        "fibercpu";
#endif

#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize()
    {
#if !FIBERISH_BROWSER_WASM
        NativeLibraryResolver.Register(typeof(X86Native), LibName);
#endif
    }
#pragma warning restore CA2255

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
    // public delegate bool FaultHandler(IntPtr state, uint addr, int isWrite, IntPtr userdata);
    // public delegate void MemHook(IntPtr state, uint addr, uint size, int isWrite, ulong val, IntPtr userdata);
    // public delegate int InterruptHandler(IntPtr state, uint vector, IntPtr userdata);

    [LibraryImport(LibName, EntryPoint = "X86_SetFaultCallback")]
    public static partial void SetFaultCallback(IntPtr state,
        delegate* unmanaged<IntPtr, uint, int, IntPtr, int> handler, IntPtr userdata);

    [LibraryImport(LibName, EntryPoint = "X86_SetMemHook")]
    public static partial void SetMemHook(IntPtr state,
        delegate* unmanaged<IntPtr, uint, uint, int, ulong, IntPtr, void> hook, IntPtr userdata);

    [LibraryImport(LibName, EntryPoint = "X86_SetInterruptHook")]
    public static partial void SetInterruptHook(IntPtr state, byte vector,
        delegate* unmanaged<IntPtr, uint, IntPtr, int> hook, IntPtr userdata);

    [LibraryImport(LibName, EntryPoint = "X86_GetFaultVector")]
    [SuppressGCTransition]
    public static partial int GetFaultVector(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_MemIsDirty")]
    [SuppressGCTransition]
    public static partial int IsDirty(IntPtr state, uint addr);

    [LibraryImport(LibName, EntryPoint = "X86_ResolvePtr")]
    [SuppressGCTransition]
    public static partial void* ResolvePtr(IntPtr state, uint addr, int isWrite);

    [LibraryImport(LibName, EntryPoint = "X86_CollectMappedPages")]
    [SuppressGCTransition]
    public static partial nuint CollectMappedPages(IntPtr state, uint addr, uint size, PageMapping* buffer,
        nuint maxCount);

    [LibraryImport(LibName, EntryPoint = "X86_AllocatePage")]
    [SuppressGCTransition]
    public static partial void* AllocatePage(IntPtr state, uint addr, byte perms);

    [LibraryImport(LibName, EntryPoint = "X86_InternalMapManagedPage")]
    [SuppressGCTransition]
    internal static partial int MapManagedPage(IntPtr state, uint addr, void* hostPage, byte perms);

    [LibraryImport(LibName, EntryPoint = "X86_MmuCreateEmpty")]
    [SuppressGCTransition]
    public static partial IntPtr MmuCreateEmpty();

    [LibraryImport(LibName, EntryPoint = "X86_MmuCloneSkipExternal")]
    [SuppressGCTransition]
    public static partial IntPtr MmuCloneSkipExternal(IntPtr mmuHandle);

    [LibraryImport(LibName, EntryPoint = "X86_MmuRetain")]
    [SuppressGCTransition]
    public static partial IntPtr MmuRetain(IntPtr mmuHandle);

    [LibraryImport(LibName, EntryPoint = "X86_MmuRelease")]
    [SuppressGCTransition]
    public static partial void MmuRelease(IntPtr mmuHandle);

    [LibraryImport(LibName, EntryPoint = "X86_MmuGetIdentity")]
    [SuppressGCTransition]
    public static partial nuint MmuGetIdentity(IntPtr mmuHandle);

    [LibraryImport(LibName, EntryPoint = "X86_EngineGetMmu")]
    [SuppressGCTransition]
    public static partial IntPtr EngineGetMmu(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_EngineDetachMmu")]
    [SuppressGCTransition]
    public static partial IntPtr EngineDetachMmu(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_EngineAttachMmu")]
    [SuppressGCTransition]
    public static partial int EngineAttachMmu(IntPtr state, IntPtr mmuHandle);

    [LibraryImport(LibName, EntryPoint = "X86_ResetAllCodeCache")]
    public static partial void ResetAllCodeCache(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_FlushMmuTlb")]
    [SuppressGCTransition]
    public static partial void FlushMmuTlb(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_ResetMemory")]
    public static partial void ResetMemory(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_ResetCodeCacheByRange")]
    public static partial void ResetCodeCacheByRange(IntPtr state, uint addr, uint size);

    [LibraryImport(LibName, EntryPoint = "X86_ReprotectMappedRange")]
    [SuppressGCTransition]
    public static partial void ReprotectMappedRange(IntPtr state, uint addr, uint size, byte perms);

    [LibraryImport(LibName, EntryPoint = "X86_SetLogCallback")]
    public static partial void SetLogCallback(IntPtr state, delegate* unmanaged<int, IntPtr, IntPtr, void> callback,
        IntPtr userdata);

    [LibraryImport(LibName, EntryPoint = "X86_DumpStats")]
    public static partial int DumpStats(IntPtr state, byte* buffer, nuint bufferSize);

    [LibraryImport(LibName, EntryPoint = "X86_GetHandlerProfileCount")]
    [SuppressGCTransition]
    public static partial nuint GetHandlerProfileCount(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_GetHandlerProfileStats")]
    [SuppressGCTransition]
    public static partial nuint GetHandlerProfileStats(IntPtr state, HandlerProfileEntry* buffer, nuint maxCount);

    [LibraryImport(LibName, EntryPoint = "X86_GetBlockStats")]
    [SuppressGCTransition]
    public static partial void GetBlockStats(IntPtr state, BlockStats* stats);

    [LibraryImport(LibName, EntryPoint = "X86_GetBlockCount")]
    [SuppressGCTransition]
    public static partial int GetBlockCount(IntPtr state);

    [LibraryImport(LibName, EntryPoint = "X86_GetBlockList")]
    public static partial int GetBlockList(IntPtr state, IntPtr* buffer, int maxCount);

    [LibraryImport(LibName, EntryPoint = "X86_GetHandlerCount")]
    [SuppressGCTransition]
    public static partial int GetHandlerCount();

    [LibraryImport(LibName, EntryPoint = "X86_GetHandlerId")]
    [SuppressGCTransition]
    public static partial int GetHandlerId(IntPtr handler);

    [LibraryImport(LibName, EntryPoint = "X86_GetHandlerById")]
    [SuppressGCTransition]
    public static partial IntPtr GetHandlerById(int handlerId);

    [LibraryImport(LibName, EntryPoint = "X86_GetHandlerSymbolById")]
    [SuppressGCTransition]
    private static partial IntPtr GetHandlerSymbolByIdUtf8(int handlerId);

    public static string? GetHandlerSymbolById(int handlerId)
    {
        var symbolPtr = GetHandlerSymbolByIdUtf8(handlerId);
        return symbolPtr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(symbolPtr);
    }

    [LibraryImport(LibName, EntryPoint = "X86_GetOpIdForHandler")]
    [SuppressGCTransition]
    public static partial int GetOpIdForHandler(IntPtr handler);

    [LibraryImport(LibName, EntryPoint = "X86_GetLibAddress")]
    public static partial IntPtr GetLibAddress();

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct DecodedOp
    {
        [FieldOffset(0)] public IntPtr handler;
        [FieldOffset(8)] public uint next_eip;
        [FieldOffset(12)] public byte len;
        [FieldOffset(13)] public byte modrm;
        [FieldOffset(14)] public byte prefixes;
        [FieldOffset(15)] public byte meta;
        [FieldOffset(16)] public uint imm;
        [FieldOffset(20)] public uint ea_desc;
        [FieldOffset(24)] public uint disp;
        [FieldOffset(28)] public uint reserved;
        [FieldOffset(24)] public IntPtr ext_ptr;
    }

    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public struct BasicBlockChainPrefix
    {
        public ulong packed;
        // Bit layout:
        // [0-31]: start_eip (32 bits)
        // [32-39]: inst_count (8 bits)
        // [40-63]: reserved (24 bits)

        public uint start_eip
        {
            get => (uint)(packed & 0xFFFFFFFF);
            set => packed = (packed & 0xFFFFFFFF00000000) | (value & 0xFFFFFFFF);
        }

        public byte inst_count
        {
            get => (byte)((packed >> 32) & 0xFF);
            set => packed = (packed & 0xFFFFFFFF00FFFFFF) | (((ulong)value & 0xFF) << 32);
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 48)]
    public struct BasicBlock
    {
        public BasicBlockChainPrefix chain;
        public IntPtr entry;
        public uint end_eip;
        public uint slot_count;
        public uint sentinel_slot_index;
        public uint branch_target_eip;
        public uint fallthrough_eip;
        public byte terminal_kind_raw;
        private byte block_padding0;
        private ushort block_padding1;
        public ulong exec_count;
        // Native BasicBlock::slots is alignas(16), so decoded ops start at offset 48.

        // Convenience properties for start_eip and inst_count
        public uint start_eip
        {
            get => chain.start_eip;
            set => chain.start_eip = value;
        }

        public byte inst_count
        {
            get => chain.inst_count;
            set => chain.inst_count = value;
        }

        public bool is_valid => (start_eip & 0x80000000u) == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PageMapping
    {
        public uint GuestPage;
        public byte Perms;
        public PageMappingFlags Flags;
        public ushort Reserved;
        public IntPtr HostPage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HandlerProfileEntry
    {
        public IntPtr Handler;
        public ulong ExecCount;
    }

    public unsafe struct BlockStats
    {
        public ulong BlockCount;
        public ulong TotalBlockInsts;
        public fixed ulong StopReasonCounts[8];
        public fixed ulong InstHistogram[65];
        public ulong BlockConcatAttempts;
        public ulong BlockConcatSuccess;
        public ulong BlockConcatSuccessDirectJmp;
        public ulong BlockConcatSuccessJccFallthrough;
        public ulong BlockConcatRejectNotConcatTerminal;
        public ulong BlockConcatRejectCrossPage;
        public ulong BlockConcatRejectSizeLimit;
        public ulong BlockConcatRejectLoop;
        public ulong BlockConcatRejectTargetMissing;
    }
}
#endif
