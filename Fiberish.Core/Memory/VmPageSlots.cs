using Fiberish.Native;

namespace Fiberish.Memory;

public sealed class VmPageSlots
{
    private readonly object _lock = new();
    private readonly Dictionary<uint, VmPage> _pages = new();

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

    public VmPage? PeekVmPage(uint pageIndex)
    {
        lock (_lock)
        {
            return _pages.TryGetValue(pageIndex, out var entry) ? entry : null;
        }
    }

    public void InstallPage(uint pageIndex, IntPtr ptr)
    {
        lock (_lock)
        {
            _pages[pageIndex] = new VmPage
            {
                Ptr = ptr,
                LastAccessTicks = DateTime.UtcNow.Ticks
            };
        }
    }

    public IntPtr InstallPageIfAbsent(uint pageIndex, IntPtr ptr, out bool inserted)
    {
        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var existing))
            {
                inserted = false;
                existing.LastAccessTicks = DateTime.UtcNow.Ticks;
                return existing.Ptr;
            }

            _pages[pageIndex] = new VmPage
            {
                Ptr = ptr,
                LastAccessTicks = DateTime.UtcNow.Ticks
            };
            inserted = true;
            return ptr;
        }
    }

    public IntPtr GetOrCreatePage(uint pageIndex, Func<IntPtr, bool>? onFirstCreate, out bool isNew)
    {
        return GetOrCreatePage(pageIndex, onFirstCreate, out isNew, false, AllocationClass.PageCache);
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

            _pages[pageIndex] = new VmPage
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
                unsafe
                {
                    var span = new Span<byte>((void*)tailPage.Ptr, LinuxConstants.PageSize);
                    span[tailBytes..].Clear();
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

    public VmPageSlots CloneSharedPagesForFork()
    {
        var clone = new VmPageSlots();
        lock (_lock)
        {
            foreach (var (pageIndex, page) in _pages)
            {
                ExternalPageManager.AddRef(page.Ptr);
                clone._pages[pageIndex] = new VmPage
                {
                    Ptr = page.Ptr,
                    Dirty = page.Dirty,
                    LastAccessTicks = DateTime.UtcNow.Ticks,
                    Uptodate = page.Uptodate,
                    Writeback = page.Writeback,
                    PinCount = page.PinCount,
                    MapCount = page.MapCount
                };
            }
        }

        return clone;
    }

    public IReadOnlyList<VmPageState> SnapshotPageStates()
    {
        var states = new List<VmPageState>();
        VisitPageStates(states.Add);
        return states;
    }

    public void VisitPageStates(Action<VmPageState> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        lock (_lock)
        {
            if (_pages.Count == 0) return;
            foreach (var (pageIndex, entry) in _pages)
                visitor(new VmPageState(pageIndex, entry.Ptr, entry.Dirty, entry.LastAccessTicks));
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
        IntPtr ptr;
        lock (_lock)
        {
            if (!_pages.TryGetValue(pageIndex, out var entry)) return false;
            if (entry.Dirty) return false;
            ptr = entry.Ptr;
            if (ExternalPageManager.GetRefCount(ptr) > 1) return false;
            _pages.Remove(pageIndex);
        }

        ExternalPageManager.ReleasePtr(ptr);
        return true;
    }

    public int RemovePagesInRange(uint startPageIndex, uint endPageIndex, Func<VmPage, bool>? predicate = null)
    {
        if (startPageIndex >= endPageIndex) return 0;
        List<IntPtr>? toRelease = null;
        var removedCount = 0;
        lock (_lock)
        {
            if (_pages.Count == 0) return 0;
            var keysToDrop = _pages
                .Where(kv => kv.Key >= startPageIndex && kv.Key < endPageIndex &&
                             (predicate == null || predicate(kv.Value)))
                .Select(kv => kv.Key)
                .ToArray();
            if (keysToDrop.Length == 0) return 0;

            toRelease = new List<IntPtr>(keysToDrop.Length);
            foreach (var key in keysToDrop)
            {
                toRelease.Add(_pages[key].Ptr);
                _pages.Remove(key);
            }

            removedCount = keysToDrop.Length;
        }

        if (toRelease != null)
            foreach (var ptr in toRelease)
                ExternalPageManager.ReleasePtr(ptr);
        return removedCount;
    }

    public bool RemovePageIfMatches(uint pageIndex, VmPage page)
    {
        IntPtr ptr;
        lock (_lock)
        {
            if (!_pages.TryGetValue(pageIndex, out var existing)) return false;
            if (!ReferenceEquals(existing, page)) return false;
            ptr = existing.Ptr;
            _pages.Remove(pageIndex);
        }

        ExternalPageManager.ReleasePtr(ptr);
        return true;
    }

    public void ReleaseAll()
    {
        List<IntPtr>? toRelease = null;
        lock (_lock)
        {
            if (_pages.Count == 0) return;
            toRelease = _pages.Values.Select(static entry => entry.Ptr).ToList();
            _pages.Clear();
        }

        foreach (var ptr in toRelease)
            ExternalPageManager.ReleasePtr(ptr);
    }
}

public sealed class VmPage
{
    public required IntPtr Ptr { get; set; }
    public bool Dirty { get; set; }
    public bool Uptodate { get; set; } = true;
    public bool Writeback { get; set; }
    public int MapCount { get; set; }
    public int PinCount { get; set; }
    public long LastAccessTicks { get; set; }
}

public readonly record struct VmPageState(uint PageIndex, IntPtr Ptr, bool Dirty, long LastAccessTicks);
