using System.Runtime.InteropServices;
using System.Text;
using Fiberish.X86.Native;
using X86Native = Fiberish.X86.Native.X86Native;

namespace Fiberish.Core;

public class Engine : IDisposable
{
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
        X86Native.SetLogCallback(State, &OnNativeLog, GCHandle.ToIntPtr(_gcHandle));
    }

    public IntPtr State { get; private set; }

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
    private static int OnNativeInterrupt(IntPtr state, uint vector, IntPtr userdata)
    {
        try
        {
            if (userdata == IntPtr.Zero) return 0;
            var handle = GCHandle.FromIntPtr(userdata);
            if (handle.Target is Engine engine)
                if (engine.InterruptHandler != null)
                    return engine.InterruptHandler(engine, vector) ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Engine] Exception in OnNativeInterrupt: {ex}");
        }

        return 0;
    }

    public Engine Clone(bool shareMem)
    {
        var newState = X86Native.Clone(State, shareMem ? 1 : 0);
        var newEngine = new Engine(newState)
        {
            FaultHandler = FaultHandler,
            InterruptHandler = InterruptHandler,
            PageFaultResolver = PageFaultResolver,
            LogHandler = LogHandler // Copy the handler delegate
        };
        return newEngine;
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

    public void InvalidateRange(uint addr, uint size)
    {
        X86Native.InvalidateRange(State, addr, size);
    }

    public void FlushCache()
    {
        X86Native.FlushCache(State);
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