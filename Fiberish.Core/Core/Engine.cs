using System.Runtime.InteropServices;
using System.Text;
using Fiberish.X86.Native;
using X86Native = Fiberish.X86.Native.X86Native;

namespace Fiberish.Core;

public enum MmuCloneMode
{
    Full = 0,
    SkipExternal = 1
}

public enum EngineCloneMemoryMode
{
    InlineClone = 0,
    SkipMmu = 1
}

public readonly struct MmuRef
{
    internal MmuRef(X86Native.MmuRef nativeRef)
    {
        NativeRef = nativeRef;
    }

    internal X86Native.MmuRef NativeRef { get; }
    public IntPtr State => NativeRef.State;
    public nuint Identity => NativeRef.MmuIdentity;
    public bool IsValid => State != IntPtr.Zero && Identity != 0;
}

public class Engine : IDisposable
{
    public sealed class DetachedMmu : IDisposable
    {
        private IntPtr _handle;
        private bool _consumed;

        internal DetachedMmu(IntPtr handle)
        {
            _handle = handle;
        }

        public bool IsConsumed => _consumed;

        public MmuCloneMode GetCloneMode()
        {
            if (_consumed || _handle == IntPtr.Zero)
                throw new InvalidOperationException("Detached MMU handle is no longer valid.");

            var cloneMode = X86Native.DetachedMmuGetCloneMode(_handle);
            return cloneMode switch
            {
                0 => MmuCloneMode.Full,
                1 => MmuCloneMode.SkipExternal,
                _ => throw new InvalidOperationException($"Invalid detached MMU clone mode: {cloneMode}.")
            };
        }

        internal IntPtr ConsumeHandle()
        {
            if (_consumed || _handle == IntPtr.Zero)
                throw new InvalidOperationException("Detached MMU handle is no longer valid.");

            _consumed = true;
            var handle = _handle;
            _handle = IntPtr.Zero;
            return handle;
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            X86Native.DestroyDetachedMmu(_handle);
            _handle = IntPtr.Zero;
            _consumed = true;
        }
    }

    private bool _disposed;
    private GCHandle _gcHandle;

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
    }

    public IntPtr State { get; private set; }
    internal GCHandle GcHandle => _gcHandle;

    // Callbacks
    public Func<Engine, uint, bool, bool>? FaultHandler { get; set; }
    public Func<Engine, uint, bool>? InterruptHandler { get; set; }

    public Action<Engine, int, string>? LogHandler { get; set; }

    // New resolver for synchronous fault handling during safe access
    public Func<uint, bool, bool>? PageFaultResolver { get; set; }

    // Context Owner (e.g. FiberTask) to avoid ThreadStatic lookups
    public object? Owner { get; set; }

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
        return Clone(shareMem, MmuCloneMode.Full, EngineCloneMemoryMode.InlineClone);
    }

    public Engine Clone(bool shareMem, MmuCloneMode mmuCloneMode)
    {
        return Clone(shareMem, mmuCloneMode, EngineCloneMemoryMode.InlineClone);
    }

    public Engine Clone(bool shareMem, EngineCloneMemoryMode memoryMode)
    {
        return Clone(shareMem, MmuCloneMode.Full, memoryMode);
    }

    public Engine Clone(bool shareMem, MmuCloneMode mmuCloneMode, EngineCloneMemoryMode memoryMode)
    {
        _ = ToNativeMmuCloneMode(mmuCloneMode);
        if (shareMem && mmuCloneMode != MmuCloneMode.Full)
            throw new ArgumentException("shareMem=true only supports MmuCloneMode.Full.", nameof(mmuCloneMode));
        if (shareMem && memoryMode != EngineCloneMemoryMode.InlineClone)
            throw new ArgumentException("shareMem=true only supports EngineCloneMemoryMode.InlineClone.",
                nameof(memoryMode));
        if (memoryMode == EngineCloneMemoryMode.SkipMmu && mmuCloneMode != MmuCloneMode.Full)
            throw new ArgumentException("SkipMmu mode requires MmuCloneMode.Full.", nameof(mmuCloneMode));

        var customMmuClone = !shareMem && mmuCloneMode != MmuCloneMode.Full;
        var skipMmu = !shareMem && memoryMode == EngineCloneMemoryMode.SkipMmu;
        var shareMemForNativeClone = shareMem || customMmuClone || skipMmu;
        var newState = X86Native.Clone(State, shareMemForNativeClone ? 1 : 0);
        if (newState == IntPtr.Zero)
            throw new InvalidOperationException("Failed to clone Fiberish state.");

        var newEngine = new Engine(newState)
        {
            FaultHandler = FaultHandler,
            InterruptHandler = InterruptHandler,
            PageFaultResolver = PageFaultResolver,
            LogHandler = LogHandler // Copy the handler delegate
        };

        if (customMmuClone)
        {
            DetachedMmu? detached = null;
            try
            {
                var mmuRef = GetMmuRef();
                detached = CloneMmu(mmuRef, mmuCloneMode);
                newEngine.AttachMmu(detached);
            }
            catch
            {
                detached?.Dispose();
                newEngine.Dispose();
                throw;
            }
        }
        else if (skipMmu)
        {
            using var detached = newEngine.DetachMmu();
        }

        return newEngine;
    }

    public MmuRef GetMmuRef()
    {
        return new MmuRef(X86Native.GetMmuRef(State));
    }

    public DetachedMmu DetachMmu()
    {
        var handle = X86Native.DetachMmu(State);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to detach MMU from engine.");
        return new DetachedMmu(handle);
    }

    public DetachedMmu CloneMmu(MmuRef mmuRef, MmuCloneMode mode)
    {
        if (!mmuRef.IsValid)
            throw new ArgumentException("MMU ref is invalid.", nameof(mmuRef));

        var handle = X86Native.CloneMmuFromRef(mmuRef.NativeRef, ToNativeMmuCloneMode(mode));
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to clone MMU from engine.");
        return new DetachedMmu(handle);
    }

    public void AttachMmu(DetachedMmu detachedMmu)
    {
        ArgumentNullException.ThrowIfNull(detachedMmu);
        var handle = detachedMmu.ConsumeHandle();
        var attached = X86Native.AttachMmu(State, handle);
        if (attached != 0) return;

        X86Native.DestroyDetachedMmu(handle);
        throw new InvalidOperationException("Failed to attach detached MMU to engine.");
    }

    private static int ToNativeMmuCloneMode(MmuCloneMode mode)
    {
        return mode switch
        {
            MmuCloneMode.Full => 0,
            MmuCloneMode.SkipExternal => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported MMU clone mode.")
        };
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
