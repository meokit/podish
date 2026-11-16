using Fiberish.X86.Native;
using Microsoft.Win32.SafeHandles;

namespace Fiberish.X86.Tests.Harness;

/// <summary>
/// A single test's CPU, MMU and pages. No Linux runtime, shared fixture, CPU pool,
/// process signal handlers, or mutable static test state participates in execution.
/// </summary>
public sealed unsafe class CpuSandbox : IDisposable
{
    public const uint PageSize = 4096;
    public const uint CodeAddress = 0x00100000;
    public const uint DataAddress = 0x00200000;
    public const uint StackAddress = 0x00300000;

    private readonly CpuHandle _handle;
    private readonly Dictionary<uint, byte> _pages = [];

    public CpuSandbox(bool mapDefaults = true)
    {
        _handle = new CpuHandle(X86Native.CreateWithOptions(1));
        if (_handle.IsInvalid)
            throw new InvalidOperationException("Could not create an isolated CPU.");
        try
        {
            // Native Create supplies the architectural reset state. Do not reset it
            // here: reset-state tests must observe the actual implementation.
            X86Native.SetTscMode(State, 0);
            X86Native.SetTscOffset(State, 0);
            if (mapDefaults)
            {
                MapMemory(CodeAddress, PageSize, 7);
                MapMemory(DataAddress, PageSize);
                MapMemory(StackAddress, PageSize);
                this[Reg.ESP] = StackAddress + PageSize - 16;
            }
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    public IntPtr State
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _handle.DangerousGetHandle();
        }
    }

    public uint this[Reg register]
    {
        get => X86Native.RegRead(State, (int)register);
        set => X86Native.RegWrite(State, (int)register, value);
    }

    public uint Eip { get => X86Native.GetEIP(State); set => X86Native.SetEIP(State, value); }
    public uint Eflags { get => X86Native.GetEFLAGS(State); set => X86Native.SetEFLAGS(State, value); }
    public ushort Fcw { get => X86Native.GetFCW(State); set => X86Native.SetFCW(State, value); }
    public ushort Fsw { get => X86Native.GetFSW(State); set => X86Native.SetFSW(State, value); }
    public ushort Ftw { get => X86Native.GetFTW(State); set => X86Native.SetFTW(State, value); }
    public uint Mxcsr { get => X86Native.GetMXCSR(State); set => X86Native.SetMXCSR(State, value); }
    public EmuStatus Status => (EmuStatus)X86Native.GetStatus(State);
    public int FaultVector => X86Native.GetFaultVector(State);
    public uint FaultAddress => X86Native.GetFaultAddress(State);

    public void MapMemory(uint address, uint size, byte permissions = 3)
    {
        var end = CheckedEnd(address, size);
        if (size == 0) return;
        for (ulong page = address & ~(PageSize - 1); page < end; page += PageSize)
        {
            var guestPage = (uint)page;
            if (_pages.TryGetValue(guestPage, out var oldPermissions))
            {
                var combined = (byte)(oldPermissions | permissions);
                X86Native.ReprotectMappedRange(State, guestPage, PageSize, combined);
                _pages[guestPage] = combined;
                continue;
            }
            var pointer = X86Native.AllocatePage(State, guestPage, permissions);
            if (pointer == null)
                throw new InvalidOperationException($"Could not map guest page {guestPage:X8}.");
            // Zero the backing allocation explicitly even if the current allocator
            // happens to clear it; no case may read a previous allocator occupant.
            new Span<byte>(pointer, (int)PageSize).Clear();
            _pages.Add(guestPage, permissions);
        }
    }

    public void ProtectMemory(uint address, uint size, byte permissions)
    {
        var end = CheckedEnd(address, size);
        if (size == 0) return;
        for (ulong page = address & ~(PageSize - 1); page < end; page += PageSize)
        {
            if (!_pages.ContainsKey((uint)page))
                throw new InvalidOperationException($"Protecting unmapped guest page {page:X8}.");
            _pages[(uint)page] = permissions;
        }
        X86Native.ReprotectMappedRange(State, address, size, permissions);
    }

    public void UnmapMemory(uint address, uint size)
    {
        var end = CheckedEnd(address, size);
        if (size == 0) return;
        X86Native.MemUnmap(State, address, size);
        for (ulong page = address & ~(PageSize - 1); page < end; page += PageSize)
            _pages.Remove((uint)page);
    }

    public void WriteCode(ReadOnlySpan<byte> code)
    {
        WriteMemory(CodeAddress, code);
        Eip = CodeAddress;
    }

    public void WriteMemory(uint address, ReadOnlySpan<byte> bytes)
    {
        CheckedEnd(address, checked((uint)bytes.Length));
        var offset = 0;
        while (offset < bytes.Length)
        {
            var current = address + (uint)offset;
            var destination = X86Native.ResolvePtrForWrite(State, current);
            if (destination == null)
                throw new InvalidOperationException($"Writing unmapped or protected guest memory {current:X8}.");
            var count = Math.Min(bytes.Length - offset, (int)(PageSize - current % PageSize));
            bytes.Slice(offset, count).CopyTo(new Span<byte>(destination, count));
            offset += count;
        }
        if (!bytes.IsEmpty)
            X86Native.ResetCodeCacheByRange(State, address, (uint)bytes.Length);
    }

    public byte[] ReadMemory(uint address, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        CheckedEnd(address, (uint)count);
        var result = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var current = address + (uint)offset;
            var source = X86Native.ResolvePtrForRead(State, current);
            if (source == null)
                throw new InvalidOperationException($"Reading unmapped or protected guest memory {current:X8}.");
            var chunk = Math.Min(count - offset, (int)(PageSize - current % PageSize));
            new ReadOnlySpan<byte>(source, chunk).CopyTo(result.AsSpan(offset));
            offset += chunk;
        }
        return result;
    }

    public byte[] ReadXmm(int index)
    {
        CheckRegisterIndex(index);
        var result = new byte[16];
        fixed (byte* pointer = result) X86Native.ReadXMM(State, index, pointer);
        return result;
    }

    public void WriteXmm(int index, ReadOnlySpan<byte> value)
    {
        CheckRegisterIndex(index);
        if (value.Length != 16) throw new ArgumentException("XMM values must contain 16 bytes.", nameof(value));
        fixed (byte* pointer = value) X86Native.WriteXMM(State, index, pointer);
    }

    public byte[] ReadFpu(int logicalIndex)
    {
        CheckRegisterIndex(logicalIndex);
        var result = new byte[10];
        fixed (byte* pointer = result) X86Native.ReadFPUReg(State, logicalIndex, pointer);
        return result;
    }

    public void WriteFpu(int logicalIndex, ReadOnlySpan<byte> value)
    {
        CheckRegisterIndex(logicalIndex);
        if (value.Length != 10) throw new ArgumentException("x87 values must contain 10 bytes.", nameof(value));
        fixed (byte* pointer = value) X86Native.WriteFPUReg(State, logicalIndex, pointer);
    }

    public EmuStatus Step() => (EmuStatus)X86Native.Step(State);

    public void Run(uint endEip, ulong maxInstructions = 10000)
    {
        if (maxInstructions == 0) throw new ArgumentOutOfRangeException(nameof(maxInstructions), "Tests need a finite budget.");
        X86Native.Run(State, endEip, maxInstructions);
    }

    public CpuSnapshot Snapshot() => new(
        Enum.GetValues<Reg>().Select(register => this[register]).ToArray(),
        Eip, Eflags, Fcw, Fsw, Ftw, Mxcsr,
        Enumerable.Range(0, 8).Select(ReadXmm).ToArray(),
        Enumerable.Range(0, 8).Select(ReadFpu).ToArray());

    public void Dispose() => _handle.Dispose();

    private static ulong CheckedEnd(uint address, uint size)
    {
        var end = (ulong)address + size;
        if (end > 0x1_0000_0000UL) throw new ArgumentOutOfRangeException(nameof(size), "Mapping wraps the guest address space.");
        return end;
    }

    private static void CheckRegisterIndex(int index)
    {
        if ((uint)index >= 8) throw new ArgumentOutOfRangeException(nameof(index));
    }

    private sealed class CpuHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public CpuHandle(IntPtr value) : base(ownsHandle: true) => SetHandle(value);
        protected override bool ReleaseHandle()
        {
            X86Native.Destroy(handle);
            return true;
        }
    }
}

public sealed record CpuSnapshot(
    uint[] Registers, uint Eip, uint Eflags, ushort Fcw, ushort Fsw, ushort Ftw,
    uint Mxcsr, byte[][] Xmm, byte[][] Fpu);
