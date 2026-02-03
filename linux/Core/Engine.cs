using System.Runtime.InteropServices;
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
            InterruptHandler = InterruptHandler
        };
        return newEngine;
    }

    public void MemMap(uint addr, uint size, byte perms) => X86Native.MemMap(State, addr, size, perms);

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
        return $"EIP: 0x{Eip:x8} ESP: 0x{RegRead(Reg.ESP):x8} EAX: 0x{RegRead(Reg.EAX):x8} EBX: 0x{RegRead(Reg.EBX):x8} ECX: 0x{RegRead(Reg.ECX):x8} EDX: 0x{RegRead(Reg.EDX):x8} ESI: 0x{RegRead(Reg.ESI):x8} EDI: 0x{RegRead(Reg.EDI):x8} EBP: 0x{RegRead(Reg.EBP):x8}";
    }

    ~Engine() => Dispose(false);
}
