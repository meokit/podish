using System.IO.MemoryMappedFiles;
using System.Threading;
using Fiberish.Native;

namespace Fiberish.Memory;

internal sealed class WindowedMappedFilePageBackend : IFilePageBackend
{
    private enum WindowAccessMode
    {
        ReadOnly,
        ReadWrite
    }

    private sealed class Window : IDisposable
    {
        public required long Start { get; init; }
        public required long Length { get; init; }
        public required MemoryMappedFile Mmf { get; init; }
        public required MemoryMappedViewAccessor View { get; init; }
        public required nint RawPtr { get; init; }
        public required nint Ptr { get; init; }
        public required WindowAccessMode AccessMode { get; init; }
        public int RefCount { get; set; }
        public bool Retired { get; set; }

        public void Dispose()
        {
            unsafe
            {
                View.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            View.Dispose();
            Mmf.Dispose();
        }
    }

    private sealed class PageHandle : IPageHandle
    {
        private readonly WindowedMappedFilePageBackend _owner;
        private readonly long _pageIndex;
        private Window? _window;
        private readonly IntPtr _pointer;
        private int _disposed;

        public PageHandle(WindowedMappedFilePageBackend owner, long pageIndex, Window window, IntPtr pointer)
        {
            _owner = owner;
            _pageIndex = pageIndex;
            _window = window;
            _pointer = pointer;
        }

        public IntPtr Pointer => _pointer;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var window = Interlocked.Exchange(ref _window, null);
            if (window != null)
                _owner.ReleaseHandle(_pageIndex, window);
        }
    }

    private readonly Dictionary<long, Window> _windows = [];
    private readonly Dictionary<long, int> _guestPageRefs = [];
    private readonly object _lock = new();
    private readonly HostMemoryMapGeometry _geometry;
    private string _path;

    public WindowedMappedFilePageBackend(string path, HostMemoryMapGeometry geometry)
    {
        _path = path;
        _geometry = geometry;
    }

    public void UpdatePath(string path)
    {
        List<Window> disposeNow;
        lock (_lock)
        {
            if (string.Equals(_path, path, StringComparison.Ordinal))
                return;
            _path = path;
            disposeNow = ResetWindowsLocked();
        }

        DisposeWindows(disposeNow);
    }

    public bool TryAcquirePageHandle(long filePageIndex, long fileSize, bool writable, out IPageHandle? handle)
    {
        handle = null;
        if (filePageIndex < 0) return false;
        if (fileSize <= 0) return false;

        var pageStart = checked(filePageIndex * LinuxConstants.PageSize);
        if (pageStart < 0 || pageStart >= fileSize) return false;
        if (checked(pageStart + LinuxConstants.PageSize) > fileSize)
        {
            // Keep EOF tail pages on the buffered path to preserve guest 4K semantics.
            return false;
        }

        var windowStart = AlignDown(pageStart, _geometry.AllocationGranularity);
        var offsetInWindow = pageStart - windowStart;
        if (offsetInWindow < 0) return false;

        lock (_lock)
        {
            if (_windows.TryGetValue(windowStart, out var existing))
            {
                if (existing.Retired)
                {
                    _windows.Remove(windowStart);
                }
                else if (!writable || existing.AccessMode == WindowAccessMode.ReadWrite)
                {
                    existing.RefCount++;
                    IncrementGuestPageRefLocked(filePageIndex);
                    handle = new PageHandle(this, filePageIndex, existing,
                        (IntPtr)(existing.Ptr + (nint)offsetInWindow));
                    return true;
                }
                else
                {
                    _windows.Remove(windowStart);
                    existing.Retired = true;
                }
            }

            var created = CreateWindowLocked(windowStart, fileSize, writable);
            if (created == null) return false;

            created.RefCount = 1;
            _windows[windowStart] = created;
            IncrementGuestPageRefLocked(filePageIndex);
            handle = new PageHandle(this, filePageIndex, created,
                (IntPtr)(created.Ptr + (nint)offsetInWindow));
            return true;
        }
    }

    public bool TryFlushPage(long filePageIndex)
    {
        if (filePageIndex < 0) return false;

        var pageStart = checked(filePageIndex * LinuxConstants.PageSize);
        var windowStart = AlignDown(pageStart, _geometry.AllocationGranularity);
        lock (_lock)
        {
            if (!_windows.TryGetValue(windowStart, out var window) || window.Retired)
                return false;

            if (window.AccessMode == WindowAccessMode.ReadOnly)
                return true;

            try
            {
                window.View.Flush();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Truncate(long size)
    {
        _ = size;
        List<Window> disposeNow;
        lock (_lock)
        {
            disposeNow = ResetWindowsLocked();
        }

        DisposeWindows(disposeNow);
    }

    public FilePageBackendDiagnostics GetDiagnostics()
    {
        lock (_lock)
        {
            long windowBytes = 0;
            foreach (var window in _windows.Values)
                if (!window.Retired)
                    windowBytes += window.Length;
            return new FilePageBackendDiagnostics(_windows.Count, windowBytes, _guestPageRefs.Count);
        }
    }

    public void Dispose()
    {
        List<Window> disposeNow;
        lock (_lock)
        {
            disposeNow = ResetWindowsLocked();
        }

        DisposeWindows(disposeNow);
    }

    private Window? CreateWindowLocked(long windowStart, long fileSize, bool writable)
    {
        var remaining = fileSize - windowStart;
        if (remaining <= 0) return null;

        var windowLength = Math.Min((long)_geometry.AllocationGranularity, remaining);
        if (windowLength <= 0) return null;

        try
        {
            var access = writable ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.Read;
            var mmf = MemoryMappedFile.CreateFromFile(_path, FileMode.Open, mapName: null, capacity: 0, access);
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
                return new Window
                {
                    Start = windowStart,
                    Length = windowLength,
                    Mmf = mmf,
                    View = view,
                    RawPtr = rawPtr,
                    Ptr = ptr,
                    AccessMode = writable ? WindowAccessMode.ReadWrite : WindowAccessMode.ReadOnly,
                    RefCount = 0,
                    Retired = false
                };
            }
        }
        catch
        {
            return null;
        }
    }

    private void ReleaseHandle(long pageIndex, Window window)
    {
        var shouldDispose = false;
        lock (_lock)
        {
            DecrementGuestPageRefLocked(pageIndex);
            if (window.RefCount > 0) window.RefCount--;
            if (window.RefCount == 0 && window.Retired)
                shouldDispose = true;
        }

        if (shouldDispose)
            window.Dispose();
    }

    private List<Window> ResetWindowsLocked()
    {
        var disposeNow = new List<Window>(_windows.Count);
        foreach (var window in _windows.Values)
        {
            if (window.RefCount == 0)
                disposeNow.Add(window);
            else
                window.Retired = true;
        }

        _windows.Clear();
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

    private static long AlignDown(long value, int alignment)
    {
        if (alignment <= 0) return value;
        return (value / alignment) * alignment;
    }

    private static void DisposeWindows(IEnumerable<Window> windows)
    {
        foreach (var window in windows)
            window.Dispose();
    }
}
