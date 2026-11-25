using System.Runtime.InteropServices;

namespace Fiberish.Memory;

internal sealed partial class HostMemoryLockTracker : IDisposable
{
    private const int ErrorWorkingSetQuota = 1453;
    private const nuint MaxAutoWorkingSetMinimumBytes = (nuint)(512UL * 1024 * 1024);

    private readonly Lock _gate = new();
    private readonly Dictionary<nint, int> _osPageRefs = [];
    private readonly nint _osPageSize = Math.Max(4096, Environment.SystemPageSize);
    private bool _disposed;

    public bool TryAcquireRange(nint ptr, long byteLength)
    {
        if (ptr == 0 || byteLength <= 0)
            return false;

        var mask = _osPageSize - 1;
        var start = (nint)((long)ptr & ~(long)mask);
        var end = (nint)(((long)ptr + byteLength + mask) & ~(long)mask);

        lock (_gate)
        {
            if (_disposed)
                return false;

            var acquiredNewCount = 0;
            nint current = start;
            while (current < end)
            {
                if (_osPageRefs.TryGetValue(current, out var count) && count > 0)
                {
                    _osPageRefs[current] = count + 1;
                }
                else
                {
                    if (!TryOsLock(current, (nuint)_osPageSize))
                    {
                        RollbackAcquiredLocked(start, current);
                        return false;
                    }

                    _osPageRefs[current] = 1;
                    acquiredNewCount++;
                }

                current += _osPageSize;
            }

            _ = acquiredNewCount;
            return true;
        }
    }

    public void ReleaseRange(nint ptr, long byteLength)
    {
        if (ptr == 0 || byteLength <= 0)
            return;

        var mask = _osPageSize - 1;
        var start = (nint)((long)ptr & ~(long)mask);
        var end = (nint)(((long)ptr + byteLength + mask) & ~(long)mask);

        lock (_gate)
        {
            for (var current = start; current < end; current += _osPageSize)
            {
                if (!_osPageRefs.TryGetValue(current, out var count))
                    continue;

                if (count <= 1)
                {
                    _osPageRefs.Remove(current);
                    TryOsUnlock(current, (nuint)_osPageSize);
                }
                else
                {
                    _osPageRefs[current] = count - 1;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var osPage in _osPageRefs.Keys)
                TryOsUnlock(osPage, (nuint)_osPageSize);
            _osPageRefs.Clear();
        }
    }

    private void RollbackAcquiredLocked(nint start, nint failedAt)
    {
        for (var p = start; p < failedAt; p += _osPageSize)
        {
            if (!_osPageRefs.TryGetValue(p, out var count))
                continue;

            if (count <= 1)
            {
                _osPageRefs.Remove(p);
                TryOsUnlock(p, (nuint)_osPageSize);
            }
            else
            {
                _osPageRefs[p] = count - 1;
            }
        }
    }

    private static bool TryOsLock(nint addr, nuint len)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (VirtualLock(addr, len))
                    return true;

                if (Marshal.GetLastWin32Error() == ErrorWorkingSetQuota && TryGrowWorkingSet(len))
                    return VirtualLock(addr, len);

                return false;
            }

            // Linear-memory pages are pinned by the guest backing-page
            // tracker. Wasm has no host OS page-lock API to call in addition.
            if (OperatingSystem.IsBrowser() || OperatingSystem.IsWasi())
                return true;

            return mlock(addr, len) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryOsUnlock(nint addr, nuint len)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _ = VirtualUnlock(addr, len);
                return;
            }

            if (OperatingSystem.IsBrowser() || OperatingSystem.IsWasi())
                return;

            _ = munlock(addr, len);
        }
        catch
        {
        }
    }

    private static bool TryGrowWorkingSet(nuint neededBytes)
    {
        var proc = GetCurrentProcess();
        if (!GetProcessWorkingSetSizeEx(proc, out var minWs, out var maxWs, out _))
            return false;

        var increment = Math.Max((nuint)(4 * 1024 * 1024), neededBytes * 2);
        var desiredMin = minWs + increment;
        if (desiredMin > MaxAutoWorkingSetMinimumBytes)
            return false;

        var desiredMax = Math.Max(maxWs, desiredMin * 2);
        return SetProcessWorkingSetSizeEx(proc, desiredMin, desiredMax, 0);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "VirtualLock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualLock(nint lpAddress, nuint dwSize);

    [LibraryImport("kernel32.dll", EntryPoint = "VirtualUnlock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualUnlock(nint lpAddress, nuint dwSize);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessWorkingSetSizeEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessWorkingSetSizeEx(nint hProcess, out nuint lpMinimumWorkingSetSize,
        out nuint lpMaximumWorkingSetSize, out uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSizeEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSizeEx(nint hProcess, nuint dwMinimumWorkingSetSize,
        nuint dwMaximumWorkingSetSize, uint flags);

    [LibraryImport("libc", EntryPoint = "mlock", SetLastError = true)]
    private static partial int mlock(nint addr, nuint len);

    [LibraryImport("libc", EntryPoint = "munlock", SetLastError = true)]
    private static partial int munlock(nint addr, nuint len);
}
