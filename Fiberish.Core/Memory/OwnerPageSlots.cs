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
        var hostPage = HostPageManager.GetOrCreate(ptr, hostPageKind);
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
            hostPage.BindOwnerRoot(ownerBinding);
            _pages[pageIndex] = CreateVmPage(hostPage, hostPageKind, ownerBinding, onReleased);
            Touch(_pages[pageIndex]);
            if (oldPage == null)
                pageCountDelta = 1;
        }

        ApplyPageCountDelta(pageCountDelta);
        if (oldPage != null)
            ReleasePageOwnership(pageIndex, oldPage, false);
        _pageBindingChanged?.Invoke(pageIndex, oldPage?.Ptr ?? IntPtr.Zero, hostPage.Ptr);
    }

    internal void ReplacePage(uint pageIndex, IntPtr ptr, HostPageKind hostPageKind)
    {
        BackingPageHandle backingHandle = default;
        ReplacePage(pageIndex, ptr, ref backingHandle, hostPageKind);
    }

    internal void ReplacePage(uint pageIndex, ref BackingPageHandle backingHandle, HostPageKind hostPageKind)
    {
        ReplacePage(pageIndex, backingHandle.Pointer, ref backingHandle, hostPageKind);
    }

    private void ReplacePage(uint pageIndex, IntPtr ptr, ref BackingPageHandle backingHandle, HostPageKind hostPageKind)
    {
        ResidentPageRecord? oldPage = null;
        var pageCountDelta = 0;
        HostPageRef hostPage;

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

            hostPage = EnsureHostPageRegistered(ptr, hostPageKind, ref backingHandle);
            var ownerBinding = _ownerBindingFactory(pageIndex);
            hostPage.BindOwnerRoot(ownerBinding);
            _pages[pageIndex] = CreateVmPage(hostPage, hostPageKind, ownerBinding);
            Touch(_pages[pageIndex]);
            if (oldPage == null)
                pageCountDelta = 1;
        }

        ApplyPageCountDelta(pageCountDelta);
        if (oldPage != null)
            ReleasePageOwnership(pageIndex, oldPage, false);
        _pageBindingChanged?.Invoke(pageIndex, oldPage?.Ptr ?? IntPtr.Zero, hostPage.Ptr);
    }

    internal IntPtr InstallHostPageIfAbsent(uint pageIndex, IntPtr ptr, HostPageKind hostPageKind,
        Action<ResidentPageRecord>? onReleased, out bool inserted)
    {
        BackingPageHandle backingHandle = default;
        return InstallHostPageIfAbsent(pageIndex, ptr, ref backingHandle, hostPageKind, onReleased, out inserted);
    }

    internal IntPtr InstallHostPageIfAbsent(uint pageIndex, IntPtr ptr, ref BackingPageHandle backingHandle,
        HostPageKind hostPageKind, Action<ResidentPageRecord>? onReleased, out bool inserted)
    {
        var pageCountDelta = 0;
        ptr = ptr != IntPtr.Zero ? ptr : backingHandle.Pointer;
        HostPageRef hostPage;

        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var existing))
            {
                inserted = false;
                Touch(existing);
                return existing.Ptr;
            }

            hostPage = EnsureHostPageRegistered(ptr, hostPageKind, ref backingHandle);
            var ownerBinding = _ownerBindingFactory(pageIndex);
            hostPage.BindOwnerRoot(ownerBinding);
            _pages[pageIndex] = CreateVmPage(hostPage, hostPageKind, ownerBinding, onReleased);
            Touch(_pages[pageIndex]);
            inserted = true;
            pageCountDelta = 1;
        }

        ApplyPageCountDelta(pageCountDelta);
        _pageBindingChanged?.Invoke(pageIndex, IntPtr.Zero, hostPage.Ptr);
        return hostPage.Ptr;
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

        BackingPageHandle backingPageHandle = default;
        if (hostPageKind == HostPageKind.Anon)
        {
            if (strictQuota)
            {
                if (!PageManager.TryAllocAnonPageMayFail(out backingPageHandle, allocationClass, allocationSource))
                {
                    isNew = false;
                    return IntPtr.Zero;
                }
            }
            else
            {
                backingPageHandle = PageManager.AllocAnonPage(allocationClass, allocationSource);
            }
        }
        else
        {
            if (strictQuota)
            {
                if (!PageManager.TryAllocatePoolBackedPageStrict(out backingPageHandle, allocationClass,
                        allocationSource))
                {
                    isNew = false;
                    return IntPtr.Zero;
                }
            }
            else
            {
                backingPageHandle = PageManager.AllocatePoolBackedPage(allocationClass, allocationSource);
            }
        }

        var ptr = backingPageHandle.Pointer;
        if (ptr == IntPtr.Zero)
        {
            isNew = false;
            return IntPtr.Zero;
        }

        if (onFirstCreate != null && !onFirstCreate(ptr))
        {
            BackingPageHandle.Release(ref backingPageHandle);
            isNew = false;
            return IntPtr.Zero;
        }

        var pageCountDelta = 0;
        HostPageRef hostPage;
        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var raced))
            {
                BackingPageHandle.Release(ref backingPageHandle);
                isNew = false;
                Touch(raced);
                return raced.Ptr;
            }

            hostPage = EnsureHostPageRegistered(ptr, hostPageKind, ref backingPageHandle);
            var ownerBinding = _ownerBindingFactory(pageIndex);
            hostPage.BindOwnerRoot(ownerBinding);
            _pages[pageIndex] = CreateVmPage(hostPage, hostPageKind, ownerBinding);
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
            if (entry.HostPage.OwnerResidentCount > 1) return false;
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
            _pageBindingChanged?.Invoke(pageIndex, pageRecord.Ptr, IntPtr.Zero);

        if (pageRecord.OnReleased != null)
            pageRecord.OnReleased(pageRecord);

        HostPageManager.UnbindOwnerRoot(pageRecord.Ptr, pageRecord.OwnerBinding);
        HostPageManager.TryRemoveIfUnused(pageRecord.Ptr);
    }

    private static ResidentPageRecord CreateVmPage(HostPageRef hostPage, HostPageKind hostPageKind,
        HostPageOwnerBinding ownerBinding,
        Action<ResidentPageRecord>? onReleased = null)
    {
        return new ResidentPageRecord
        {
            Ptr = hostPage.Ptr,
            HostPage = hostPage,
            HostPageKind = hostPageKind,
            OwnerBinding = ownerBinding,
            OnReleased = onReleased
        };
    }

    private static void Touch(ResidentPageRecord pageRecord)
    {
        var hostPage = pageRecord.HostPage;
        hostPage.LastAccessTimestamp = MonotonicTime.GetTimestamp();
    }

    private static HostPageRef EnsureHostPageRegistered(IntPtr ptr, HostPageKind hostPageKind,
        ref BackingPageHandle backingPageHandle)
    {
        if (ptr == IntPtr.Zero)
            throw new ArgumentException("Host page pointer must be non-zero.", nameof(ptr));

        if (!backingPageHandle.IsValid)
            return HostPageManager.GetOrCreate(ptr, hostPageKind);

        if (backingPageHandle.Pointer != ptr)
            throw new InvalidOperationException("Backing handle pointer does not match host page pointer.");

        return HostPageManager.CreateWithBacking(ref backingPageHandle, hostPageKind);
    }
}

internal sealed class ResidentPageRecord
{
    public required IntPtr Ptr { get; set; }
    public required HostPageRef HostPage { get; set; }
    public required HostPageKind HostPageKind { get; set; }
    public required HostPageOwnerBinding OwnerBinding { get; set; }
    public Action<ResidentPageRecord>? OnReleased { get; set; }

    public bool Dirty
    {
        get
        {
            var hostPage = HostPage;
            return hostPage.Dirty;
        }
        set
        {
            var hostPage = HostPage;
            hostPage.Dirty = value;
        }
    }

    public bool Uptodate
    {
        get
        {
            var hostPage = HostPage;
            return hostPage.Uptodate;
        }
        set
        {
            var hostPage = HostPage;
            hostPage.Uptodate = value;
        }
    }

    public bool Writeback
    {
        get
        {
            var hostPage = HostPage;
            return hostPage.Writeback;
        }
        set
        {
            var hostPage = HostPage;
            hostPage.Writeback = value;
        }
    }

    public int MapCount
    {
        get
        {
            var hostPage = HostPage;
            return hostPage.MapCount;
        }
        set
        {
            var hostPage = HostPage;
            hostPage.MapCount = value;
        }
    }

    public int PinCount
    {
        get
        {
            var hostPage = HostPage;
            return hostPage.PinCount;
        }
        set
        {
            var hostPage = HostPage;
            hostPage.PinCount = value;
        }
    }

    public long LastAccessTimestamp
    {
        get
        {
            var hostPage = HostPage;
            return hostPage.LastAccessTimestamp;
        }
        set
        {
            var hostPage = HostPage;
            hostPage.LastAccessTimestamp = value;
        }
    }
}

public readonly record struct VmPageState(uint PageIndex, IntPtr Ptr, bool Dirty, long LastAccessTimestamp)
{
}
