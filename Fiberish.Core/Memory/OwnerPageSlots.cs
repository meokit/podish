using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Memory;

internal sealed class OwnerPageSlots
{
    [ThreadStatic] private static InstallBatchScratch? _threadInstallBatchScratch;

    private sealed class InstallBatchScratch
    {
        public BackingPageHandle[] Handles = [];
        public HostPageOwnerBinding[] Bindings = [];
        public HostPageRef[] HostPages = [];
        public InodePageRecord?[] PendingRecords = [];
        public int[] PendingInputIndices = [];

        public void EnsureCapacity(int count)
        {
            if (Handles.Length < count)
                Array.Resize(ref Handles, count);
            if (Bindings.Length < count)
                Array.Resize(ref Bindings, count);
            if (HostPages.Length < count)
                Array.Resize(ref HostPages, count);
            if (PendingRecords.Length < count)
                Array.Resize(ref PendingRecords, count);
            if (PendingInputIndices.Length < count)
                Array.Resize(ref PendingInputIndices, count);
        }

        public void ClearPendingRecords(int count)
        {
            Array.Clear(PendingRecords, 0, count);
        }
    }

    private readonly Lock _lock = new();
    private readonly MemoryRuntimeContext _memoryContext;
    private readonly Func<uint, HostPageOwnerBinding> _ownerBindingFactory;
    private readonly Action<uint, IntPtr, IntPtr>? _pageBindingChanged;
    private readonly Action<int>? _pageCountChanged;
    private readonly Dictionary<uint, ResidentPageRecord> _pages;
    private int _pageCount;

    internal OwnerPageSlots(MemoryRuntimeContext memoryContext, Func<uint, HostPageOwnerBinding> ownerBindingFactory,
        Action<uint, IntPtr, IntPtr>? pageBindingChanged = null,
        Action<int>? pageCountChanged = null,
        int initialCapacity = 0)
    {
        _memoryContext = memoryContext;
        _ownerBindingFactory = ownerBindingFactory;
        _pageBindingChanged = pageBindingChanged;
        _pageCountChanged = pageCountChanged;
        _pages = initialCapacity > 0 ? new Dictionary<uint, ResidentPageRecord>(initialCapacity) : [];
    }

    public int PageCount => Volatile.Read(ref _pageCount);

    private static InstallBatchScratch GetInstallBatchScratch(int count)
    {
        var scratch = _threadInstallBatchScratch ??= new InstallBatchScratch();
        scratch.EnsureCapacity(count);
        return scratch;
    }

    private static int GetBatchInsertCapacity(int currentCount, int additionalEntries)
    {
        if (additionalEntries <= 0)
            return currentCount;

        var minRequired = checked(currentCount + additionalEntries);
        var growth = Math.Max(additionalEntries, Math.Max(256, currentCount / 2));
        return Math.Max(minRequired, checked(currentCount + growth));
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

    internal void CloneInto(OwnerPageSlots target, HostPageKind hostPageKind)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(this, target))
            throw new ArgumentException("Cannot clone page slots into the same instance.", nameof(target));

        var clonedCount = 0;
        lock (_lock)
        {
            if (_pages.Count == 0)
                return;

            lock (target._lock)
            {
                target._pages.EnsureCapacity(target._pages.Count + _pages.Count);
                foreach (var (pageIndex, page) in _pages)
                {
                    var hostPage = target._memoryContext.HostPages.GetOrCreate(page.Ptr, hostPageKind);
                    hostPage.BindOwnerRoot(target._ownerBindingFactory(pageIndex));
                    target._pages.Add(pageIndex, CreateVmPage(hostPage, null, null));
                    Touch(target._pages[pageIndex]);
                    clonedCount++;
                }
            }
        }

        target.ApplyPageCountDelta(clonedCount);
    }

    internal void InstallExistingHostPage(uint pageIndex, IntPtr ptr, HostPageKind hostPageKind)
    {
        ResidentPageRecord? oldPage = null;
        var pageCountDelta = 0;
        var hostPage = _memoryContext.HostPages.GetOrCreate(ptr, hostPageKind);
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
            _pages[pageIndex] = CreateVmPage(hostPage, null, null);
            Touch(_pages[pageIndex]);
            if (oldPage == null)
                pageCountDelta = 1;
        }

        ApplyPageCountDelta(pageCountDelta);
        if (oldPage != null)
            ReleasePageOwnership(pageIndex, oldPage.Value, false);
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
            _pages[pageIndex] = CreateVmPage(hostPage, null, null);
            Touch(_pages[pageIndex]);
            if (oldPage == null)
                pageCountDelta = 1;
        }

        ApplyPageCountDelta(pageCountDelta);
        if (oldPage != null)
            ReleasePageOwnership(pageIndex, oldPage.Value, false);
        _pageBindingChanged?.Invoke(pageIndex, oldPage?.Ptr ?? IntPtr.Zero, hostPage.Ptr);
    }

    internal IntPtr InstallHostPageIfAbsent(uint pageIndex, IntPtr ptr, ref BackingPageHandle backingHandle,
        HostPageKind hostPageKind, MappingBackedInode releaseOwner, InodePageRecord releaseRecord, out bool inserted)
    {
        ArgumentNullException.ThrowIfNull(releaseOwner);
        ArgumentNullException.ThrowIfNull(releaseRecord);
        return InstallHostPageIfAbsentCore(pageIndex, ptr, ref backingHandle, hostPageKind, releaseOwner, releaseRecord,
            out inserted);
    }

    internal void InstallHostPageRecordsIfAbsentBatch(ReadOnlySpan<InodePageRecord?> records, HostPageKind hostPageKind,
        MappingBackedInode releaseOwner, Span<IntPtr> finalPointers, Span<bool> inserted)
    {
        ArgumentNullException.ThrowIfNull(releaseOwner);
        if (finalPointers.Length < records.Length)
            throw new ArgumentException("Output span is smaller than record batch.", nameof(finalPointers));
        if (inserted.Length < records.Length)
            throw new ArgumentException("Inserted span is smaller than record batch.", nameof(inserted));

        var count = records.Length;
        if (count == 0)
            return;

        var scratch = GetInstallBatchScratch(count);
        var handles = scratch.Handles;
        var bindings = scratch.Bindings;
        var hostPages = scratch.HostPages;
        var pendingRecords = scratch.PendingRecords;
        var pendingInputIndices = scratch.PendingInputIndices;
        var accessTimestamp = MonotonicTime.GetTimestamp();
        var pageCountDelta = 0;
        var pendingCount = 0;
        try
        {
            lock (_lock)
            {
                for (var i = 0; i < count; i++)
                {
                    var record = records[i];
                    if (record == null)
                    {
                        finalPointers[i] = IntPtr.Zero;
                        inserted[i] = false;
                        continue;
                    }

                    if (_pages.TryGetValue(record.PageIndex, out var existing))
                    {
                        finalPointers[i] = existing.Ptr;
                        inserted[i] = false;
                        Touch(existing, accessTimestamp);
                        continue;
                    }

                    pendingInputIndices[pendingCount] = i;
                    pendingRecords[pendingCount] = record;
                    handles[pendingCount] = record.Handle;
                    bindings[pendingCount] = _ownerBindingFactory(record.PageIndex);
                    pendingCount++;
                }

                if (pendingCount == 0)
                    return;

                _pages.EnsureCapacity(GetBatchInsertCapacity(_pages.Count, pendingCount));
                _memoryContext.HostPages.CreateWithBackingsAndBindOwnerRoots(handles.AsSpan(0, pendingCount),
                    hostPageKind, bindings.AsSpan(0, pendingCount), accessTimestamp, hostPages.AsSpan(0, pendingCount));

                for (var i = 0; i < pendingCount; i++)
                {
                    var record = pendingRecords[i]!;
                    record.Handle = handles[i];
                    var hostPage = hostPages[i];
                    _pages[record.PageIndex] = CreateVmPage(hostPage, releaseOwner, record);
                    finalPointers[pendingInputIndices[i]] = hostPage.Ptr;
                    inserted[pendingInputIndices[i]] = true;
                }

                pageCountDelta = pendingCount;
            }

            ApplyPageCountDelta(pageCountDelta);
            for (var i = 0; i < pendingCount; i++)
            {
                var record = pendingRecords[i]!;
                _pageBindingChanged?.Invoke(record.PageIndex, IntPtr.Zero, finalPointers[pendingInputIndices[i]]);
            }
        }
        finally
        {
            for (var i = 0; i < pendingCount; i++)
            {
                if (pendingRecords[i] != null)
                    pendingRecords[i]!.Handle = handles[i];
            }
            scratch.ClearPendingRecords(pendingCount);
        }
    }

    private IntPtr InstallHostPageIfAbsentCore(uint pageIndex, IntPtr ptr, ref BackingPageHandle backingHandle,
        HostPageKind hostPageKind, MappingBackedInode? releaseOwner, InodePageRecord? releaseRecord,
        out bool inserted)
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
            _pages[pageIndex] = CreateVmPage(hostPage, releaseOwner, releaseRecord);
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
                if (!_memoryContext.BackingPagePool.TryAllocAnonPageMayFail(out backingPageHandle, allocationClass,
                        allocationSource))
                {
                    isNew = false;
                    return IntPtr.Zero;
                }
            }
            else
            {
                backingPageHandle = _memoryContext.BackingPagePool.AllocAnonPage(allocationClass, allocationSource);
            }
        }
        else
        {
            if (strictQuota)
            {
                if (!_memoryContext.BackingPagePool.TryAllocatePoolBackedPageStrict(out backingPageHandle,
                        allocationClass,
                        allocationSource))
                {
                    isNew = false;
                    return IntPtr.Zero;
                }
            }
            else
            {
                backingPageHandle =
                    _memoryContext.BackingPagePool.AllocatePoolBackedPage(allocationClass, allocationSource);
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
                var racedPtr = raced.Ptr;
                return racedPtr;
            }

            hostPage = EnsureHostPageRegistered(ptr, hostPageKind, ref backingPageHandle);
            var ownerBinding = _ownerBindingFactory(pageIndex);
            hostPage.BindOwnerRoot(ownerBinding);
            _pages[pageIndex] = CreateVmPage(hostPage, null, null);
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

    public void MarkDirtyRange(uint startPageIndex, uint endPageIndexInclusive)
    {
        if (endPageIndexInclusive < startPageIndex)
            return;

        var accessTimestamp = MonotonicTime.GetTimestamp();
        lock (_lock)
        {
            if (_pages.Count == 0)
                return;

            var rangePageCount = (ulong)endPageIndexInclusive - startPageIndex + 1;
            if (rangePageCount <= (ulong)_pages.Count)
            {
                for (var pageIndex = startPageIndex;; pageIndex++)
                {
                    if (_pages.TryGetValue(pageIndex, out var entry))
                    {
                        entry.Dirty = true;
                        Touch(entry, accessTimestamp);
                    }

                    if (pageIndex == endPageIndexInclusive)
                        break;
                }

                return;
            }

            foreach (var (pageIndex, entry) in _pages)
            {
                if (pageIndex < startPageIndex || pageIndex > endPageIndexInclusive)
                    continue;

                entry.Dirty = true;
                Touch(entry, accessTimestamp);
            }
        }
    }

    public int CountLeadingAbsentPages(uint startPageIndex, uint endPageIndexInclusive)
    {
        if (endPageIndexInclusive < startPageIndex)
            return 0;

        var rangePageCount = (ulong)endPageIndexInclusive - startPageIndex + 1;
        lock (_lock)
        {
            if (_pages.Count == 0)
                return checked((int)rangePageCount);

            if (rangePageCount <= (ulong)_pages.Count)
            {
                var missingCount = 0;
                for (var pageIndex = startPageIndex;; pageIndex++)
                {
                    if (_pages.ContainsKey(pageIndex))
                        return missingCount;

                    missingCount++;
                    if (pageIndex == endPageIndexInclusive)
                        return missingCount;
                }
            }

            var firstPresentPageIndex = uint.MaxValue;
            foreach (var pageIndex in _pages.Keys)
            {
                if (pageIndex < startPageIndex || pageIndex > endPageIndexInclusive)
                    continue;
                if (pageIndex == startPageIndex)
                    return 0;
                if (pageIndex < firstPresentPageIndex)
                    firstPresentPageIndex = pageIndex;
            }

            return firstPresentPageIndex == uint.MaxValue
                ? checked((int)rangePageCount)
                : checked((int)(firstPresentPageIndex - startPageIndex));
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

    internal List<uint> GetDirtyPageIndicesInRangeOrdered(long startPageIndexInclusive, long endPageIndexInclusive)
    {
        if (endPageIndexInclusive < startPageIndexInclusive)
            return [];

        if (endPageIndexInclusive < 0 || startPageIndexInclusive > uint.MaxValue)
            return [];

        var startPageIndex = (uint)Math.Max(0L, startPageIndexInclusive);
        var endPageIndex = (uint)Math.Min(uint.MaxValue, endPageIndexInclusive);

        lock (_lock)
        {
            if (_pages.Count == 0)
                return [];

            var dirtyPageIndices = new List<uint>();
            var rangePageCount = (ulong)endPageIndex - startPageIndex + 1;
            if (rangePageCount <= (ulong)_pages.Count)
            {
                for (var pageIndex = startPageIndex;; pageIndex++)
                {
                    if (_pages.TryGetValue(pageIndex, out var entry) && entry.Dirty)
                        dirtyPageIndices.Add(pageIndex);
                    if (pageIndex == endPageIndex)
                        break;
                }

                return dirtyPageIndices;
            }

            foreach (var (pageIndex, entry) in _pages)
            {
                if (pageIndex < startPageIndex || pageIndex > endPageIndex || !entry.Dirty)
                    continue;
                dirtyPageIndices.Add(pageIndex);
            }

            if (dirtyPageIndices.Count > 1)
                dirtyPageIndices.Sort();

            return dirtyPageIndices;
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
        ReleasePageOwnership(pageIndex, page.Value, true);
        return true;
    }

    public int RemovePagesInRange(uint startPageIndex, uint endPageIndex,
        Func<ResidentPageRecord, bool>? predicate = null)
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
            if (existing.Ptr != pageRecord.Ptr) return false;
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

        if (pageRecord.MappingReleaseOwner != null && pageRecord.MappingReleaseRecord != null)
            pageRecord.MappingReleaseOwner.ReleaseInstalledMappingPage(pageRecord.MappingReleaseRecord);

        _memoryContext.HostPages.UnbindOwnerRoot(pageRecord.Ptr, _ownerBindingFactory(pageIndex));
        _memoryContext.HostPages.TryRemoveIfUnused(pageRecord.Ptr);
    }

    private static ResidentPageRecord CreateVmPage(HostPageRef hostPage, MappingBackedInode? releaseOwner = null,
        InodePageRecord? releaseRecord = null)
    {
        return new ResidentPageRecord
        {
            Ptr = hostPage.Ptr,
            HostPage = hostPage,
            MappingReleaseOwner = releaseOwner,
            MappingReleaseRecord = releaseRecord
        };
    }

    private static void Touch(ResidentPageRecord pageRecord)
    {
        Touch(pageRecord, MonotonicTime.GetTimestamp());
    }

    private static void Touch(ResidentPageRecord pageRecord, long accessTimestamp)
    {
        var hostPage = pageRecord.HostPage;
        hostPage.LastAccessTimestamp = accessTimestamp;
    }

    private HostPageRef EnsureHostPageRegistered(IntPtr ptr, HostPageKind hostPageKind,
        ref BackingPageHandle backingPageHandle)
    {
        if (ptr == IntPtr.Zero)
            throw new ArgumentException("Host page pointer must be non-zero.", nameof(ptr));

        if (!backingPageHandle.IsValid)
            return _memoryContext.HostPages.GetOrCreate(ptr, hostPageKind);

        if (backingPageHandle.Pointer != ptr)
            throw new InvalidOperationException("Backing handle pointer does not match host page pointer.");

        return _memoryContext.HostPages.CreateWithBacking(ref backingPageHandle, hostPageKind);
    }
}

internal readonly struct ResidentPageRecord
{
    public required IntPtr Ptr { get; init; }
    public required HostPageRef HostPage { get; init; }
    public MappingBackedInode? MappingReleaseOwner { get; init; }
    public InodePageRecord? MappingReleaseRecord { get; init; }

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
