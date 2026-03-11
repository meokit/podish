using Fiberish.VFS;
using Fiberish.Native;

namespace Fiberish.Memory;

public enum MemoryObjectKind
{
    Anonymous,
    File,
    Image
}

public enum MemoryObjectRole
{
    FileSharedSource,
    ShmemSharedSource,
    AnonSharedSourceZeroFill,
    PrivateOverlay
}

public sealed class MemoryObject
{
    private sealed class PageEntry
    {
        public required IntPtr Ptr { get; set; }
        public bool Dirty { get; set; }
        public long LastAccessTicks { get; set; }
    }

    private readonly Dictionary<uint, PageEntry> _pages = new();
    private readonly object _lock = new();
    private int _refCount = 1;

    public MemoryObject(MemoryObjectKind kind, LinuxFile? file, long fileBaseOffset, long fileSize, bool shared,
        MemoryObjectRole? role = null)
    {
        Kind = kind;
        File = file;
        FileBaseOffset = fileBaseOffset;
        FileSize = fileSize;
        IsShared = shared;
        Role = role ?? InferRole(kind, shared);
    }

    public MemoryObjectKind Kind { get; }
    public MemoryObjectRole Role { get; }
    public LinuxFile? File { get; }
    public long FileBaseOffset { get; }
    public long FileSize { get; }
    public bool IsShared { get; }
    public bool IsRecoverableWithoutSwap => Role is MemoryObjectRole.FileSharedSource or MemoryObjectRole.AnonSharedSourceZeroFill;
    public bool IsPrivateOverlay => Role == MemoryObjectRole.PrivateOverlay;

    private static MemoryObjectRole InferRole(MemoryObjectKind kind, bool shared)
    {
        return kind switch
        {
            MemoryObjectKind.File => MemoryObjectRole.FileSharedSource,
            MemoryObjectKind.Anonymous when shared => MemoryObjectRole.ShmemSharedSource,
            MemoryObjectKind.Anonymous => MemoryObjectRole.PrivateOverlay,
            _ => MemoryObjectRole.PrivateOverlay
        };
    }

    public void AddRef()
    {
        lock (_lock)
        {
            _refCount++;
        }
    }

    /// <summary>
    /// Try to get an existing page without creating it.
    /// </summary>
    public IntPtr GetPage(uint pageIndex)
    {
        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var entry))
            {
                entry.LastAccessTicks = DateTime.UtcNow.Ticks;
                return entry.Ptr;
            }

            return IntPtr.Zero;
        }
    }

    public IntPtr PeekPage(uint pageIndex)
    {
        lock (_lock)
        {
            return _pages.TryGetValue(pageIndex, out var entry) ? entry.Ptr : IntPtr.Zero;
        }
    }

    /// <summary>
    /// Used by COW: store a private page that was just allocated.
    /// </summary>
    internal void SetPage(uint pageIndex, IntPtr ptr)
    {
        lock (_lock)
        {
            _pages[pageIndex] = new PageEntry
            {
                Ptr = ptr,
                LastAccessTicks = DateTime.UtcNow.Ticks
            };
        }
    }

    internal IntPtr SetPageIfAbsent(uint pageIndex, IntPtr ptr, out bool inserted)
    {
        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var existing))
            {
                inserted = false;
                existing.LastAccessTicks = DateTime.UtcNow.Ticks;
                return existing.Ptr;
            }

            _pages[pageIndex] = new PageEntry
            {
                Ptr = ptr,
                LastAccessTicks = DateTime.UtcNow.Ticks
            };
            inserted = true;
            return ptr;
        }
    }

    public void Release()
    {
        List<IntPtr>? toRelease = null;
        lock (_lock)
        {
            _refCount--;
            if (_refCount > 0) return;
            toRelease = _pages.Values.Select(static entry => entry.Ptr).ToList();
            _pages.Clear();
        }

        foreach (var ptr in toRelease) ExternalPageManager.ReleasePtr(ptr);
    }

    public IntPtr GetOrCreatePage(uint pageIndex, Func<IntPtr, bool>? onFirstCreate, out bool isNew)
    {
        return GetOrCreatePage(pageIndex, onFirstCreate, out isNew, strictQuota: false, AllocationClass.PageCache,
            AllocationSource.Unknown);
    }

    public IntPtr GetOrCreatePage(uint pageIndex, Func<IntPtr, bool>? onFirstCreate, out bool isNew,
        bool strictQuota, AllocationClass allocationClass, AllocationSource allocationSource = AllocationSource.Unknown)
    {
        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var existing))
            {
                isNew = false;
                existing.LastAccessTicks = DateTime.UtcNow.Ticks;
                return existing.Ptr;
            }
        }

        IntPtr ptr;
        if (strictQuota)
        {
            if (!ExternalPageManager.TryAllocateExternalPageStrict(out ptr, allocationClass, allocationSource))
            {
                isNew = false;
                return IntPtr.Zero;
            }
        }
        else
        {
            ptr = ExternalPageManager.AllocateExternalPage(allocationClass, allocationSource);
        }

        if (ptr == IntPtr.Zero)
        {
            isNew = false;
            return IntPtr.Zero;
        }

        if (onFirstCreate != null && !onFirstCreate(ptr))
        {
            ExternalPageManager.ReleasePtr(ptr);
            isNew = false;
            return IntPtr.Zero;
        }

        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var raced))
            {
                ExternalPageManager.ReleasePtr(ptr);
                isNew = false;
                raced.LastAccessTicks = DateTime.UtcNow.Ticks;
                return raced.Ptr;
            }

            _pages[pageIndex] = new PageEntry
            {
                Ptr = ptr,
                LastAccessTicks = DateTime.UtcNow.Ticks
            };
            isNew = true;
            return ptr;
        }
    }

    public void MarkDirty(uint pageIndex)
    {
        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var entry))
            {
                entry.Dirty = true;
                entry.LastAccessTicks = DateTime.UtcNow.Ticks;
            }
        }
    }

    public bool IsDirty(uint pageIndex)
    {
        lock (_lock)
        {
            return _pages.TryGetValue(pageIndex, out var entry) && entry.Dirty;
        }
    }

    public void ClearDirty(uint pageIndex)
    {
        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var entry))
                entry.Dirty = false;
        }
    }

    public void TruncateToSize(long size)
    {
        if (size < 0) size = 0;
        List<IntPtr>? toRelease = null;
        lock (_lock)
        {
            var keepPageIndex = (uint)(size / LinuxConstants.PageSize);
            var tailBytes = (int)(size % LinuxConstants.PageSize);

            if (tailBytes > 0 && _pages.TryGetValue(keepPageIndex, out var tailPage))
            {
                unsafe
                {
                    var span = new Span<byte>((void*)tailPage.Ptr, LinuxConstants.PageSize);
                    span[tailBytes..].Clear();
                }
            }

            var removeFrom = tailBytes == 0 ? keepPageIndex : keepPageIndex + 1;
            if (_pages.Count == 0) return;

            var keysToDrop = _pages.Keys.Where(k => k >= removeFrom).ToArray();
            if (keysToDrop.Length == 0) return;

            toRelease = new List<IntPtr>(keysToDrop.Length);
            foreach (var key in keysToDrop)
            {
                toRelease.Add(_pages[key].Ptr);
                _pages.Remove(key);
            }
        }

        if (toRelease == null) return;
        foreach (var ptr in toRelease) ExternalPageManager.ReleasePtr(ptr);
    }

    /// <summary>
        /// Clone this object by sharing page pointers (AddRef each page) rather than copying bytes.
    /// Used for fork-time cloning of private-page containers so each process gets its own
    /// metadata object while still deferring physical copy until the next write fault.
    /// </summary>
    public MemoryObject ForkCloneSharingPages()
    {
        var clone = new MemoryObject(Kind, File, FileBaseOffset, FileSize, false, Role);
        lock (_lock)
        {
            foreach (var (pageIndex, pagePtr) in _pages)
            {
                ExternalPageManager.AddRef(pagePtr.Ptr);
                clone._pages[pageIndex] = new PageEntry
                {
                    Ptr = pagePtr.Ptr,
                    Dirty = pagePtr.Dirty,
                    LastAccessTicks = DateTime.UtcNow.Ticks
                };
            }
        }

        return clone;
    }

    public int PageCount
    {
        get
        {
            lock (_lock)
            {
                return _pages.Count;
            }
        }
    }

    public readonly record struct PageState(uint PageIndex, IntPtr Ptr, bool Dirty, long LastAccessTicks);

    public IReadOnlyList<PageState> SnapshotPageStates()
    {
        lock (_lock)
        {
            if (_pages.Count == 0) return Array.Empty<PageState>();
            var states = new List<PageState>(_pages.Count);
            foreach (var (pageIndex, entry) in _pages)
            {
                states.Add(new PageState(pageIndex, entry.Ptr, entry.Dirty, entry.LastAccessTicks));
            }

            return states;
        }
    }

    public long CountPagesInRange(uint startPageIndex, uint endPageIndex)
    {
        if (startPageIndex >= endPageIndex) return 0;

        long count = 0;
        lock (_lock)
        {
            foreach (var pageIndex in _pages.Keys)
            {
                if (pageIndex < startPageIndex || pageIndex >= endPageIndex)
                    continue;
                count++;
            }
        }

        return count;
    }

    public bool TryEvictCleanPage(uint pageIndex)
    {
        if (!IsRecoverableWithoutSwap) return false;
        IntPtr ptr;
        lock (_lock)
        {
            if (!_pages.TryGetValue(pageIndex, out var entry)) return false;
            if (entry.Dirty) return false;
            ptr = entry.Ptr;
            // If referenced elsewhere (e.g. mapped by an engine), skip eviction.
            if (ExternalPageManager.GetRefCount(ptr) > 1) return false;
            _pages.Remove(pageIndex);
        }

        ExternalPageManager.ReleasePtr(ptr);
        return true;
    }
}
