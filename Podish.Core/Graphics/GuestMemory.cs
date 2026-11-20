using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;

namespace Podish.Core.Graphics;

internal sealed class GuestMemory(FiberTask task)
{
    internal const int MaxTransfer = 64 * 1024 * 1024;
    internal const int MaxString = 1024 * 1024;

    internal sealed record Staging(Process Process, VMAManager Memory, VmArea Area, uint Address, uint Length)
    {
        internal void Release()
        {
            if (Process.MemoryReleased || !ReferenceEquals(Process.Mem, Memory)) return;
            var thread = Process.Threads.FirstOrDefault(thread => !thread.Exited);
            if (thread != null) Memory.UnmapDeviceStaging(thread.CPU, Area, Address, Length);
        }
    }

    internal Staging AllocateStaging(int length)
    {
        ValidateLength(length);
        var aligned = ((uint)Math.Max(1, length) + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
        var memory = task.Process.Mem;
        try
        {
            var area = memory.MapDeviceStaging(task.CPU, aligned, "GLES buffer mapping");
            return new Staging(task.Process, memory, area, area.Start, aligned);
        }
        catch (OutOfMemoryException)
        {
            throw new GuestTransferException(Errno.E2BIG);
        }
    }

    internal byte[]? Input(uint address, int length, bool nullable = false)
    {
        ValidateLength(length);
        if (address == 0 && nullable) return null;
        // Keep a non-null native pointer for zero-sized arrays.
        var bytes = new byte[Math.Max(1, length)];
        Read(address, bytes.AsSpan(0, length));
        return bytes;
    }

    internal byte[]? Output(uint address, int length, bool nullable = false)
    {
        ValidateLength(length);
        if (address == 0 && nullable) return null;
        // Preserve untouched elements and allow enough native space for scalar and vector queries.
        var bytes = new byte[Math.Max(64, length)];
        Read(address, bytes.AsSpan(0, length));
        return bytes;
    }

    internal byte[] String(uint address)
    {
        if (address == 0) throw new GuestTransferException(Errno.EFAULT);
        using var result = new MemoryStream();
        Span<byte> page = stackalloc byte[LinuxConstants.PageSize];
        while (result.Length < MaxString)
        {
            var amount = LinuxConstants.PageSize - (int)(address & LinuxConstants.PageOffsetMask);
            var chunk = page[..amount];
            Read(address, chunk);
            var end = chunk.IndexOf((byte)0);
            if (end >= 0)
            {
                result.Write(chunk[..(end + 1)]);
                return result.ToArray();
            }
            result.Write(chunk);
            if (address > uint.MaxValue - amount) break;
            address += (uint)amount;
        }
        throw new GuestTransferException(Errno.E2BIG);
    }

    internal void Read(uint address, Span<byte> destination)
    {
        if (destination.IsEmpty) return;
        if (address == 0 || (ulong)address + (uint)destination.Length > 1UL << 32 ||
            !task.CPU.CopyFromUser(address, destination))
            throw new GuestTransferException(Errno.EFAULT);
    }

    internal void WriteOutput(uint address, byte[]? data, int length)
    {
        if (data != null) Write(address, data.AsSpan(0, length));
    }

    internal void Write(uint address, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        if (address == 0 || (ulong)address + (uint)data.Length > 1UL << 32 || !task.CPU.CopyToUser(address, data))
            throw new GuestTransferException(Errno.EFAULT);
    }

    private static void ValidateLength(int size)
    {
        if (size < 0) throw new GuestTransferException(Errno.EINVAL);
        if (size > MaxTransfer) throw new GuestTransferException(Errno.E2BIG);
    }
}

internal sealed class GuestTransferException(Errno errno) : Exception
{
    internal Errno Errno { get; } = errno;
}
