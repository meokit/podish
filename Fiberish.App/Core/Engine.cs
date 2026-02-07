using System.Runtime.InteropServices;
using System.Text;
using Bifrost.Native;

namespace Bifrost.Core;

public class Engine : IDisposable
{
    public IntPtr State { get; private set; }
    private GCHandle _gcHandle;
    private bool _disposed;

    // Callbacks
    public Action<Engine, uint, bool>? FaultHandler { get; set; }
    public Func<Engine, uint, bool>? InterruptHandler { get; set; }
    // New resolver for synchronous fault handling during safe access
    public Func<uint, bool, bool>? PageFaultResolver { get; set; }

    public bool TraceInstructions { get; set; } = false;

    public unsafe Engine()
    {
        State = X86Native.Create();
        if (State == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create Bifrost state");

        // Create GCHandle to this object so it can be retrieved in callbacks
        _gcHandle = GCHandle.Alloc(this);

        // Register callbacks
        X86Native.SetFaultCallback(State, &OnNativeFault, GCHandle.ToIntPtr(_gcHandle));
        X86Native.SetInterruptHook(State, 0x80, &OnNativeInterrupt, GCHandle.ToIntPtr(_gcHandle));
    }

    internal unsafe Engine(IntPtr state)
    {
        State = state;
        _gcHandle = GCHandle.Alloc(this);
        X86Native.SetFaultCallback(State, &OnNativeFault, GCHandle.ToIntPtr(_gcHandle));
        X86Native.SetInterruptHook(State, 0x80, &OnNativeInterrupt, GCHandle.ToIntPtr(_gcHandle));
    }

    [UnmanagedCallersOnly]
    private static void OnNativeFault(IntPtr state, uint addr, int isWrite, IntPtr userdata)
    {
        if (userdata == IntPtr.Zero) return;
        var handle = GCHandle.FromIntPtr(userdata);
        if (handle.Target is Engine engine)
        {
            engine.FaultHandler?.Invoke(engine, addr, isWrite != 0);
        }
    }

    [UnmanagedCallersOnly]
    private static int OnNativeInterrupt(IntPtr state, uint vector, IntPtr userdata)
    {
        if (userdata == IntPtr.Zero) return 0;
        var handle = GCHandle.FromIntPtr(userdata);
        if (handle.Target is Engine engine)
        {
            if (engine.InterruptHandler != null)
            {
                return engine.InterruptHandler(engine, vector) ? 1 : 0;
            }
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
            TraceInstructions = TraceInstructions
        };
        return newEngine;
    }

    public void MemMap(uint addr, uint size, byte perms) => X86Native.MemMap(State, addr, size, perms);

    public void MemUnmap(uint addr, uint size) => X86Native.MemUnmap(State, addr, size);

    public unsafe void MemWrite(uint addr, ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            X86Native.MemWrite(State, addr, p, (uint)data.Length);
        }
    }

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
        int len = data.Length;
        int written = 0;
        
        fixed (byte* pData = data)
        {
            while (written < len)
            {
                uint currAddr = vaddr + (uint)written;
                void* ptr = X86Native.ResolvePtr(State, currAddr, 1); // 1 = Write
                if (ptr == null) 
                {
                    if (PageFaultResolver != null && PageFaultResolver(currAddr, true))
                    {
                        ptr = X86Native.ResolvePtr(State, currAddr, 1);
                    }
                }
                if (ptr == null) return false;

                uint pageOffset = currAddr & 0xFFF;
                int chunk = Math.Min(len - written, 4096 - (int)pageOffset);

                Buffer.MemoryCopy(pData + written, (byte*)ptr, chunk, chunk);
                written += chunk;
            }
        }
        return true;
    }

    public unsafe bool CopyFromUser(uint vaddr, Span<byte> data)
    {
        int len = data.Length;
        int read = 0;

        fixed (byte* pData = data)
        {
            while (read < len)
            {
                uint currAddr = vaddr + (uint)read;
                void* ptr = X86Native.ResolvePtr(State, currAddr, 0); // 0 = Read
                if (ptr == null) 
                {
                    if (PageFaultResolver != null && PageFaultResolver(currAddr, false))
                    {
                        ptr = X86Native.ResolvePtr(State, currAddr, 0);
                    }
                }
                if (ptr == null) return false;

                uint pageOffset = currAddr & 0xFFF;
                int chunk = Math.Min(len - read, 4096 - (int)pageOffset);

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
        uint current = addr;
        
        Console.WriteLine($"[ReadStringSafe] Entering addr=0x{addr:x}");
        
        while (sb.Length < limit)
        {
            void* ptr = X86Native.ResolvePtr(State, current, 0); // Read
            Console.WriteLine($"[ReadStringSafe] ResolvePtr(0x{current:x}) returned {(ptr != null ? "valid" : "null")}");
            if (ptr == null) 
            {
                if (PageFaultResolver != null && PageFaultResolver(current, false))
                {
                    Console.WriteLine($"[ReadStringSafe] Resolved fault for 0x{current:x}");
                    ptr = X86Native.ResolvePtr(State, current, 0);
                }
            }
            if (ptr == null) return null; // Fault

            // NOTE: X86_ResolvePtr/resolve_safe returns pointer with offset already applied
            // So ptr is the exact byte pointer for 'current' address
            uint pageOffset = current & 0xFFF;
            byte* p = (byte*)ptr;  // Already includes offset
            int remainingInPage = 4096 - (int)pageOffset;
            
            for (int i = 0; i < remainingInPage; i++)
            {
                byte b = p[i];
                if (b == 0) return sb.ToString();
                sb.Append((char)b);
                if (sb.Length >= limit) break;
            }
            
            current += (uint)remainingInPage;
        }
        
        return sb.ToString();
    }

    public uint RegRead(Reg reg) => X86Native.RegRead(State, (int)reg);
    public void RegWrite(Reg reg, uint val) => X86Native.RegWrite(State, (int)reg, val);

    public uint Eip
    {
        get => X86Native.GetEIP(State);
        set => X86Native.SetEIP(State, value);
    }

    public uint Eflags
    {
        get => X86Native.GetEFLAGS(State);
        set => X86Native.SetEFLAGS(State, value);
    }

    public void SetSegBase(Seg seg, uint baseAddr) => X86Native.SegBaseWrite(State, (int)seg, baseAddr);
    public uint GetSegBase(Seg seg) => X86Native.SegBaseRead(State, (int)seg);

    public void Run(uint endEip = 0, ulong maxInsts = 0) => X86Native.Run(State, endEip, maxInsts);
    public void Stop() => X86Native.EmuStop(State);
    public void SetStatusFault() => X86Native.EmuFault(State);
    public void Yield() => X86Native.EmuYield(State);
    public int Step() => X86Native.Step(State);
    public EmuStatus Status => (EmuStatus)X86Native.GetStatus(State);

    public int FaultVector => X86Native.GetFaultVector(State);

    public bool IsDirty(uint addr) => X86Native.IsDirty(State, addr) != 0;

    public void InvalidateRange(uint addr, uint size) => X86Native.InvalidateRange(State, addr, size);
    public void FlushCache() => X86Native.FlushCache(State);

    public unsafe string? DumpStats()
    {
        byte[] buf = new byte[1024];
        fixed (byte* p = buf)
        {
            int n = X86Native.DumpStats(State, p, 1024);
            if (n < 0) return null;
            return System.Text.Encoding.UTF8.GetString(buf, 0, n);
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
            if (_gcHandle.IsAllocated)
            {
                _gcHandle.Free();
            }
            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public override string ToString()
    {
        return $"EIP: 0x{Eip:x8} ESP: 0x{RegRead(Reg.ESP):x8} EAX: 0x{RegRead(Reg.EAX):x8} EBX: 0x{RegRead(Reg.EBX):x8} ECX: 0x{RegRead(Reg.ECX):x8} EDX: 0x{RegRead(Reg.EDX):x8} ESI: 0x{RegRead(Reg.ESI):x8} EDI: 0x{RegRead(Reg.EDI):x8} EBP: 0x{RegRead(Reg.EBP):x8} EFLAGS: 0x{Eflags:x8}";
    }

    ~Engine() => Dispose(false);
}
