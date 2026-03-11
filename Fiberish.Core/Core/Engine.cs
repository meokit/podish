using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Diagnostics;
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
    private bool _disposed;
    private GCHandle _gcHandle;
    private MmuHandle _currentMmu = null!;
    private static readonly ConcurrentDictionary<nuint, ConcurrentDictionary<nint, byte>> MmuAttachmentRegistry = new();

    public unsafe Engine()
    {
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
        if (mock)
            State = IntPtr.Zero;
        else
            throw new ArgumentException("Use parameterless constructor for real engine", nameof(mock));
    }

    internal unsafe Engine(IntPtr state)
    {
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
    public nuint CurrentMmuIdentity => _currentMmu.Identity;
    public MmuHandle CurrentMmu => _currentMmu.AddRefHandle();

    // Callbacks
    public Func<Engine, uint, bool, bool>? FaultHandler { get; set; }
    public Func<Engine, uint, bool>? InterruptHandler { get; set; }

    public Action<Engine, int, string>? LogHandler { get; set; }

    // New resolver for synchronous fault handling during safe access
    public Func<uint, bool, bool>? PageFaultResolver { get; set; }

    // Context Owner (e.g. FiberTask) to avoid ThreadStatic lookups
    public object? Owner { get; set; }

    private void AssertSchedulerThread([CallerMemberName] string? caller = null)
    {
        if (Owner is FiberTask task)
            task.CommonKernel.AssertSchedulerThread(caller);
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

    private void RegisterAttachment(MmuHandle mmu)
    {
        var id = mmu.Identity;
        var engineKey = (nint)State;
        var engines = MmuAttachmentRegistry.GetOrAdd(id, _ => new ConcurrentDictionary<nint, byte>());

        if (!engines.TryAdd(engineKey, 0))
            throw new InvalidOperationException($"Engine already attached to MMU {id}.");
    }

    private void UnregisterAttachment(MmuHandle mmu)
    {
        var id = mmu.Identity;
        var engineKey = (nint)State;
        if (!MmuAttachmentRegistry.TryGetValue(id, out var engines))
            throw new InvalidOperationException($"MMU {id} attachment registry entry is missing.");
        if (!engines.TryRemove(engineKey, out _))
            throw new InvalidOperationException($"Engine attachment missing for MMU {id}.");
        if (engines.IsEmpty)
            MmuAttachmentRegistry.TryRemove(new KeyValuePair<nuint, ConcurrentDictionary<nint, byte>>(id, engines));
    }

    public static int GetAttachmentCount(nuint mmuId)
    {
        return MmuAttachmentRegistry.TryGetValue(mmuId, out var engines) ? engines.Count : 0;
    }

    public int CurrentMmuAttachmentCount => GetAttachmentCount(_currentMmu.Identity);

    private void SwapCurrentMmu(MmuHandle next)
    {
        var previous = _currentMmu;
        if (previous.Identity == next.Identity)
        {
            next.Dispose();
            return;
        }

        RegisterAttachment(next);
        _currentMmu = next;
        UnregisterAttachment(previous);
        previous.Dispose();

        Debug.Assert(GetAttachmentCount(_currentMmu.Identity) > 0);
    }

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
        Console.WriteLine($"[Engine 0x{State:x}] Disposing... \n{Environment.StackTrace}");
        Dispose(true);
        Console.WriteLine("[Engine] Disposed.");
        GC.SuppressFinalize(this);
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

    public Engine Clone(bool shareMem)
    {
        return Clone(shareMem, EngineCloneMemoryMode.InlineClone);
    }

    /// <summary>
    ///     Clone engine execution state.
    ///     If <paramref name="shareMem"/> is false, MMU cloning always skips External pages.
    ///     External pages are never converted into owned pages.
    /// </summary>
    public Engine Clone(bool shareMem, EngineCloneMemoryMode memoryMode)
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

        var newEngine = new Engine(newState)
        {
            FaultHandler = FaultHandler,
            InterruptHandler = InterruptHandler,
            PageFaultResolver = PageFaultResolver,
            LogHandler = LogHandler // Copy the handler delegate
        };

        if (skipMmu)
        {
            using var detached = newEngine.DetachMmu();
        }

        return newEngine;
    }

    public void ShareMmuFrom(Engine source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var handle = source.CurrentMmu;
        ReplaceMmu(handle);
    }

    public void ReplaceMmu(MmuHandle mmu)
    {
        AssertSchedulerThread();
        ArgumentNullException.ThrowIfNull(mmu);
        mmu.ThrowIfInvalid();
        if (mmu.Identity == _currentMmu.Identity) return;

        MmuHandle? nextOwned = null;
        try
        {
            nextOwned = mmu.AddRefHandle();
            var attached = X86Native.EngineAttachMmu(State, mmu.DangerousMmuHandle());
            if (attached == 0)
                throw new InvalidOperationException("Failed to attach MMU to engine.");
            SwapCurrentMmu(nextOwned);
            nextOwned = null;
        }
        finally
        {
            nextOwned?.Dispose();
        }
    }

    public MmuHandle DetachMmu()
    {
        AssertSchedulerThread();
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
        try
        {
            SwapCurrentMmu(nextCurrent);
            return detached;
        }
        catch
        {
            nextCurrent.Dispose();
            detached.Dispose();
            throw;
        }
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

    /// <summary>
    ///     Map external memory to guest address. Caller owns the memory.
    ///     Useful for mmap passthrough, shared memory, etc.
    /// </summary>
    public unsafe bool MapExternalPage(uint addr, IntPtr externalPage, byte perms)
    {
        return X86Native.MapExternalPage(State, addr, (void*)externalPage, perms) != 0;
    }

    /// <summary>
    ///     DEPRECATED: Use CopyToUser instead. This is slow (byte-by-byte native calls) and has recursive fault risk.
    /// </summary>
    [Obsolete("Use CopyToUser instead. MemWrite is slow and risks recursive faults.")]
    public unsafe void MemWrite(uint addr, ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            X86Native.MemWrite(State, addr, p, (uint)data.Length);
        }
    }

    /// <summary>
    ///     DEPRECATED: Use CopyFromUser instead. This is slow (byte-by-byte native calls) and has recursive fault risk.
    /// </summary>
    [Obsolete("Use CopyFromUser instead. MemRead is slow and risks recursive faults.")]
    public unsafe byte[] MemRead(uint addr, uint size)
    {
        var buf = new byte[size];
        fixed (byte* p = buf)
        {
            X86Native.MemRead(State, addr, p, size);
        }

        return buf;
    }

    public unsafe IntPtr GetPhysicalAddressSafe(uint vaddr, bool isWrite)
    {
        return (IntPtr)X86Native.ResolvePtr(State, vaddr, isWrite ? 1 : 0);
    }

    public IntPtr GetPhysicalAddress(uint vaddr)
    {
        return GetPhysicalAddressSafe(vaddr, false);
    }

    public unsafe bool CopyToUser(uint vaddr, ReadOnlySpan<byte> data)
    {
        var len = data.Length;
        var written = 0;

        fixed (byte* pData = data)
        {
            while (written < len)
            {
                var currAddr = vaddr + (uint)written;
                var ptr = X86Native.ResolvePtr(State, currAddr, 1); // 1 = Write
                if (ptr == null)
                    if (PageFaultResolver != null && PageFaultResolver(currAddr, true))
                        ptr = X86Native.ResolvePtr(State, currAddr, 1);

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
        var len = data.Length;
        var read = 0;

        fixed (byte* pData = data)
        {
            while (read < len)
            {
                var currAddr = vaddr + (uint)read;
                var ptr = X86Native.ResolvePtr(State, currAddr, 0); // 0 = Read
                if (ptr == null)
                    if (PageFaultResolver != null && PageFaultResolver(currAddr, false))
                        ptr = X86Native.ResolvePtr(State, currAddr, 0);

                if (ptr == null) return false;

                var pageOffset = currAddr & 0xFFF;
                var chunk = Math.Min(len - read, 4096 - (int)pageOffset);

                Buffer.MemoryCopy(ptr, pData + read, chunk, chunk);
                read += chunk;
            }
        }

        return true;
    }

    /// <summary>
    ///     Best-effort read from guest memory without invoking PageFaultResolver.
    ///     Returns the number of bytes copied before hitting an unmapped/inaccessible range.
    /// </summary>
    public unsafe int CopyFromUserNoFault(uint vaddr, Span<byte> data)
    {
        var len = data.Length;
        var read = 0;

        fixed (byte* pData = data)
        {
            while (read < len)
            {
                var currAddr = vaddr + (uint)read;
                var ptr = X86Native.ResolvePtr(State, currAddr, 0); // 0 = Read
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
        if (addr == 0) return "";

        var sb = new StringBuilder();
        var current = addr;

        while (sb.Length < limit)
        {
            var ptr = X86Native.ResolvePtr(State, current, 0); // Read
            if (ptr == null)
                if (PageFaultResolver != null && PageFaultResolver(current, false))
                    ptr = X86Native.ResolvePtr(State, current, 0);

            if (ptr == null) return null; // Fault

            // NOTE: X86_ResolvePtr/resolve_safe returns pointer with offset already applied
            // So ptr is the exact byte pointer for 'current' address
            var pageOffset = current & 0xFFF;
            var p = (byte*)ptr; // Already includes offset
            var remainingInPage = 4096 - (int)pageOffset;

            for (var i = 0; i < remainingInPage; i++)
            {
                var b = p[i];
                if (b == 0) return sb.ToString();
                sb.Append((char)b);
                if (sb.Length >= limit) break;
            }

            current += (uint)remainingInPage;
        }

        return sb.ToString();
    }

    public virtual uint RegRead(Reg reg)
    {
        return X86Native.RegRead(State, (int)reg);
    }

    public virtual void RegWrite(Reg reg, uint val)
    {
        X86Native.RegWrite(State, (int)reg, val);
    }

    public void SetSegBase(Seg seg, uint baseAddr)
    {
        X86Native.SegBaseWrite(State, (int)seg, baseAddr);
    }

    public uint GetSegBase(Seg seg)
    {
        return X86Native.SegBaseRead(State, (int)seg);
    }

    public virtual void Run(uint endEip = 0, ulong maxInsts = 0)
    {
        X86Native.Run(State, endEip, maxInsts);
    }

    public virtual void Stop()
    {
        X86Native.EmuStop(State);
    }

    public virtual void SetStatusFault()
    {
        X86Native.EmuFault(State);
    }

    public virtual void Yield()
    {
        X86Native.EmuYield(State);
    }

    public virtual int Step()
    {
        return X86Native.Step(State);
    }

    public virtual bool IsDirty(uint addr)
    {
        return X86Native.IsDirty(State, addr) != 0;
    }

    public unsafe int CollectMappedPages(uint addr, uint size, Span<X86Native.PageMapping> buffer)
    {
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

    public void InvalidateRange(uint addr, uint size)
    {
        X86Native.InvalidateRange(State, addr, size);
    }

    public void FlushCache()
    {
        X86Native.FlushCache(State);
    }

    /// <summary>
    /// Reset entire native MMU page directory + JIT cache.
    /// Used during execve to clear all stale native pages before loading new binary.
    /// </summary>
    public void ResetMemory()
    {
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
}
