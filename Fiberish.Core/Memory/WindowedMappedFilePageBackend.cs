using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Fiberish.Native;

namespace Fiberish.Memory;

internal sealed class WindowedMappedFilePageBackend : IFilePageBackend
{
    private enum WindowAccessMode
    {
        ReadOnly,
        ReadWrite
    }

    private interface IWindowMapping : IDisposable
    {
        nint RawPtr { get; }
        nint Ptr { get; }
        long Length { get; }
        bool Flush();
    }

    private sealed class MmfWindowMapping : IWindowMapping
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;

        public MmfWindowMapping(MemoryMappedFile mmf, MemoryMappedViewAccessor view, nint rawPtr, nint ptr, long length)
        {
            _mmf = mmf;
            _view = view;
            RawPtr = rawPtr;
            Ptr = ptr;
            Length = length;
        }

        public nint RawPtr { get; }
        public nint Ptr { get; }
        public long Length { get; }

        public bool Flush()
        {
            try
            {
                _view.Flush();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            unsafe
            {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            _view.Dispose();
            _mmf.Dispose();
        }
    }

    private sealed class UnixWindowMapping : IWindowMapping
    {
        private const int ProtRead = 0x1;
        private const int ProtWrite = 0x2;
        private const int MapShared = 0x01;

        private readonly nint _addr;

        private UnixWindowMapping(nint addr, long length)
        {
            _addr = addr;
            Length = length;
        }

        public nint RawPtr => _addr;
        public nint Ptr => _addr;
        public long Length { get; }

        public bool Flush()
        {
            return msync(_addr, checked((nuint)Length), GetMsSyncFlag()) == 0;
        }

        public void Dispose()
        {
            _ = munmap(_addr, checked((nuint)Length));
        }

        public static UnixWindowMapping? TryCreate(string path, long offset, long length, bool writable)
        {
            try
            {
                using var handle = File.OpenHandle(
                    path,
                    FileMode.Open,
                    writable ? FileAccess.ReadWrite : FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var fd = checked((int)handle.DangerousGetHandle());
                var prot = writable ? ProtRead | ProtWrite : ProtRead;
                var addr = mmap(0, checked((nuint)length), prot, MapShared, fd, (nint)offset);
                if (addr == new nint(-1))
                    return null;
                return new UnixWindowMapping(addr, length);
            }
            catch
            {
                return null;
            }
        }

        private static int GetMsSyncFlag()
        {
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() ||
                OperatingSystem.IsTvOS() || OperatingSystem.IsWatchOS())
                return 0x0010;
            return 0x0004;
        }

        [DllImport("libc", EntryPoint = "mmap")]
        private static extern nint mmap(nint addr, nuint length, int prot, int flags, int fd, nint offset);

        [DllImport("libc", EntryPoint = "munmap")]
        private static extern int munmap(nint addr, nuint length);

        [DllImport("libc", EntryPoint = "msync")]
        private static extern int msync(nint addr, nuint length, int flags);
    }

    private sealed class Window : IDisposable
    {
        public required long Start { get; init; }
        public required IWindowMapping Mapping { get; init; }
        public required WindowAccessMode AccessMode { get; init; }
        public int RefCount { get; set; }
        public bool Retired { get; set; }

        public long Length => Mapping.Length;
        public nint RawPtr => Mapping.RawPtr;
        public nint Ptr => Mapping.Ptr;

        public bool Flush()
        {
            return Mapping.Flush();
        }

        public void Dispose()
        {
            Mapping.Dispose();
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

        var isTailPage = checked(pageStart + LinuxConstants.PageSize) > fileSize;
        if (isTailPage && !_geometry.SupportsDirectMappedTailPage)
            return false;

        var windowStart = AlignDown(pageStart, _geometry.AllocationGranularity);
        var offsetInWindow = pageStart - windowStart;
        if (offsetInWindow < 0) return false;
        var requiredCoverage = checked(offsetInWindow + LinuxConstants.PageSize);

        lock (_lock)
        {
            if (_windows.TryGetValue(windowStart, out var existing))
            {
                if (existing.Retired)
                {
                    _windows.Remove(windowStart);
                }
                else if (existing.Length < requiredCoverage)
                {
                    _windows.Remove(windowStart);
                    existing.Retired = true;
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

            var created = CreateWindowLocked(windowStart, fileSize, writable, requiredCoverage);
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

            return window.Flush();
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

    private Window? CreateWindowLocked(long windowStart, long fileSize, bool writable, long requiredCoverage)
    {
        var remaining = fileSize - windowStart;
        if (remaining <= 0) return null;
        var requiresTailExtension = requiredCoverage > remaining;

        var cappedWindowLength = Math.Min((long)_geometry.AllocationGranularity, Math.Max(remaining, requiredCoverage));
        if (cappedWindowLength < requiredCoverage)
            return null;

        IWindowMapping? mapping = !OperatingSystem.IsWindows() && requiresTailExtension
            ? CreateUnixMappedWindow(windowStart, cappedWindowLength, writable)
            : CreateMemoryMappedWindow(windowStart, Math.Min(cappedWindowLength, remaining), writable);
        if (mapping == null)
            return null;

        return new Window
        {
            Start = windowStart,
            Mapping = mapping,
            AccessMode = writable ? WindowAccessMode.ReadWrite : WindowAccessMode.ReadOnly,
            RefCount = 0,
            Retired = false
        };
    }

    private IWindowMapping? CreateMemoryMappedWindow(long windowStart, long windowLength, bool writable)
    {
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
                return new MmfWindowMapping(mmf, view, rawPtr, ptr, windowLength);
            }
        }
        catch
        {
            return null;
        }
    }

    private IWindowMapping? CreateUnixMappedWindow(long windowStart, long windowLength, bool writable)
    {
        var alignedLength = AlignUp(windowLength, _geometry.HostPageSize);
        if (alignedLength <= 0)
            return null;
        return UnixWindowMapping.TryCreate(_path, windowStart, alignedLength, writable);
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

    private static long AlignUp(long value, int alignment)
    {
        if (alignment <= 0) return value;
        var mask = alignment - 1L;
        return (value + mask) & ~mask;
    }

    private static void DisposeWindows(IEnumerable<Window> windows)
    {
        foreach (var window in windows)
            window.Dispose();
    }
}
