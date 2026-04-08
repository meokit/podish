using Fiberish.Native;

namespace Fiberish.Memory;

internal sealed class VmPageSlots
{
    private readonly Lock _lock = new();
    private readonly Func<uint, HostPageOwnerRef> _ownerRefFactory;
    private readonly Dictionary<uint, VmPage> _pages = [];

    internal VmPageSlots(Func<uint, HostPageOwnerRef> ownerRefFactory)
    {
        _ownerRefFactory = ownerRefFactory;
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

    public IntPtr GetPage(uint pageIndex)
    {
        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var entry))
            {
                entry.HostPage.LastAccessTicks = DateTime.UtcNow.Ticks;
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

    internal HostPage? PeekHostPage(uint pageIndex)
    {
        lock (_lock)
        {
            return _pages.TryGetValue(pageIndex, out var entry) ? entry.HostPage : null;
        }
    }

    internal VmPage? PeekVmPage(uint pageIndex)
    {
        lock (_lock)
        {
            return _pages.TryGetValue(pageIndex, out var entry) ? entry : null;
        }
    }

    internal void InstallPage(uint pageIndex, IntPtr ptr, HostPageKind hostPageKind)
    {
        var hostPage = HostPageManager.GetOrCreate(ptr, hostPageKind);
        InstallExistingHostPage(pageIndex, hostPage);
    }

    internal void InstallExistingHostPage(uint pageIndex, HostPage hostPage)
    {
        lock (_lock)
        {
            var entry = new VmPage
            {
                HostPage = hostPage
            };
            hostPage.AddOwnerRef(_ownerRefFactory(pageIndex));
            _pages[pageIndex] = entry;
            hostPage.LastAccessTicks = DateTime.UtcNow.Ticks;
        }
    }

    internal void ReplacePage(uint pageIndex, IntPtr ptr, HostPageKind hostPageKind)
    {
        var oldPage = default(VmPage);
        var hostPage = HostPageManager.GetOrCreate(ptr, hostPageKind);

        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var existing))
            {
                if (ReferenceEquals(existing.HostPage, hostPage))
                {
                    existing.HostPage.LastAccessTicks = DateTime.UtcNow.Ticks;
                    return;
                }

                oldPage = existing;
            }

            hostPage.AddOwnerRef(_ownerRefFactory(pageIndex));
            _pages[pageIndex] = new VmPage
            {
                HostPage = hostPage
            };
            hostPage.LastAccessTicks = DateTime.UtcNow.Ticks;
        }

        if (oldPage != null)
            ReleasePageOwnership(pageIndex, oldPage);
    }

    internal IntPtr InstallPageIfAbsent(uint pageIndex, IntPtr ptr, HostPageKind hostPageKind, out bool inserted)
    {
        var hostPage = HostPageManager.GetOrCreate(ptr, hostPageKind);

        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var existing))
            {
                inserted = false;
                existing.HostPage.LastAccessTicks = DateTime.UtcNow.Ticks;
                return existing.Ptr;
            }

            hostPage.AddOwnerRef(_ownerRefFactory(pageIndex));
            _pages[pageIndex] = new VmPage
            {
                HostPage = hostPage
            };
            hostPage.LastAccessTicks = DateTime.UtcNow.Ticks;
            inserted = true;
            return ptr;
        }
    }

    public IntPtr GetOrCreatePage(uint pageIndex, Func<IntPtr, bool>? onFirstCreate, out bool isNew,
        bool strictQuota, AllocationClass allocationClass, HostPageKind hostPageKind,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var existing))
            {
                isNew = false;
                existing.HostPage.LastAccessTicks = DateTime.UtcNow.Ticks;
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

        var hostPage = HostPageManager.GetOrCreate(ptr, hostPageKind);
        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var raced))
            {
                ExternalPageManager.ReleasePtr(ptr);
                isNew = false;
                raced.HostPage.LastAccessTicks = DateTime.UtcNow.Ticks;
                return raced.Ptr;
            }

            hostPage.AddOwnerRef(_ownerRefFactory(pageIndex));
            _pages[pageIndex] = new VmPage
            {
                HostPage = hostPage
            };
            hostPage.LastAccessTicks = DateTime.UtcNow.Ticks;
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
                entry.HostPage.LastAccessTicks = DateTime.UtcNow.Ticks;
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
        List<(uint PageIndex, VmPage Page)>? toRelease = null;
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

            toRelease = new List<(uint PageIndex, VmPage Page)>(keysToDrop.Length);
            foreach (var key in keysToDrop)
            {
                toRelease.Add((key, _pages[key]));
                _pages.Remove(key);
            }
        }

        if (toRelease == null) return;
        foreach (var (pageIndex, page) in toRelease)
            ReleasePageOwnership(pageIndex, page);
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
        VmPage? page = null;
        lock (_lock)
        {
            if (!_pages.TryGetValue(pageIndex, out var entry)) return false;
            if (entry.Dirty) return false;
            if (ExternalPageManager.GetRefCount(entry.Ptr) > 1) return false;
            page = entry;
            _pages.Remove(pageIndex);
        }

        ReleasePageOwnership(pageIndex, page);
        return true;
    }

    public int RemovePagesInRange(uint startPageIndex, uint endPageIndex, Func<VmPage, bool>? predicate = null)
    {
        if (startPageIndex >= endPageIndex) return 0;
        List<(uint PageIndex, VmPage Page)>? toRelease = null;
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

            toRelease = new List<(uint PageIndex, VmPage Page)>(keysToDrop.Length);
            foreach (var key in keysToDrop)
            {
                toRelease.Add((key, _pages[key]));
                _pages.Remove(key);
            }

            removedCount = keysToDrop.Length;
        }

        if (toRelease != null)
            foreach (var (pageIndex, page) in toRelease)
                ReleasePageOwnership(pageIndex, page);
        return removedCount;
    }

    public bool RemovePageIfMatches(uint pageIndex, VmPage page)
    {
        lock (_lock)
        {
            if (!_pages.TryGetValue(pageIndex, out var existing)) return false;
            if (!ReferenceEquals(existing, page)) return false;
            _pages.Remove(pageIndex);
        }

        ReleasePageOwnership(pageIndex, page);
        return true;
    }

    public void ReleaseAll()
    {
        List<(uint PageIndex, VmPage Page)>? toRelease = null;
        lock (_lock)
        {
            if (_pages.Count == 0) return;
            toRelease = _pages.Select(static kv => (kv.Key, kv.Value)).ToList();
            _pages.Clear();
        }

        foreach (var (pageIndex, page) in toRelease)
            ReleasePageOwnership(pageIndex, page);
    }

    private void ReleasePageOwnership(uint pageIndex, VmPage page)
    {
        page.HostPage.RemoveOwnerRef(_ownerRefFactory(pageIndex));
        ExternalPageManager.ReleasePtr(page.Ptr);
    }
}

internal sealed class VmPage
{
    public required HostPage HostPage { get; set; }
    public IntPtr Ptr => HostPage.Ptr;

    public bool Dirty
    {
        get => HostPage.Dirty;
        set => HostPage.Dirty = value;
    }

    public bool Uptodate
    {
        get => HostPage.Uptodate;
        set => HostPage.Uptodate = value;
    }

    public bool Writeback
    {
        get => HostPage.Writeback;
        set => HostPage.Writeback = value;
    }

    public int MapCount
    {
        get => HostPage.MapCount;
        set => HostPage.MapCount = value;
    }

    public int PinCount
    {
        get => HostPage.PinCount;
        set => HostPage.PinCount = value;
    }

    public long LastAccessTicks
    {
        get => HostPage.LastAccessTicks;
        set => HostPage.LastAccessTicks = value;
    }
}

public readonly record struct VmPageState(uint PageIndex, IntPtr Ptr, bool Dirty, long LastAccessTicks)
{
}
