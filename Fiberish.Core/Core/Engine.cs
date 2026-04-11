using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Fiberish.Memory;
using Fiberish.Syscalls;
using Fiberish.X86.Native;
using X86Native = Fiberish.X86.Native.X86Native;

namespace Fiberish.Core;

public enum EngineCloneMemoryMode
{
    InlineClone = 0,
    SkipMmu = 1
}

public class Engine : IDisposable
{
    internal const uint BlockDumpMagic = 0x324b4c42; // "BLK2"
    internal const int BlockDumpFormatVersion = 2;

    private static readonly ConcurrentDictionary<nuint, ConcurrentDictionary<nint, byte>> MmuAttachmentRegistry = new();
    private MmuHandle _currentMmu = null!;
    private bool _disposed;
    private GCHandle _gcHandle;

    public unsafe Engine(MemoryRuntimeContext memoryContext)
    {
        ArgumentNullException.ThrowIfNull(memoryContext);
        MemoryContext = memoryContext;
        State = X86Native.Create();
        if (State == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create Fiberish state");

        // Create GCHandle to this object so it can be retrieved in callbacks
        _gcHandle = GCHandle.Alloc(this);

        // Register callbacks
        X86Native.SetFaultCallback(State, &OnNativeFault, GCHandle.ToIntPtr(_gcHandle));
        X86Native.SetInterruptHook(State, 0x80, &OnNativeInterrupt, GCHandle.ToIntPtr(_gcHandle));
        X86Native.SetInterruptHook(State, 3, &OnNativeInterrupt, GCHandle.ToIntPtr(_gcHandle));
        X86Native.SetInterruptHook(State, 6, &OnNativeInterrupt, GCHandle.ToIntPtr(_gcHandle));
        X86Native.SetLogCallback(State, &OnNativeLog, GCHandle.ToIntPtr(_gcHandle));
        InitializeCurrentMmuFromState();
    }

    protected Engine(bool mock)
    {
        MemoryContext = new MemoryRuntimeContext();
        if (mock)
            State = IntPtr.Zero;
        else
            throw new ArgumentException("Use parameterless constructor for real engine", nameof(mock));
    }

    internal unsafe Engine(IntPtr state, MemoryRuntimeContext memoryContext)
    {
        MemoryContext = memoryContext;
        State = state;
        _gcHandle = GCHandle.Alloc(this);
        X86Native.SetFaultCallback(State, &OnNativeFault, GCHandle.ToIntPtr(_gcHandle));
        X86Native.SetInterruptHook(State, 0x80, &OnNativeInterrupt, GCHandle.ToIntPtr(_gcHandle));
        X86Native.SetInterruptHook(State, 3, &OnNativeInterrupt, GCHandle.ToIntPtr(_gcHandle));
        X86Native.SetInterruptHook(State, 6, &OnNativeInterrupt, GCHandle.ToIntPtr(_gcHandle));
        X86Native.SetLogCallback(State, &OnNativeLog, GCHandle.ToIntPtr(_gcHandle));
        InitializeCurrentMmuFromState();
    }

    public IntPtr State { get; private set; }
    internal GCHandle GcHandle => _gcHandle;
    internal long AddressSpaceMapSequenceSeen { get; set; }
    internal nuint CurrentMmuIdentityInternal => _currentMmu.Identity;
    internal MmuHandle CurrentMmuInternal => _currentMmu.AddRefHandle();
    internal nuint CurrentMmuIdentity => CurrentMmuIdentityInternal;
    internal MmuHandle CurrentMmu => CurrentMmuInternal;

    // Callbacks
    public Func<Engine, uint, bool, bool>? FaultHandler { get; set; }
    public Func<Engine, uint, bool>? InterruptHandler { get; set; }

    public Action<Engine, int, string>? LogHandler { get; set; }

    // New resolver for synchronous fault handling during safe access
    public Func<uint, bool, bool>? PageFaultResolver { get; set; }

    // Context Owner (e.g. FiberTask) to avoid ThreadStatic lookups
    public object? Owner { get; set; }

    public MemoryRuntimeContext MemoryContext { get; }

    // Current syscall manager while this engine is executing a syscall.
    public SyscallManager? CurrentSyscallManager { get; internal set; }

    public int CurrentMmuAttachmentCount => GetAttachmentCount(_currentMmu.Identity);

    internal List<VMAManager.NativeRange> AddressSpaceInvalidationScratch { get; } = [];

    public virtual uint Eip
    {
        get => X86Native.GetEIP(State);
        set => X86Native.SetEIP(State, value);
    }

    public virtual uint Eflags
    {
        get => X86Native.GetEFLAGS(State);
        set => X86Native.SetEFLAGS(State, value);
    }

    public virtual EmuStatus Status => (EmuStatus)X86Native.GetStatus(State);

    public virtual int FaultVector => X86Native.GetFaultVector(State);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void AssertSchedulerThread([CallerMemberName] string? caller = null)
    {
        if (Owner is FiberTask task)
            task.CommonKernel.AssertSchedulerThread(caller);
    }

    private void AssertNotDisposed([CallerMemberName] string? caller = null)
    {
        if (!_disposed && State != IntPtr.Zero) return;

        var ownerDetails = Owner switch
        {
            FiberTask ownerTask =>
                $"ownerTid={ownerTask.TID} ownerPid={ownerTask.PID} ownerStatus={ownerTask.Status} ownerMode={ownerTask.ExecutionMode} ownerExited={ownerTask.Exited}",
            null => "owner=<null>",
            _ => $"ownerType={Owner.GetType().Name}"
        };
        throw new ObjectDisposedException(nameof(Engine),
            $"Engine accessed after disposal in {caller}. state=0x{State.ToInt64():X} disposed={_disposed} {ownerDetails}");
    }

    private void EnsureAddressSpaceSynchronized()
    {
        AssertNotDisposed();
        AssertSchedulerThread();
        if (Owner is not FiberTask task) return;
        ProcessAddressSpaceSync.SyncEngineBeforeRun(task.Process.Mem, this, task.Process);
    }

    private void InitializeCurrentMmuFromState()
    {
        var handle = X86Native.EngineGetMmu(State);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to get MMU handle from engine state.");
        _currentMmu = new MmuHandle(handle);
        RegisterAttachment(_currentMmu);
        Debug.Assert(_currentMmu.Identity != 0);
    }

    private bool TryRegisterAttachment(MmuHandle mmu)
    {
        var id = mmu.Identity;
        var engineKey = State;
        var engines = MmuAttachmentRegistry.GetOrAdd(id, _ => new ConcurrentDictionary<nint, byte>());
        return engines.TryAdd(engineKey, 0);
    }

    private bool TryUnregisterAttachment(MmuHandle mmu)
    {
        var id = mmu.Identity;
        var engineKey = State;
        if (!MmuAttachmentRegistry.TryGetValue(id, out var engines))
            return false;
        if (!engines.TryRemove(engineKey, out _))
            return false;
        if (engines.IsEmpty)
            MmuAttachmentRegistry.TryRemove(new KeyValuePair<nuint, ConcurrentDictionary<nint, byte>>(id, engines));
        return true;
    }

    private void RegisterAttachment(MmuHandle mmu)
    {
        var id = mmu.Identity;
        if (!TryRegisterAttachment(mmu))
            throw new InvalidOperationException($"Engine already attached to MMU {id}.");
    }

    private void UnregisterAttachment(MmuHandle mmu)
    {
        var id = mmu.Identity;
        if (!TryUnregisterAttachment(mmu))
            throw new InvalidOperationException($"Engine attachment missing for MMU {id}.");
    }

    public static int GetAttachmentCount(nuint mmuId)
    {
        return MmuAttachmentRegistry.TryGetValue(mmuId, out var engines) ? engines.Count : 0;
    }

    private bool TrySwapCurrentMmu(MmuHandle next, out string? error)
    {
        var previous = _currentMmu;
        if (previous.Identity == next.Identity)
        {
            next.Dispose();
            error = null;
            return true;
        }

        if (!TryRegisterAttachment(next))
        {
            error = $"Failed to register attachment for MMU {next.Identity}.";
            return false;
        }

        _currentMmu = next;
        if (!TryUnregisterAttachment(previous))
        {
            _currentMmu = previous;
            _ = TryUnregisterAttachment(next);
            error = $"Failed to unregister previous MMU attachment {previous.Identity}.";
            return false;
        }

        previous.Dispose();
        Debug.Assert(GetAttachmentCount(_currentMmu.Identity) > 0);
        error = null;
        return true;
    }

    [UnmanagedCallersOnly]
    private static void OnNativeLog(int level, IntPtr msgPtr, IntPtr userdata)
    {
        try
        {
            if (userdata == IntPtr.Zero) return;
            var handle = GCHandle.FromIntPtr(userdata);
            if (handle.Target is Engine engine && engine.LogHandler != null)
            {
                var msg = Marshal.PtrToStringUTF8(msgPtr);
                if (msg != null) engine.LogHandler(engine, level, msg);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Engine] Exception in OnNativeLog: {ex}");
        }
    }

    [UnmanagedCallersOnly]
    private static int OnNativeFault(IntPtr state, uint addr, int isWrite, IntPtr userdata)
    {
        try
        {
            if (userdata == IntPtr.Zero) return 0;
            var handle = GCHandle.FromIntPtr(userdata);
            if (handle.Target is Engine engine)
                return engine.FaultHandler?.Invoke(engine, addr, isWrite != 0) ?? false ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Engine] Exception in OnNativeFault: {ex}");
        }

        return 0;
    }

    [UnmanagedCallersOnly]
    internal static int OnNativeInterrupt(IntPtr state, uint vector, IntPtr userdata)
    {
        try
        {
            if (userdata == IntPtr.Zero) return 0;
            var handle = GCHandle.FromIntPtr(userdata);
            if (handle.Target is Engine engine)
            {
                if (engine.InterruptHandler != null)
                {
                    engine.InterruptHandler(engine, vector);
                    return 1; // Always return 1 (handled) as requested
                }

                return 1; // Default to 1 to avoid native fault
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Engine] Exception in OnNativeInterrupt: {ex}");
        }

        return 0;
    }

    /// <summary>
    ///     Clone engine execution state.
    ///     If <paramref name="shareMem" /> is false, MMU cloning copies owned pages and preserves External mappings.
    ///     External pages are never converted into owned pages.
    /// </summary>
    public Engine Clone(bool shareMem, EngineCloneMemoryMode memoryMode = EngineCloneMemoryMode.InlineClone)
    {
        AssertSchedulerThread();
        if (shareMem && memoryMode != EngineCloneMemoryMode.InlineClone)
            throw new ArgumentException(
                "shareMem=true only supports EngineCloneMemoryMode.InlineClone.",
                nameof(memoryMode));

        var skipMmu = !shareMem && memoryMode == EngineCloneMemoryMode.SkipMmu;
        var newState = X86Native.Clone(State, shareMem ? 1 : 0);
        if (newState == IntPtr.Zero)
            throw new InvalidOperationException("Failed to clone Fiberish state.");

        var newEngine = new Engine(newState, MemoryContext)
        {
            FaultHandler = FaultHandler,
            InterruptHandler = InterruptHandler,
            PageFaultResolver = PageFaultResolver,
            LogHandler = LogHandler // Copy the handler delegate
        };

        if (skipMmu)
        {
            using var detached = newEngine.DetachOwnedMmuHandle();
        }

        return newEngine;
    }

    internal void ShareMmuFrom(Engine source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var handle = source.CurrentMmuInternal;
        AttachOwnedMmuHandle(handle);
    }

    internal void AttachOwnedMmuHandle(MmuHandle mmu)
    {
        AssertSchedulerThread();
        ArgumentNullException.ThrowIfNull(mmu);
        mmu.ThrowIfInvalid();
        if (mmu.Identity == _currentMmu.Identity) return;

        using var previous = _currentMmu.AddRefHandle();
        MmuHandle? nextOwned = null;
        try
        {
            nextOwned = mmu.AddRefHandle();
            var attached = X86Native.EngineAttachMmu(State, mmu.DangerousMmuHandle());
            if (attached == 0)
                throw new InvalidOperationException("Failed to attach MMU to engine.");

            if (!TrySwapCurrentMmu(nextOwned, out var swapError))
            {
                var rollbackRc = X86Native.EngineAttachMmu(State, previous.DangerousMmuHandle());
                if (rollbackRc == 0)
                    throw new InvalidOperationException(
                        $"MMU attach swap failed and rollback failed: {swapError}");
                Debug.Assert(_currentMmu.Identity == previous.Identity);
                throw new InvalidOperationException(
                    $"MMU attach succeeded but managed state swap failed: {swapError}");
            }

            nextOwned = null;
        }
        finally
        {
            nextOwned?.Dispose();
        }
    }

    internal MmuHandle CaptureCurrentMmuHandle()
    {
        AssertSchedulerThread();
        return _currentMmu.AddRefHandle();
    }

    internal void ReplaceMmu(MmuHandle mmu)
    {
        AttachOwnedMmuHandle(mmu);
    }

    internal MmuHandle DetachOwnedMmuHandle()
    {
        AssertSchedulerThread();
        using var previous = _currentMmu.AddRefHandle();
        var detachedHandle = X86Native.EngineDetachMmu(State);
        if (detachedHandle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to detach MMU from engine.");

        var nextCurrentRaw = X86Native.EngineGetMmu(State);
        if (nextCurrentRaw == IntPtr.Zero)
        {
            X86Native.MmuRelease(detachedHandle);
            throw new InvalidOperationException("Detached MMU but failed to fetch replacement MMU.");
        }

        var detached = new MmuHandle(detachedHandle);
        var nextCurrent = new MmuHandle(nextCurrentRaw);
        if (!TrySwapCurrentMmu(nextCurrent, out var swapError))
        {
            var rollbackRc = X86Native.EngineAttachMmu(State, previous.DangerousMmuHandle());
            nextCurrent.Dispose();
            detached.Dispose();
            if (rollbackRc == 0)
                throw new InvalidOperationException(
                    $"MMU detach swap failed and rollback failed: {swapError}");
            Debug.Assert(_currentMmu.Identity == previous.Identity);
            throw new InvalidOperationException(
                $"MMU detach succeeded but managed state swap failed: {swapError}");
        }

        return detached;
    }

    internal MmuHandle DetachMmu()
    {
        return DetachOwnedMmuHandle();
    }

    public void MemMap(uint addr, uint size, byte perms)
    {
        X86Native.MemMap(State, addr, size, perms);
    }

    public void MemUnmap(uint addr, uint size)
    {
        X86Native.MemUnmap(State, addr, size);
    }

    /// <summary>
    ///     Allocate a single page with given permissions and return host pointer.
    ///     Unlike MemMap, this also allocates the actual page memory.
    /// </summary>
    public unsafe IntPtr AllocatePage(uint addr, byte perms)
    {
        return (IntPtr)X86Native.AllocatePage(State, addr, perms);
    }

    internal unsafe bool MapManagedPage(uint addr, IntPtr hostPage, byte perms)
    {
        return X86Native.MapManagedPage(State, addr, (void*)hostPage, perms) != 0;
    }

    public unsafe IntPtr GetPhysicalAddressSafe(uint vaddr, bool isWrite)
    {
        EnsureAddressSpaceSynchronized();
        return (IntPtr)(isWrite ? X86Native.ResolvePtrForWrite(State, vaddr) : X86Native.ResolvePtrForRead(State, vaddr));
    }

    public unsafe bool TryGetWritableUserBuffer(uint vaddr, int maxLen, out Span<byte> buffer)
    {
        EnsureAddressSpaceSynchronized();

        if (maxLen < 0)
        {
            buffer = default;
            return false;
        }

        if (maxLen == 0)
        {
            buffer = Span<byte>.Empty;
            return true;
        }

        var ptr = X86Native.ResolvePtrForWrite(State, vaddr);
        if (ptr == null && TryResolveUserAccessFault(vaddr, true))
            ptr = X86Native.ResolvePtrForWrite(State, vaddr);

        if (ptr == null)
        {
            buffer = default;
            return false;
        }

        var pageOffset = vaddr & 0xFFF;
        var chunkLen = Math.Min(maxLen, 4096 - (int)pageOffset);
        buffer = new Span<byte>((byte*)ptr, chunkLen);
        return true;
    }

    public unsafe bool CopyToUser(uint vaddr, ReadOnlySpan<byte> data)
    {
        EnsureAddressSpaceSynchronized();
        var len = data.Length;
        var written = 0;

        fixed (byte* pData = data)
        {
            while (written < len)
            {
                var currAddr = vaddr + (uint)written;
                var ptr = X86Native.ResolvePtrForWrite(State, currAddr);
                if (ptr == null)
                    if (TryResolveUserAccessFault(currAddr, true))
                        ptr = X86Native.ResolvePtrForWrite(State, currAddr);

                if (ptr == null) return false;

                var pageOffset = currAddr & 0xFFF;
                var chunk = Math.Min(len - written, 4096 - (int)pageOffset);

                Buffer.MemoryCopy(pData + written, (byte*)ptr, chunk, chunk);
                written += chunk;
            }
        }

        return true;
    }

    public unsafe bool CopyFromUser(uint vaddr, Span<byte> data)
    {
        EnsureAddressSpaceSynchronized();
        var len = data.Length;
        var read = 0;

        fixed (byte* pData = data)
        {
            while (read < len)
            {
                var currAddr = vaddr + (uint)read;
                var ptr = X86Native.ResolvePtrForRead(State, currAddr);
                if (ptr == null)
                    if (TryResolveUserAccessFault(currAddr, false))
                        ptr = X86Native.ResolvePtrForRead(State, currAddr);

                if (ptr == null) return false;

                var pageOffset = currAddr & 0xFFF;
                var chunk = Math.Min(len - read, 4096 - (int)pageOffset);

                Buffer.MemoryCopy(ptr, pData + read, chunk, chunk);
                read += chunk;
            }
        }

        return true;
    }

    private bool TryResolveUserAccessFault(uint addr, bool isWrite)
    {
        // While servicing a guest syscall, invalid user pointers should surface as
        // -EFAULT to the guest rather than posting SIGSEGV via FiberTask's regular
        // page-fault path. We still fault in valid-but-not-yet-mapped pages here.
        if (CurrentSyscallManager?.Mem != null)
            return CurrentSyscallManager.Mem.HandleFaultDetailed(addr, isWrite, this) == FaultResult.Handled;

        return PageFaultResolver != null && PageFaultResolver(addr, isWrite);
    }

    /// <summary>
    ///     Best-effort read from guest memory without invoking PageFaultResolver.
    ///     Returns the number of bytes copied before hitting an unmapped/inaccessible range.
    /// </summary>
    public unsafe int CopyFromUserNoFault(uint vaddr, Span<byte> data)
    {
        EnsureAddressSpaceSynchronized();
        var len = data.Length;
        var read = 0;

        fixed (byte* pData = data)
        {
            while (read < len)
            {
                var currAddr = vaddr + (uint)read;
                var ptr = X86Native.ResolvePtrForRead(State, currAddr);
                if (ptr == null) return read;

                var pageOffset = currAddr & 0xFFF;
                var chunk = Math.Min(len - read, 4096 - (int)pageOffset);

                Buffer.MemoryCopy(ptr, pData + read, chunk, chunk);
                read += chunk;
            }
        }

        return read;
    }

    public unsafe string? ReadStringSafe(uint addr, int limit = 4096)
    {
        EnsureAddressSpaceSynchronized();
        if (addr == 0)
            return "";
        if (limit <= 0)
            return string.Empty;

        var rented = ArrayPool<byte>.Shared.Rent(limit);
        var current = addr;
        var length = 0;

        try
        {
            while (length < limit)
            {
                var ptr = X86Native.ResolvePtrForRead(State, current);
                if (ptr == null)
                    if (TryResolveUserAccessFault(current, false))
                        ptr = X86Native.ResolvePtrForRead(State, current);

                if (ptr == null)
                    return null;

                // NOTE: X86_ResolvePtrForRead/resolve_safe_for_read returns a pointer with the guest-page offset already applied.
                var pageOffset = current & 0xFFF;
                var remainingInPage = 4096 - (int)pageOffset;
                var chunkLength = Math.Min(limit - length, remainingInPage);
                var source = new ReadOnlySpan<byte>((byte*)ptr, remainingInPage);
                var chunk = source[..chunkLength];
                var nulIndex = chunk.IndexOf((byte)0);
                if (nulIndex >= 0)
                {
                    chunk[..nulIndex].CopyTo(rented.AsSpan(length));
                    length += nulIndex;
                    return Encoding.UTF8.GetString(rented.AsSpan(0, length));
                }

                chunk.CopyTo(rented.AsSpan(length));
                length += chunkLength;
                current += (uint)chunkLength;
            }

            return Encoding.UTF8.GetString(rented.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    public virtual uint RegRead(Reg reg)
    {
        AssertNotDisposed();
        return X86Native.RegRead(State, (int)reg);
    }

    public virtual void RegWrite(Reg reg, uint val)
    {
        AssertNotDisposed();
        X86Native.RegWrite(State, (int)reg, val);
    }

    public void SetSegBase(Seg seg, uint baseAddr)
    {
        AssertNotDisposed();
        X86Native.SegBaseWrite(State, (int)seg, baseAddr);
    }

    public uint GetSegBase(Seg seg)
    {
        AssertNotDisposed();
        return X86Native.SegBaseRead(State, (int)seg);
    }

    public virtual void Run(uint endEip = 0, ulong maxInsts = 0)
    {
        AssertNotDisposed();
        X86Native.Run(State, endEip, maxInsts);
    }

    public virtual void Stop()
    {
        AssertNotDisposed();
        X86Native.EmuStop(State);
    }

    public virtual void SetStatusFault()
    {
        AssertNotDisposed();
        X86Native.EmuFault(State);
    }

    public virtual void Yield()
    {
        AssertNotDisposed();
        X86Native.EmuYield(State);
    }

    public virtual int Step()
    {
        AssertNotDisposed();
        return X86Native.Step(State);
    }

    public virtual bool IsDirty(uint addr)
    {
        AssertNotDisposed();
        return X86Native.IsDirty(State, addr) != 0;
    }

    public unsafe int CollectMappedPages(uint addr, uint size, Span<X86Native.PageMapping> buffer)
    {
        EnsureAddressSpaceSynchronized();
        if (size == 0 || buffer.Length == 0) return 0;
        fixed (X86Native.PageMapping* pBuffer = buffer)
        {
            return (int)X86Native.CollectMappedPages(State, addr, size, pBuffer, (nuint)buffer.Length);
        }
    }

    public bool HasMappedPage(uint addr, uint size)
    {
        Span<X86Native.PageMapping> page = stackalloc X86Native.PageMapping[1];
        return CollectMappedPages(addr, size, page) > 0;
    }

    public void ResetCodeCacheByRange(uint addr, uint size)
    {
        AssertNotDisposed();
        X86Native.ResetCodeCacheByRange(State, addr, size);
    }

    public unsafe void InvalidateCodeCacheHostPages(ReadOnlySpan<nint> hostPages)
    {
        AssertNotDisposed();
        if (hostPages.Length == 0) return;

        fixed (nint* pHostPages = hostPages)
        {
            X86Native.InvalidateCodeCacheHostPages(State, (IntPtr*)pHostPages, (nuint)hostPages.Length);
        }
    }

    public void FlushMmuTlbOnly()
    {
        AssertNotDisposed();
        X86Native.FlushMmuTlb(State);
    }

    public void ReprotectMappedRange(uint addr, uint size, byte perms)
    {
        AssertNotDisposed();
        X86Native.ReprotectMappedRange(State, addr, size, perms);
    }

    public void ResetAllCodeCache()
    {
        AssertNotDisposed();
        X86Native.ResetAllCodeCache(State);
    }

    /// <summary>
    ///     Reset entire native MMU page directory + translated block cache.
    ///     Used during execve to clear all stale native pages before loading new binary.
    /// </summary>
    public void ResetMemory()
    {
        AssertNotDisposed();
        X86Native.ResetMemory(State);
    }

    public unsafe string? DumpStats()
    {
        var buf = new byte[1024];
        fixed (byte* p = buf)
        {
            var n = X86Native.DumpStats(State, p, 1024);
            if (n < 0) return null;
            return Encoding.UTF8.GetString(buf, 0, n);
        }
    }

    public unsafe HandlerProfileStat[] GetHandlerProfileStats()
    {
        var count = (int)X86Native.GetHandlerProfileCount(State);
        if (count <= 0) return Array.Empty<HandlerProfileStat>();

        var buffer = new X86Native.HandlerProfileEntry[count];
        fixed (X86Native.HandlerProfileEntry* p = buffer)
        {
            var actual = (int)X86Native.GetHandlerProfileStats(State, p, (nuint)buffer.Length);
            if (actual <= 0) return Array.Empty<HandlerProfileStat>();
            if (actual < buffer.Length) Array.Resize(ref buffer, actual);
        }

        var result = new HandlerProfileStat[buffer.Length];
        for (var i = 0; i < buffer.Length; i++)
            result[i] = new HandlerProfileStat(
                buffer[i].Handler,
                buffer[i].ExecCount);

        return result;
    }

    public unsafe BlockStatsSnapshot GetBlockStats()
    {
        X86Native.BlockStats native = default;
        X86Native.GetBlockStats(State, &native);

        var stopReasonCounts = new ulong[8];
        var instHistogram = new ulong[65];
        for (var i = 0; i < stopReasonCounts.Length; i++)
            stopReasonCounts[i] = native.StopReasonCounts[i];
        for (var i = 0; i < instHistogram.Length; i++)
            instHistogram[i] = native.InstHistogram[i];

        return new BlockStatsSnapshot(
            native.BlockCount,
            native.TotalBlockInsts,
            stopReasonCounts,
            instHistogram,
            native.BlockConcatAttempts,
            native.BlockConcatSuccess,
            native.BlockConcatSuccessDirectJmp,
            native.BlockConcatSuccessJccFallthrough,
            native.BlockConcatRejectNotConcatTerminal,
            native.BlockConcatRejectCrossPage,
            native.BlockConcatRejectSizeLimit,
            native.BlockConcatRejectLoop,
            native.BlockConcatRejectTargetMissing);
    }

    public int GetBlockCount()
    {
        return X86Native.GetBlockCount(State);
    }

    public unsafe IntPtr[] GetBlockPointers()
    {
        var count = X86Native.GetBlockCount(State);
        if (count <= 0) return Array.Empty<IntPtr>();

        var buffer = new IntPtr[count];
        fixed (IntPtr* pBuffer = buffer)
        {
            var actual = X86Native.GetBlockList(State, pBuffer, buffer.Length);
            if (actual <= 0) return Array.Empty<IntPtr>();
            if (actual < buffer.Length) Array.Resize(ref buffer, actual);
        }

        return buffer;
    }

    public unsafe void DumpBlocks(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        var imageBase = GetNativeImageBase().ToInt64();
        var blocks = GetBlockPointers();
        var handlerTable = BuildBlockDumpHandlerTable();

        writer.Write(BlockDumpMagic);
        writer.Write(BlockDumpFormatVersion);
        writer.Write((ulong)imageBase);
        writer.Write(blocks.Length);
        writer.Write(handlerTable.Count);
        foreach (var handler in handlerTable)
        {
            writer.Write(handler.HandlerId);
            writer.Write(handler.OpId);
            writer.Write(unchecked((ulong)handler.HandlerPtr.ToInt64()));
            WriteLengthPrefixedUtf8(writer, handler.Symbol);
        }

        var handlerIdsByPtr = new Dictionary<nint, int>(handlerTable.Count);
        foreach (var handler in handlerTable)
            if (handler.HandlerPtr != IntPtr.Zero)
                handlerIdsByPtr[handler.HandlerPtr] = handler.HandlerId;

        foreach (var blockPtr in blocks)
        {
            if (blockPtr == IntPtr.Zero) continue;

            var nativeBlock = (X86Native.BasicBlock*)blockPtr;
            var startEip = nativeBlock->start_eip;
            var instCount = (uint)nativeBlock->inst_count;
            writer.Write(startEip);
            writer.Write(nativeBlock->end_eip);
            writer.Write(instCount);
            writer.Write(nativeBlock->exec_count);

            var ops = (X86Native.DecodedOp*)((byte*)nativeBlock + sizeof(X86Native.BasicBlock));
            for (var i = 0; i < instCount; i++)
            {
                var op = ops[i];
                var memPacked = PackDumpMem(op);
                writer.Write(memPacked);
                writer.Write(op.next_eip);
                writer.Write(op.len);
                writer.Write(op.modrm);
                writer.Write(op.prefixes);
                writer.Write(op.meta);
                writer.Write(ExtractDumpImm(op));
                writer.Write(0u);
                writer.Write(op.handler.ToInt64());
                var handlerId = handlerIdsByPtr.TryGetValue(op.handler, out var knownHandlerId)
                    ? knownHandlerId
                    : X86Native.GetHandlerId(op.handler);
                writer.Write(handlerId);
            }
        }
    }

    internal static void WriteLengthPrefixedUtf8(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    internal static List<BlockDumpHandlerEntry> BuildBlockDumpHandlerTable()
    {
        var handlerCount = X86Native.GetHandlerCount();
        if (handlerCount <= 0)
            return [];

        var handlers = new List<BlockDumpHandlerEntry>(handlerCount);
        for (var handlerId = 0; handlerId < handlerCount; handlerId++)
        {
            var handlerPtr = X86Native.GetHandlerById(handlerId);
            var symbol = X86Native.GetHandlerSymbolById(handlerId) ?? string.Empty;
            var opId = handlerPtr != IntPtr.Zero ? X86Native.GetOpIdForHandler(handlerPtr) : -1;
            handlers.Add(new BlockDumpHandlerEntry(handlerId, handlerPtr, opId, symbol));
        }

        return handlers;
    }

    internal static ulong PackDumpMem(X86Native.DecodedOp op)
    {
        if (!HasMem(op.meta))
            return 0;
        return ((ulong)op.ea_desc << 32) | op.disp;
    }

    internal static uint ExtractDumpImm(X86Native.DecodedOp op)
    {
        return HasImm(op.meta) ? op.imm : 0u;
    }

    private static bool HasMem(byte meta)
    {
        return (meta & (1 << 1)) != 0;
    }

    private static bool HasImm(byte meta)
    {
        return (meta & (1 << 2)) != 0;
    }

    public IntPtr GetNativeImageBase()
    {
        return X86Native.GetLibAddress();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (_currentMmu != null && !_currentMmu.IsClosed)
            {
                try
                {
                    if (State != IntPtr.Zero) UnregisterAttachment(_currentMmu);
                }
                catch
                {
                    // best-effort during dispose/finalize
                }

                _currentMmu.Dispose();
            }

            if (State != IntPtr.Zero)
            {
                X86Native.Destroy(State);
                State = IntPtr.Zero;
            }

            if (_gcHandle.IsAllocated) _gcHandle.Free();
            _disposed = true;
        }
    }

    public override string ToString()
    {
        return
            $"EIP: 0x{Eip:x8} ESP: 0x{RegRead(Reg.ESP):x8} EAX: 0x{RegRead(Reg.EAX):x8} EBX: 0x{RegRead(Reg.EBX):x8} ECX: 0x{RegRead(Reg.ECX):x8} EDX: 0x{RegRead(Reg.EDX):x8} ESI: 0x{RegRead(Reg.ESI):x8} EDI: 0x{RegRead(Reg.EDI):x8} EBP: 0x{RegRead(Reg.EBP):x8} EFLAGS: 0x{Eflags:x8}";
    }

    ~Engine()
    {
        Dispose(false);
    }

    internal sealed record BlockDumpHandlerEntry(int HandlerId, IntPtr HandlerPtr, int OpId, string Symbol);
}

public readonly record struct HandlerProfileStat(IntPtr Handler, ulong ExecCount);

public readonly record struct BlockStatsSnapshot(
    ulong BlockCount,
    ulong TotalBlockInsts,
    ulong[] StopReasonCounts,
    ulong[] InstHistogram,
    ulong BlockConcatAttempts,
    ulong BlockConcatSuccess,
    ulong BlockConcatSuccessDirectJmp,
    ulong BlockConcatSuccessJccFallthrough,
    ulong BlockConcatRejectNotConcatTerminal,
    ulong BlockConcatRejectCrossPage,
    ulong BlockConcatRejectSizeLimit,
    ulong BlockConcatRejectLoop,
    ulong BlockConcatRejectTargetMissing);
