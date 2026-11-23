using Fiberish.X86.Native;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

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
    private readonly List<IntPtr> _backingPages = [];

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
            Dispose();
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
                X86Native.MemProtect(State, guestPage, PageSize, combined);
                _pages[guestPage] = combined;
                continue;
            }
            MapPage(guestPage, permissions);
        }
    }

    /// <summary>Installs new backing and lets the native mapping API invalidate cached code.</summary>
    public void ReplaceBackingPage(uint address, ReadOnlySpan<byte> bytes, byte permissions = 7)
    {
        CheckedEnd(address, PageSize);
        if ((address & (PageSize - 1)) != 0)
            throw new ArgumentException("The guest page address must be aligned.", nameof(address));
        if (bytes.Length > PageSize) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (!_pages.ContainsKey(address))
            throw new InvalidOperationException($"Guest page {address:X8} is unmapped.");
        MapPage(address, permissions, bytes);
    }

    private void MapPage(uint address, byte permissions, ReadOnlySpan<byte> bytes = default)
    {
        IntPtr memory = Marshal.AllocHGlobal((int)PageSize);
        var span = new Span<byte>((void*)memory, (int)PageSize);
        span.Clear();
        bytes.CopyTo(span);
        if (X86Native.MemMapHostPage(State, address, (void*)memory, permissions) == 0)
        {
            Marshal.FreeHGlobal(memory);
            throw new InvalidOperationException($"Could not map guest page {address:X8}.");
        }
        _backingPages.Add(memory);
        _pages[address] = permissions;
    }

    /// <summary>Maps another guest address to a backing page owned by this sandbox.</summary>
    public void MapAlias(uint address, uint sourceAddress, byte permissions = 3)
    {
        CheckedEnd(address, PageSize);
        if (((address | sourceAddress) & (PageSize - 1)) != 0)
            throw new ArgumentException("Guest alias addresses must be page-aligned.");
        if (_pages.ContainsKey(address))
            throw new InvalidOperationException($"Guest page {address:X8} is already mapped.");
        X86Native.PageMapping source = default;
        if (X86Native.CollectMappedPages(State, sourceAddress, PageSize, &source, 1) != 1)
            throw new InvalidOperationException($"Guest page {sourceAddress:X8} is unmapped.");
        if (X86Native.MemMapHostPage(State, address, (void*)source.HostPage, permissions) == 0)
            throw new InvalidOperationException($"Could not map guest alias {address:X8}.");
        _pages.Add(address, permissions);
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
        X86Native.MemProtect(State, address, size, permissions);
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

    public EmuStatus Step()
    {
        var status = X86Native.Step(State);
        if (status < 0) throw new InvalidOperationException("Guest execution cannot be re-entered from a callback.");
        return (EmuStatus)status;
    }

    public void Run(uint endEip, ulong maxInstructions = 10000)
    {
        if (maxInstructions == 0) throw new ArgumentOutOfRangeException(nameof(maxInstructions), "Tests need a finite budget.");
        if (X86Native.Run(State, endEip, maxInstructions) == 0)
            throw new InvalidOperationException("Guest execution cannot be re-entered from a callback.");
    }

    public CpuSnapshot Snapshot() => new(
        Enum.GetValues<Reg>().Select(register => this[register]).ToArray(),
        Eip, Eflags, Fcw, Fsw, Ftw, Mxcsr,
        Enumerable.Range(0, 8).Select(ReadXmm).ToArray(),
        Enumerable.Range(0, 8).Select(ReadFpu).ToArray());

    public void Dispose()
    {
        _handle.Dispose();
        foreach (IntPtr page in _backingPages) Marshal.FreeHGlobal(page);
        _backingPages.Clear();
    }

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
