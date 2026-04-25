using System.Runtime.InteropServices;
using Fiberish.Native;

namespace Fiberish.Memory;

internal enum PooledSegmentAllocationKind
{
    NativeMemory,
    VirtualMemory
}

internal readonly record struct PooledSegmentMemoryReservation(
    nint BasePtr,
    nuint Size,
    PooledSegmentAllocationKind AllocationKind,
    bool IsZeroInitialized)
{
    public bool IsAllocated => BasePtr != 0;
}

internal static partial class PooledSegmentMemory
{
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadOnly = 0x02;
    private const uint PageReadWrite = 0x04;
    private const int ProtRead = 0x1;
    private const int ProtWrite = 0x2;
    private const int MapPrivate = 0x2;
    private const int MadviseDontNeed = 4;
    private static readonly nint MmapFailed = new(-1);
    internal static Action<nint, nuint>? TestAdviseUnusedObserver;

    public static bool SupportsReadOnlyProtection => SupportsVirtualMemoryApi();

    public static unsafe PooledSegmentMemoryReservation Allocate(nuint size)
    {
        if (size == 0)
            return default;

        if (TryAllocateVirtualMemory(size, out var reservation))
            return reservation;

        var alignment = (nuint)Math.Max(LinuxConstants.PageSize, Environment.SystemPageSize);
        var ptr = (nint)NativeMemory.AlignedAlloc(size, alignment);
        if (ptr == 0)
            return default;

        return new PooledSegmentMemoryReservation(
            ptr,
            size,
            PooledSegmentAllocationKind.NativeMemory,
            false);
    }

    public static unsafe void Free(PooledSegmentMemoryReservation reservation)
    {
        if (!reservation.IsAllocated)
            return;

        switch (reservation.AllocationKind)
        {
            case PooledSegmentAllocationKind.VirtualMemory:
                FreeVirtualMemory(reservation);
                return;
            case PooledSegmentAllocationKind.NativeMemory:
                NativeMemory.AlignedFree((void*)reservation.BasePtr);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(reservation));
        }
    }

    public static bool TryAdviseUnused(PooledSegmentMemoryReservation reservation, nint addr, nuint length)
    {
        if (!reservation.IsAllocated || reservation.AllocationKind != PooledSegmentAllocationKind.VirtualMemory ||
            addr == 0 || length == 0)
            return false;

        TestAdviseUnusedObserver?.Invoke(addr, length);

        if (OperatingSystem.IsWindows())
            return false;

        var preferredAdvice = GetPreferredFreeAdvice();
        if (madvise(addr, length, preferredAdvice) == 0)
            return true;

        if (preferredAdvice != MadviseDontNeed && madvise(addr, length, MadviseDontNeed) == 0)
            return true;

        return false;
    }

    public static bool TryProtectReadOnly(PooledSegmentMemoryReservation reservation, nint addr, nuint length)
    {
        if (!reservation.IsAllocated || reservation.AllocationKind != PooledSegmentAllocationKind.VirtualMemory ||
            addr == 0 || length == 0 || !SupportsReadOnlyProtection)
            return false;

        var reservationStart = reservation.BasePtr.ToInt64();
        var reservationEnd = checked(reservationStart + (long)reservation.Size);
        var protectStart = addr.ToInt64();
        var protectEnd = checked(protectStart + (long)length);
        if (protectStart < reservationStart || protectEnd > reservationEnd)
            return false;

        if (OperatingSystem.IsWindows())
            return VirtualProtect(addr, length, PageReadOnly, out _);

        return mprotect(addr, length, ProtRead) == 0;
    }

    private static bool TryAllocateVirtualMemory(nuint size, out PooledSegmentMemoryReservation reservation)
    {
        if (!SupportsVirtualMemoryApi())
        {
            reservation = default;
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            var ptr = VirtualAlloc(0, size, MemReserve | MemCommit, PageReadWrite);
            if (ptr == 0)
            {
                reservation = default;
                return false;
            }

            reservation = new PooledSegmentMemoryReservation(
                ptr,
                size,
                PooledSegmentAllocationKind.VirtualMemory,
                true);
            return true;
        }

        var mapped = mmap(0, size, ProtRead | ProtWrite, MapPrivate | GetAnonymousMapFlag(), -1, 0);
        if (mapped == MmapFailed)
        {
            reservation = default;
            return false;
        }

        reservation = new PooledSegmentMemoryReservation(
            mapped,
            size,
            PooledSegmentAllocationKind.VirtualMemory,
            true);
        return true;
    }

    private static void FreeVirtualMemory(PooledSegmentMemoryReservation reservation)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!VirtualFree(reservation.BasePtr, 0, MemRelease))
                throw new InvalidOperationException(
                    $"VirtualFree failed for pooled segment 0x{reservation.BasePtr.ToInt64():X}.");
            return;
        }

        if (munmap(reservation.BasePtr, reservation.Size) != 0)
            throw new InvalidOperationException(
                $"munmap failed for pooled segment 0x{reservation.BasePtr.ToInt64():X}.");
    }

    private static int GetAnonymousMapFlag()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() ||
            OperatingSystem.IsTvOS() || OperatingSystem.IsWatchOS())
            return 0x1000;

        return 0x20;
    }

    private static int GetPreferredFreeAdvice()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() ||
            OperatingSystem.IsTvOS() || OperatingSystem.IsWatchOS())
            return 5;

        if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
            return 8;

        return MadviseDontNeed;
    }

    private static bool SupportsVirtualMemoryApi()
    {
        if (OperatingSystem.IsBrowser())
            return false;
#if NET8_0_OR_GREATER
        if (OperatingSystem.IsWasi())
            return false;
#endif
        return true;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint VirtualAlloc(nint lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(nint lpAddress, nuint dwSize, uint dwFreeType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtect(
        nint lpAddress,
        nuint dwSize,
        uint flNewProtect,
        out uint lpflOldProtect);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint mmap(nint addr, nuint length, int prot, int flags, int fd, nint offset);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int munmap(nint addr, nuint length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int madvise(nint addr, nuint length, int advice);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int mprotect(nint addr, nuint length, int prot);
}
