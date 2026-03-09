using System.IO.MemoryMappedFiles;
using System.Threading;
using Fiberish.Native;

namespace Fiberish.Memory;

/// <summary>
/// Best-effort file-backed page provider for inode page cache.
/// Falls back automatically on unsupported platforms (for example browser/Wasm).
/// </summary>
internal sealed class MappedFilePageCache : IDisposable
{
    private sealed class MappedPage : IDisposable
    {
        public required MemoryMappedFile Mmf { get; init; }
        public required MemoryMappedViewAccessor View { get; init; }
        public required nint RawPtr { get; init; }
        public required nint Ptr { get; init; }
        public int RefCount { get; set; }
        public bool Retired { get; set; }

        public void Dispose()
        {
            unsafe
            {
                byte* raw = (byte*)RawPtr;
                View.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            View.Dispose();
            Mmf.Dispose();
        }
    }

    private sealed class PageHandle : IPageHandle
    {
        private readonly MappedFilePageCache _owner;
        private readonly long _pageIndex;
        private MappedPage? _page;
        private int _disposed;

        public PageHandle(MappedFilePageCache owner, long pageIndex, MappedPage page)
        {
            _owner = owner;
            _pageIndex = pageIndex;
            _page = page;
        }

        public IntPtr Pointer => (IntPtr)(_page?.Ptr ?? IntPtr.Zero);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var page = Interlocked.Exchange(ref _page, null);
            if (page != null)
                _owner.ReleaseHandle(_pageIndex, page);
        }
    }

    private readonly bool _writable;
    private readonly Dictionary<long, MappedPage> _pages = [];
    private readonly object _lock = new();
    private string _path;
    private bool _disabled;

    public MappedFilePageCache(string path, bool writable)
    {
        _path = path;
        _writable = writable;
        _disabled = !IsSupportedOnCurrentPlatform();
    }

    public void UpdatePath(string path)
    {
        List<MappedPage> disposeNow;
        lock (_lock)
        {
            if (string.Equals(_path, path, StringComparison.Ordinal))
                return;
            _path = path;
            disposeNow = ResetPagesLocked();
        }

        DisposePages(disposeNow);
    }

    public bool TryAcquirePageHandle(long filePageIndex, long fileSize, out IPageHandle? handle)
    {
        handle = null;
        if (filePageIndex < 0) return false;
        if (fileSize <= 0) return false;

        lock (_lock)
        {
            if (_disabled) return false;

            var pageStart = checked(filePageIndex * LinuxConstants.PageSize);
            if (pageStart < 0 || pageStart >= fileSize) return false;
            if (checked(pageStart + LinuxConstants.PageSize) > fileSize)
            {
                // Mapping the last partial page would require file growth, which changes file-size semantics.
                return false;
            }

            if (_pages.TryGetValue(filePageIndex, out var existing))
            {
                existing.RefCount++;
                handle = new PageHandle(this, filePageIndex, existing);
                return true;
            }

            try
            {
                var access = _writable ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.Read;
                var mmf = MemoryMappedFile.CreateFromFile(_path, FileMode.Open, mapName: null, capacity: 0, access);
                var view = mmf.CreateViewAccessor(pageStart, LinuxConstants.PageSize, access);

                unsafe
                {
                    byte* raw = null;
                    view.SafeMemoryMappedViewHandle.AcquirePointer(ref raw);
                    if (raw == null)
                    {
                        view.Dispose();
                        mmf.Dispose();
                        return false;
                    }

                    var rawPtr = (nint)raw;
                    var ptr = rawPtr + (nint)view.PointerOffset;
                    var mapped = new MappedPage
                    {
                        Mmf = mmf,
                        View = view,
                        RawPtr = rawPtr,
                        Ptr = ptr,
                        RefCount = 1,
                        Retired = false
                    };
                    _pages[filePageIndex] = mapped;
                    handle = new PageHandle(this, filePageIndex, mapped);
                    return true;
                }
            }
            catch (PlatformNotSupportedException)
            {
                _disabled = true;
                var disposeNow = ResetPagesLocked();
                DisposePages(disposeNow);
                return false;
            }
            catch (NotSupportedException)
            {
                _disabled = true;
                var disposeNow = ResetPagesLocked();
                DisposePages(disposeNow);
                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Truncate(long size)
    {
        if (size < 0) size = 0;
        List<MappedPage>? disposeNow = null;
        lock (_lock)
        {
            if (_disabled) return;
            var firstDroppedPage = (size + LinuxConstants.PageOffsetMask) / LinuxConstants.PageSize;
            var keys = _pages.Keys.Where(k => k >= firstDroppedPage).ToArray();
            disposeNow = [];
            foreach (var key in keys)
            {
                var page = _pages[key];
                _pages.Remove(key);
                if (page.RefCount == 0) disposeNow.Add(page);
                else page.Retired = true;
            }
        }

        if (disposeNow != null)
            DisposePages(disposeNow);
    }

    public void Dispose()
    {
        List<MappedPage> disposeNow;
        lock (_lock)
        {
            disposeNow = ResetPagesLocked();
        }

        DisposePages(disposeNow);
    }

    private void ReleaseHandle(long pageIndex, MappedPage page)
    {
        var shouldDispose = false;
        lock (_lock)
        {
            if (page.RefCount > 0) page.RefCount--;
            if (page.RefCount == 0 && page.Retired)
            {
                if (_pages.TryGetValue(pageIndex, out var tracked) && ReferenceEquals(tracked, page))
                    _pages.Remove(pageIndex);
                shouldDispose = true;
            }
        }

        if (shouldDispose)
            page.Dispose();
    }

    private List<MappedPage> ResetPagesLocked()
    {
        var disposeNow = new List<MappedPage>(_pages.Count);
        foreach (var page in _pages.Values)
        {
            if (page.RefCount == 0) disposeNow.Add(page);
            else page.Retired = true;
        }

        _pages.Clear();
        return disposeNow;
    }

    private static void DisposePages(List<MappedPage> pages)
    {
        foreach (var page in pages)
            page.Dispose();
    }

    private static bool IsSupportedOnCurrentPlatform()
    {
        if (OperatingSystem.IsBrowser()) return false;
#if NET8_0_OR_GREATER
        if (OperatingSystem.IsWasi()) return false;
#endif
        return true;
    }
}
