using Fiberish.Native;

namespace Fiberish.Memory;

internal sealed class OwnerPageSlots
{
    private readonly Lock _lock = new();
    private readonly Func<uint, HostPageOwnerBinding> _ownerBindingFactory;
    private readonly Action<uint, IntPtr, IntPtr>? _pageBindingChanged;
    private readonly Action<int>? _pageCountChanged;
    private readonly Dictionary<uint, ResidentPageRecord> _pages = [];
    private int _pageCount;

    internal OwnerPageSlots(Func<uint, HostPageOwnerBinding> ownerBindingFactory,
        Action<uint, IntPtr, IntPtr>? pageBindingChanged = null,
        Action<int>? pageCountChanged = null)
    {
        _ownerBindingFactory = ownerBindingFactory;
        _pageBindingChanged = pageBindingChanged;
        _pageCountChanged = pageCountChanged;
    }

    public int PageCount
    {
        get
        {
            return Volatile.Read(ref _pageCount);
        }
    }

    private void ApplyPageCountDelta(int delta)
    {
        if (delta == 0)
            return;

        Interlocked.Add(ref _pageCount, delta);
        _pageCountChanged?.Invoke(delta);
    }

    public IntPtr GetPage(uint pageIndex)
    {
        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var entry))
            {
                Touch(entry);
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

    internal ResidentPageRecord? PeekVmPage(uint pageIndex)
    {
        lock (_lock)
        {
            return _pages.TryGetValue(pageIndex, out var entry) ? entry : null;
        }
    }

    internal void InstallExistingHostPage(uint pageIndex, IntPtr ptr, HostPageKind hostPageKind,
        Action<ResidentPageRecord>? onReleased = null)
    {
        ResidentPageRecord? oldPage = null;
        var pageCountDelta = 0;
        HostPageManager.GetOrCreate(ptr, hostPageKind);
        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var existing))
            {
                if (existing.Ptr == ptr)
                {
                    Touch(existing);
                    return;
                }

                oldPage = existing;
            }

            var ownerBinding = _ownerBindingFactory(pageIndex);
            HostPageManager.BindOwnerRoot(ptr, hostPageKind, ownerBinding);
            _pages[pageIndex] = CreateVmPage(ptr, hostPageKind, ownerBinding, onReleased);
            Touch(_pages[pageIndex]);
            if (oldPage == null)
                pageCountDelta = 1;
        }

        ApplyPageCountDelta(pageCountDelta);
        if (oldPage != null)
            ReleasePageOwnership(pageIndex, oldPage, false);
        _pageBindingChanged?.Invoke(pageIndex, oldPage?.Ptr ?? IntPtr.Zero, ptr);
    }

    internal void ReplacePage(uint pageIndex, IntPtr ptr, HostPageKind hostPageKind)
    {
        ResidentPageRecord? oldPage = null;
        HostPageManager.GetOrCreate(ptr, hostPageKind);
        var pageCountDelta = 0;

        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var existing))
            {
                if (existing.Ptr == ptr)
                {
                    Touch(existing);
                    return;
                }

                oldPage = existing;
            }

            var ownerBinding = _ownerBindingFactory(pageIndex);
            HostPageManager.BindOwnerRoot(ptr, hostPageKind, ownerBinding);
            _pages[pageIndex] = CreateVmPage(ptr, hostPageKind, ownerBinding);
            Touch(_pages[pageIndex]);
            if (oldPage == null)
                pageCountDelta = 1;
        }

        ApplyPageCountDelta(pageCountDelta);
        if (oldPage != null)
            ReleasePageOwnership(pageIndex, oldPage, false);
        _pageBindingChanged?.Invoke(pageIndex, oldPage?.Ptr ?? IntPtr.Zero, ptr);
    }

    internal IntPtr InstallHostPageIfAbsent(uint pageIndex, IntPtr ptr, HostPageKind hostPageKind,
        Action<ResidentPageRecord>? onReleased, out bool inserted)
    {
        var pageCountDelta = 0;
        HostPageManager.GetOrCreate(ptr, hostPageKind);

        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var existing))
            {
                inserted = false;
                Touch(existing);
                return existing.Ptr;
            }

            var ownerBinding = _ownerBindingFactory(pageIndex);
            HostPageManager.BindOwnerRoot(ptr, hostPageKind, ownerBinding);
            _pages[pageIndex] = CreateVmPage(ptr, hostPageKind, ownerBinding, onReleased);
            Touch(_pages[pageIndex]);
            inserted = true;
            pageCountDelta = 1;
        }

        ApplyPageCountDelta(pageCountDelta);
        _pageBindingChanged?.Invoke(pageIndex, IntPtr.Zero, ptr);
        return ptr;
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
                Touch(existing);
                return existing.Ptr;
            }
        }

        IntPtr ptr;
        BackingPageHandle backingPageHandle = default;
        var usePageManager = hostPageKind == HostPageKind.Anon;
        if (usePageManager)
        {
            if (strictQuota)
            {
                if (!PageManager.TryAllocAnonPageMayFail(out ptr, allocationClass, allocationSource))
                {
                    isNew = false;
                    return IntPtr.Zero;
                }
            }
            else
            {
                ptr = PageManager.AllocAnonPage(allocationClass, allocationSource);
            }
        }
        else
        {
            if (strictQuota)
            {
                if (!InodePageAllocator.TryAllocatePageStrict(out backingPageHandle, allocationClass, allocationSource))
                {
                    isNew = false;
                    return IntPtr.Zero;
                }
            }
            else
            {
                backingPageHandle = InodePageAllocator.AllocatePage(allocationClass, allocationSource);
            }

            ptr = backingPageHandle.Pointer;
        }

        if (ptr == IntPtr.Zero)
        {
            isNew = false;
            return IntPtr.Zero;
        }

        if (onFirstCreate != null && !onFirstCreate(ptr))
        {
            if (backingPageHandle.IsValid)
                BackingPageHandle.Release(ref backingPageHandle);
            else
                PageManager.FreeAnonPage(ptr);
            isNew = false;
            return IntPtr.Zero;
        }

        var hostPage = HostPageManager.GetOrCreate(ptr, hostPageKind);
        var pageCountDelta = 0;
        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var raced))
            {
                if (backingPageHandle.IsValid)
                    BackingPageHandle.Release(ref backingPageHandle);
                else
                    PageManager.FreeAnonPage(ptr);
                isNew = false;
                Touch(raced);
                return raced.Ptr;
            }

            var ownerBinding = _ownerBindingFactory(pageIndex);
            HostPageManager.BindOwnerRoot(hostPage.Ptr, hostPageKind, ownerBinding);
            _pages[pageIndex] = CreateVmPage(hostPage.Ptr, hostPageKind, ownerBinding, null, backingPageHandle);
            Touch(_pages[pageIndex]);
            isNew = true;
            pageCountDelta = 1;
        }

        ApplyPageCountDelta(pageCountDelta);
        _pageBindingChanged?.Invoke(pageIndex, IntPtr.Zero, ptr);
        return ptr;
    }

    public void MarkDirty(uint pageIndex)
    {
        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var entry))
            {
                entry.Dirty = true;
                Touch(entry);
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
        List<(uint PageIndex, ResidentPageRecord Page)>? toRelease = null;
        var pageCountDelta = 0;
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

            foreach (var (pageIndex, page) in _pages)
            {
                if (pageIndex < removeFrom)
                    continue;

                toRelease ??= [];
                toRelease.Add((pageIndex, page));
            }

            if (toRelease == null) return;
            foreach (var (pageIndex, _) in toRelease)
                _pages.Remove(pageIndex);
            pageCountDelta = -toRelease.Count;
        }

        ApplyPageCountDelta(pageCountDelta);
        foreach (var (pageIndex, page) in toRelease)
            ReleasePageOwnership(pageIndex, page, true);
    }

    public IReadOnlyList<VmPageState> SnapshotPageStates()
    {
        lock (_lock)
        {
            if (_pages.Count == 0)
                return Array.Empty<VmPageState>();

            var states = new List<VmPageState>(_pages.Count);
            foreach (var (pageIndex, entry) in _pages)
                states.Add(new VmPageState(pageIndex, entry.Ptr, entry.Dirty, entry.LastAccessTimestamp));
            return states;
        }
    }

    public void VisitPageStates(Action<VmPageState> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        lock (_lock)
        {
            if (_pages.Count == 0) return;
            foreach (var (pageIndex, entry) in _pages)
                visitor(new VmPageState(pageIndex, entry.Ptr, entry.Dirty, entry.LastAccessTimestamp));
        }
    }

    public void GetPageStats(out int totalPages, out int dirtyPages)
    {
        lock (_lock)
        {
            totalPages = _pages.Count;
            dirtyPages = 0;
            if (totalPages == 0)
                return;

            foreach (var entry in _pages.Values)
                if (entry.Dirty)
                    dirtyPages++;
        }
    }

    internal void VisitResidentPagesInRange(uint startPageIndex, uint endPageIndexExclusive,
        Action<uint, ResidentPageRecord> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (startPageIndex >= endPageIndexExclusive)
            return;

        lock (_lock)
        {
            var rangePageCount = (ulong)endPageIndexExclusive - startPageIndex;
            if (rangePageCount < (ulong)_pages.Count)
            {
                for (var pageIndex = startPageIndex; pageIndex < endPageIndexExclusive; pageIndex++)
                    if (_pages.TryGetValue(pageIndex, out var page))
                        visitor(pageIndex, page);
                return;
            }

            foreach (var (pageIndex, page) in _pages)
                if (pageIndex >= startPageIndex && pageIndex < endPageIndexExclusive)
                    visitor(pageIndex, page);
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
        ResidentPageRecord? page = null;
        var pageCountDelta = 0;
        lock (_lock)
        {
            if (!_pages.TryGetValue(pageIndex, out var entry)) return false;
            if (entry.Dirty) return false;
            if (entry.MapCount > 0 || entry.PinCount > 0) return false;
            if (PageManager.GetAnonPageRefCount(entry.Ptr) > 1) return false;
            page = entry;
            _pages.Remove(pageIndex);
            pageCountDelta = -1;
        }

        ApplyPageCountDelta(pageCountDelta);
        ReleasePageOwnership(pageIndex, page, true);
        return true;
    }

    public int RemovePagesInRange(uint startPageIndex, uint endPageIndex, Func<ResidentPageRecord, bool>? predicate = null)
    {
        if (startPageIndex >= endPageIndex) return 0;
        List<(uint PageIndex, ResidentPageRecord Page)>? toRelease = null;
        var pageCountDelta = 0;
        lock (_lock)
        {
            if (_pages.Count == 0) return 0;
            foreach (var (pageIndex, page) in _pages)
            {
                if (pageIndex < startPageIndex || pageIndex >= endPageIndex)
                    continue;
                if (predicate != null && !predicate(page))
                    continue;

                toRelease ??= [];
                toRelease.Add((pageIndex, page));
            }

            if (toRelease == null) return 0;
            foreach (var (pageIndex, _) in toRelease)
                _pages.Remove(pageIndex);
            pageCountDelta = -toRelease.Count;
        }

        ApplyPageCountDelta(pageCountDelta);
        foreach (var (pageIndex, page) in toRelease)
            ReleasePageOwnership(pageIndex, page, true);
        return toRelease.Count;
    }

    public bool RemovePageIfMatches(uint pageIndex, ResidentPageRecord pageRecord)
    {
        var pageCountDelta = 0;
        lock (_lock)
        {
            if (!_pages.TryGetValue(pageIndex, out var existing)) return false;
            if (!ReferenceEquals(existing, pageRecord)) return false;
            _pages.Remove(pageIndex);
            pageCountDelta = -1;
        }

        ApplyPageCountDelta(pageCountDelta);
        ReleasePageOwnership(pageIndex, pageRecord, true);
        return true;
    }

    public void ReleaseAll()
    {
        List<(uint PageIndex, ResidentPageRecord Page)>? toRelease = null;
        var pageCountDelta = 0;
        lock (_lock)
        {
            if (_pages.Count == 0) return;
            toRelease = new List<(uint PageIndex, ResidentPageRecord Page)>(_pages.Count);
            foreach (var (pageIndex, page) in _pages)
                toRelease.Add((pageIndex, page));
            _pages.Clear();
            pageCountDelta = -toRelease.Count;
        }

        ApplyPageCountDelta(pageCountDelta);
        foreach (var (pageIndex, page) in toRelease)
            ReleasePageOwnership(pageIndex, page, true);
    }

    private void ReleasePageOwnership(uint pageIndex, ResidentPageRecord pageRecord, bool notify)
    {
        if (notify)
        {
            _pageBindingChanged?.Invoke(pageIndex, pageRecord.Ptr, IntPtr.Zero);
        }

        HostPageManager.UnbindOwnerRoot(pageRecord.Ptr, pageRecord.OwnerBinding);
        if (pageRecord.Handle.IsValid)
            BackingPageHandle.Release(ref pageRecord.Handle);
        else if (pageRecord.OnReleased != null)
            pageRecord.OnReleased(pageRecord);
        else
            PageManager.FreeAnonPage(pageRecord.Ptr);
    }

    private static ResidentPageRecord CreateVmPage(IntPtr ptr, HostPageKind hostPageKind, HostPageOwnerBinding ownerBinding,
        Action<ResidentPageRecord>? onReleased = null, BackingPageHandle handle = default)
    {
        return new ResidentPageRecord
        {
            Ptr = ptr,
            HostPageKind = hostPageKind,
            OwnerBinding = ownerBinding,
            OnReleased = onReleased,
            Handle = handle
        };
    }

    private static void Touch(ResidentPageRecord pageRecord)
    {
        var hostPage = HostPageManager.GetOrCreate(pageRecord.Ptr, pageRecord.HostPageKind);
        hostPage.LastAccessTimestamp = MonotonicTime.GetTimestamp();
    }
}

internal sealed class ResidentPageRecord
{
    public BackingPageHandle Handle;
    public required IntPtr Ptr { get; set; }
    public required HostPageKind HostPageKind { get; set; }
    public required HostPageOwnerBinding OwnerBinding { get; set; }
    public Action<ResidentPageRecord>? OnReleased { get; set; }

    public bool Dirty
    {
        get => HostPageManager.GetOrCreate(Ptr, HostPageKind).Dirty;
        set
        {
            var hostPage = HostPageManager.GetOrCreate(Ptr, HostPageKind);
            hostPage.Dirty = value;
        }
    }

    public bool Uptodate
    {
        get => HostPageManager.GetOrCreate(Ptr, HostPageKind).Uptodate;
        set
        {
            var hostPage = HostPageManager.GetOrCreate(Ptr, HostPageKind);
            hostPage.Uptodate = value;
        }
    }

    public bool Writeback
    {
        get => HostPageManager.GetOrCreate(Ptr, HostPageKind).Writeback;
        set
        {
            var hostPage = HostPageManager.GetOrCreate(Ptr, HostPageKind);
            hostPage.Writeback = value;
        }
    }

    public int MapCount
    {
        get => HostPageManager.GetOrCreate(Ptr, HostPageKind).MapCount;
        set
        {
            var hostPage = HostPageManager.GetOrCreate(Ptr, HostPageKind);
            hostPage.MapCount = value;
        }
    }

    public int PinCount
    {
        get => HostPageManager.GetOrCreate(Ptr, HostPageKind).PinCount;
        set
        {
            var hostPage = HostPageManager.GetOrCreate(Ptr, HostPageKind);
            hostPage.PinCount = value;
        }
    }

    public long LastAccessTimestamp
    {
        get => HostPageManager.GetOrCreate(Ptr, HostPageKind).LastAccessTimestamp;
        set
        {
            var hostPage = HostPageManager.GetOrCreate(Ptr, HostPageKind);
            hostPage.LastAccessTimestamp = value;
        }
    }
}

public readonly record struct VmPageState(uint PageIndex, IntPtr Ptr, bool Dirty, long LastAccessTimestamp)
{
}
