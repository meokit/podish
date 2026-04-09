using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fiberish.Native;

namespace Fiberish.Memory;

internal sealed class WindowedMappedFilePageBackend(string path, HostMemoryMapGeometry geometry) : IFilePageBackend
{
    private readonly Dictionary<long, long> _activeWindowIdsByStart = [];
    private readonly Dictionary<long, int> _guestPageRefs = [];
    private readonly Dictionary<long, PageLease> _leases = [];
    private readonly Lock _lock = new();
    private readonly Dictionary<long, Window> _windowsById = [];
    private long _nextLeaseToken = 1;
    private long _nextWindowId = 1;
    private string _path = path;

    public void UpdatePath(string path)
    {
        List<WindowMapping> disposeNow;
        lock (_lock)
        {
            if (string.Equals(_path, path, StringComparison.Ordinal))
                return;
            _path = path;
            disposeNow = ResetWindowsLocked();
        }

        DisposeMappings(disposeNow);
    }

    public bool TryAcquirePageLease(long filePageIndex, long fileSize, bool writable, out IntPtr pointer,
        out long releaseToken)
    {
        pointer = IntPtr.Zero;
        releaseToken = 0;
        if (filePageIndex < 0) return false;
        if (fileSize <= 0) return false;

        var pageStart = checked(filePageIndex * LinuxConstants.PageSize);
        if (pageStart >= fileSize) return false;

        var isTailPage = checked(pageStart + LinuxConstants.PageSize) > fileSize;
        if (isTailPage && !geometry.SupportsDirectMappedTailPage)
            return false;

        var windowStart = AlignDown(pageStart, geometry.AllocationGranularity);
        var offsetInWindow = pageStart - windowStart;
        if (offsetInWindow < 0) return false;
        var requiredCoverage = checked(offsetInWindow + LinuxConstants.PageSize);

        WindowMapping retiredMapping = default;
        var success = false;
        try
        {
            lock (_lock)
            {
                if (_activeWindowIdsByStart.TryGetValue(windowStart, out var activeWindowId))
                {
                    if (TryAcquireFromActiveWindowLocked(activeWindowId, filePageIndex, writable, requiredCoverage,
                            offsetInWindow, out pointer, out releaseToken))
                    {
                        success = true;
                        return true;
                    }

                    RetireActiveWindowLocked(windowStart, activeWindowId, ref retiredMapping);
                }

                var created = CreateWindowLocked(windowStart, fileSize, writable, requiredCoverage);
                if (created == null)
                    return false;

                var window = created.Value;
                window.RefCount = 1;
                _activeWindowIdsByStart[windowStart] = window.Id;
                _windowsById.Add(window.Id, window);
                IncrementGuestPageRefLocked(filePageIndex);
                pointer = window.Ptr + (nint)offsetInWindow;
                releaseToken = CreateLeaseLocked(filePageIndex, window.Id);
                success = true;
                return true;
            }
        }
        finally
        {
            Dispose(ref retiredMapping);
            if (!success)
            {
                pointer = IntPtr.Zero;
                releaseToken = 0;
            }
        }
    }

    public void ReleasePageLease(long releaseToken)
    {
        if (releaseToken == 0)
            return;

        WindowMapping disposeNow = default;
        lock (_lock)
        {
            if (!_leases.Remove(releaseToken, out var lease))
                return;

            DecrementGuestPageRefLocked(lease.PageIndex);
            ReleaseWindowLeaseLocked(lease.WindowId, ref disposeNow);
        }

        Dispose(ref disposeNow);
    }

    public bool TryFlushPage(long filePageIndex)
    {
        if (filePageIndex < 0) return false;

        var pageStart = checked(filePageIndex * LinuxConstants.PageSize);
        var windowStart = AlignDown(pageStart, geometry.AllocationGranularity);
        lock (_lock)
        {
            if (!_activeWindowIdsByStart.TryGetValue(windowStart, out var windowId))
                return false;

            ref var window = ref GetWindowRef(windowId);
            if (Unsafe.IsNullRef(ref window) || window.Retired)
                return false;

            if (window.AccessMode == WindowAccessMode.ReadOnly)
                return true;

            return Flush(in window.Mapping);
        }
    }

    public void Truncate(long size)
    {
        _ = size;
        List<WindowMapping> disposeNow;
        lock (_lock)
        {
            disposeNow = ResetWindowsLocked();
        }

        DisposeMappings(disposeNow);
    }

    public long Trim(bool aggressive)
    {
        List<WindowMapping> disposeNow;
        lock (_lock)
        {
            disposeNow = TrimWindowsLocked(aggressive);
        }

        long reclaimedBytes = 0;
        foreach (var mapping in disposeNow)
        {
            reclaimedBytes += mapping.Length;
            var detached = mapping;
            Dispose(ref detached);
        }

        return reclaimedBytes;
    }

    public FilePageBackendDiagnostics GetDiagnostics()
    {
        lock (_lock)
        {
            long windowBytes = 0;
            var windowCount = 0;
            foreach (var windowId in _activeWindowIdsByStart.Values)
            {
                ref var window = ref GetWindowRef(windowId);
                if (Unsafe.IsNullRef(ref window) || window.Retired)
                    continue;

                windowCount++;
                windowBytes += window.Length;
            }

            return new FilePageBackendDiagnostics(windowCount, windowBytes, _guestPageRefs.Count);
        }
    }

    public void Dispose()
    {
        List<WindowMapping> disposeNow;
        lock (_lock)
        {
            disposeNow = ResetWindowsLocked();
        }

        DisposeMappings(disposeNow);
    }

    private bool TryAcquireFromActiveWindowLocked(long windowId, long filePageIndex, bool writable,
        long requiredCoverage,
        long offsetInWindow, out IntPtr pointer, out long releaseToken)
    {
        pointer = IntPtr.Zero;
        releaseToken = 0;

        ref var window = ref GetWindowRef(windowId);
        if (Unsafe.IsNullRef(ref window) || window.Retired)
            return false;
        if (window.Length < requiredCoverage)
            return false;
        if (writable && window.AccessMode != WindowAccessMode.ReadWrite)
            return false;

        window.RefCount++;
        IncrementGuestPageRefLocked(filePageIndex);
        pointer = window.Ptr + (nint)offsetInWindow;
        releaseToken = CreateLeaseLocked(filePageIndex, window.Id);
        return true;
    }

    private Window? CreateWindowLocked(long windowStart, long fileSize, bool writable, long requiredCoverage)
    {
        var remaining = fileSize - windowStart;
        if (remaining <= 0) return null;

        var cappedWindowLength = Math.Min(geometry.AllocationGranularity, Math.Max(remaining, requiredCoverage));
        if (cappedWindowLength < requiredCoverage)
            return null;

        var mapping = CreateMemoryMappedWindow(
            windowStart,
            Math.Min(cappedWindowLength, remaining),
            cappedWindowLength,
            writable);
        if (mapping == null)
            return null;

        return new Window
        {
            Id = _nextWindowId++,
            Start = windowStart,
            Mapping = mapping.Value,
            AccessMode = writable ? WindowAccessMode.ReadWrite : WindowAccessMode.ReadOnly,
            RefCount = 0,
            Retired = false
        };
    }

    private WindowMapping? CreateMemoryMappedWindow(long windowStart, long windowLength, long logicalLength,
        bool writable)
    {
        try
        {
            var access = writable ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.Read;
            var mmf = MemoryMappedFile.CreateFromFile(_path, FileMode.Open, null, 0, access);
            var view = mmf.CreateViewAccessor(windowStart, windowLength, access);

            unsafe
            {
                byte* raw = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref raw);
                if (raw == null)
                {
                    view.Dispose();
                    mmf.Dispose();
                    return null;
                }

                var rawPtr = (nint)raw;
                var ptr = rawPtr + (nint)view.PointerOffset;
                var flushLength = AlignUp(view.PointerOffset + windowLength, geometry.HostPageSize);
                return new WindowMapping
                {
                    Mmf = mmf,
                    View = view,
                    RawPtr = rawPtr,
                    Ptr = ptr,
                    Length = logicalLength,
                    FlushLength = flushLength
                };
            }
        }
        catch
        {
            return null;
        }
    }

    private void RetireActiveWindowLocked(long windowStart, long windowId, ref WindowMapping disposeNow)
    {
        _activeWindowIdsByStart.Remove(windowStart);
        RetireWindowLocked(windowId, ref disposeNow);
    }

    private void RetireWindowLocked(long windowId, ref WindowMapping disposeNow)
    {
        var shouldRemove = false;
        {
            ref var window = ref GetWindowRef(windowId);
            if (Unsafe.IsNullRef(ref window))
                return;

            window.Retired = true;
            if (window.RefCount != 0)
                return;

            disposeNow = DetachWindowMapping(ref window);
            shouldRemove = true;
        }

        if (shouldRemove)
            _windowsById.Remove(windowId);
    }

    private void ReleaseWindowLeaseLocked(long windowId, ref WindowMapping disposeNow)
    {
        var shouldRemove = false;
        {
            ref var window = ref GetWindowRef(windowId);
            if (Unsafe.IsNullRef(ref window))
                return;

            if (window.RefCount > 0)
                window.RefCount--;
            if (window.RefCount != 0 || !window.Retired)
                return;

            disposeNow = DetachWindowMapping(ref window);
            shouldRemove = true;
        }

        if (shouldRemove)
            _windowsById.Remove(windowId);
    }

    private List<WindowMapping> ResetWindowsLocked()
    {
        var disposeNow = new List<WindowMapping>(_activeWindowIdsByStart.Count);
        var activeWindows = new List<KeyValuePair<long, long>>(_activeWindowIdsByStart);
        _activeWindowIdsByStart.Clear();
        foreach (var (_, windowId) in activeWindows)
        {
            WindowMapping mapping = default;
            RetireWindowLocked(windowId, ref mapping);
            if (mapping.IsAllocated)
                disposeNow.Add(mapping);
        }

        return disposeNow;
    }

    private List<WindowMapping> TrimWindowsLocked(bool aggressive)
    {
        var disposeNow = new List<WindowMapping>();
        var startsToRetire = new List<KeyValuePair<long, long>>();
        foreach (var pair in _activeWindowIdsByStart)
        {
            ref var window = ref GetWindowRef(pair.Value);
            if (Unsafe.IsNullRef(ref window) || window.Retired)
            {
                startsToRetire.Add(pair);
                continue;
            }

            if (!aggressive && window.RefCount > 0)
                continue;

            startsToRetire.Add(pair);
        }

        foreach (var (start, windowId) in startsToRetire)
        {
            _activeWindowIdsByStart.Remove(start);
            WindowMapping mapping = default;
            RetireWindowLocked(windowId, ref mapping);
            if (mapping.IsAllocated)
                disposeNow.Add(mapping);
        }

        return disposeNow;
    }

    private void IncrementGuestPageRefLocked(long filePageIndex)
    {
        _guestPageRefs.TryGetValue(filePageIndex, out var count);
        _guestPageRefs[filePageIndex] = count + 1;
    }

    private void DecrementGuestPageRefLocked(long filePageIndex)
    {
        if (!_guestPageRefs.TryGetValue(filePageIndex, out var count))
            return;
        if (count <= 1)
            _guestPageRefs.Remove(filePageIndex);
        else
            _guestPageRefs[filePageIndex] = count - 1;
    }

    private ref Window GetWindowRef(long windowId)
    {
        return ref CollectionsMarshal.GetValueRefOrNullRef(_windowsById, windowId);
    }

    private static WindowMapping DetachWindowMapping(ref Window window)
    {
        var mapping = window.Mapping;
        window.Mapping = default;
        return mapping;
    }

    private static bool Flush(in WindowMapping mapping)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                mapping.View?.Flush();
            }
            else
            {
                if (msync(mapping.RawPtr, checked((nuint)mapping.FlushLength), GetMsSyncFlag()) != 0)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Dispose(ref WindowMapping mapping)
    {
        var view = mapping.View;
        var mmf = mapping.Mmf;
        mapping = default;

        if (view != null)
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
            view.Dispose();
        }

        mmf?.Dispose();
    }

    private static void DisposeMappings(IEnumerable<WindowMapping> mappings)
    {
        foreach (var mapping in mappings)
        {
            var detached = mapping;
            Dispose(ref detached);
        }
    }

    private long CreateLeaseLocked(long pageIndex, long windowId)
    {
        var releaseToken = _nextLeaseToken++;
        _leases.Add(releaseToken, new PageLease(pageIndex, windowId));
        return releaseToken;
    }

    private static long AlignDown(long value, int alignment)
    {
        if (alignment <= 0) return value;
        return value / alignment * alignment;
    }

    private static long AlignUp(long value, int alignment)
    {
        if (alignment <= 0) return value;
        var mask = alignment - 1L;
        return (value + mask) & ~mask;
    }

    private static int GetMsSyncFlag()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() ||
            OperatingSystem.IsTvOS() || OperatingSystem.IsWatchOS())
            return 0x0010;
        return 0x0004;
    }

    [DllImport("libc", EntryPoint = "msync")]
    private static extern int msync(nint addr, nuint length, int flags);

    private enum WindowAccessMode
    {
        ReadOnly,
        ReadWrite
    }

    private struct WindowMapping
    {
        public MemoryMappedFile? Mmf;
        public MemoryMappedViewAccessor? View;
        public nint RawPtr;
        public nint Ptr;
        public long Length;
        public long FlushLength;

        public readonly bool IsAllocated => Ptr != 0;
    }

    private struct Window
    {
        public long Id;
        public long Start;
        public WindowMapping Mapping;
        public WindowAccessMode AccessMode;
        public int RefCount;
        public bool Retired;

        public readonly long Length => Mapping.Length;
        public readonly nint Ptr => Mapping.Ptr;
    }

    private readonly record struct PageLease(long PageIndex, long WindowId);
}