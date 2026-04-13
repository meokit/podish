using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Memory;

public enum AddressSpaceKind
{
    File,
    Shmem,
    Zero
}

public sealed class AddressSpace
{
    private readonly MemoryRuntimeContext _memoryContext;
    private readonly HostPageKind _pageKind;
    private readonly OwnerRmapTracker _ownerRmap = new();
    private readonly List<RmapAttachment> _rmapAttachments = [];
    private readonly Lock _rmapLock = new();
    private Action<int>? _trackedPageCountDelta;
    private int _refCount = 1;

    public AddressSpace(MemoryRuntimeContext memoryContext, AddressSpaceKind kind)
    {
        _memoryContext = memoryContext;
        Kind = kind;
        _pageKind = kind switch
        {
            AddressSpaceKind.Zero => HostPageKind.Zero,
            _ => HostPageKind.PageCache
        };
        Pages = new OwnerPageSlots(memoryContext, CreateOwnerBinding, OnPageBindingChanged, OnPageCountChanged);
    }

    public AddressSpaceKind Kind { get; }
    internal MemoryRuntimeContext MemoryContext => _memoryContext;
    internal OwnerPageSlots Pages { get; }
    public bool IsRecoverableWithoutSwap => Kind == AddressSpaceKind.File;
    internal bool IsZeroBacking => Kind == AddressSpaceKind.Zero;
    public int PageCount => Pages.PageCount;

    private void OnPageCountChanged(int delta)
    {
        _trackedPageCountDelta?.Invoke(delta);
    }

    internal void SetTrackedPageCountDeltaCallback(Action<int>? callback)
    {
        _trackedPageCountDelta = callback;
    }

    public void AddRef()
    {
        Interlocked.Increment(ref _refCount);
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) > 0) return;
        Pages.ReleaseAll();
        ClearRmapAttachments();
    }

    public IntPtr GetPage(uint pageIndex)
    {
        return Pages.GetPage(pageIndex);
    }

    public IntPtr PeekPage(uint pageIndex)
    {
        return Pages.PeekPage(pageIndex);
    }

    internal ResidentPageRecord? PeekVmPage(uint pageIndex)
    {
        return Pages.PeekVmPage(pageIndex);
    }

    internal IntPtr InstallHostPageIfAbsent(uint pageIndex, IntPtr ptr, ref BackingPageHandle backingHandle,
        HostPageKind hostPageKind, MappingBackedInode releaseOwner, InodePageRecord releaseRecord, out bool inserted)
    {
        return Pages.InstallHostPageIfAbsent(pageIndex, ptr, ref backingHandle, hostPageKind, releaseOwner,
            releaseRecord, out inserted);
    }

    public IntPtr GetOrCreatePage(uint pageIndex, Func<IntPtr, bool>? onFirstCreate, out bool isNew)
    {
        return Pages.GetOrCreatePage(pageIndex, onFirstCreate, out isNew, false, AllocationClass.PageCache,
            _pageKind);
    }

    public IntPtr GetOrCreatePage(uint pageIndex, Func<IntPtr, bool>? onFirstCreate, out bool isNew,
        bool strictQuota, AllocationClass allocationClass,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        return Pages.GetOrCreatePage(pageIndex, onFirstCreate, out isNew, strictQuota, allocationClass,
            _pageKind, allocationSource);
    }

    public void MarkDirty(uint pageIndex)
    {
        Pages.MarkDirty(pageIndex);
    }

    public bool IsDirty(uint pageIndex)
    {
        return Pages.IsDirty(pageIndex);
    }

    public void ClearDirty(uint pageIndex)
    {
        Pages.ClearDirty(pageIndex);
    }

    public void TruncateToSize(long size)
    {
        Pages.TruncateToSize(size);
    }

    public IReadOnlyList<VmPageState> SnapshotPageStates()
    {
        return Pages.SnapshotPageStates();
    }

    internal void GetPageStats(out int totalPages, out int dirtyPages)
    {
        Pages.GetPageStats(out totalPages, out dirtyPages);
    }

    public bool TryEvictCleanPage(uint pageIndex)
    {
        return Pages.TryEvictCleanPage(pageIndex);
    }

    internal int RemovePagesInRange(uint startPageIndex, uint endPageIndex, Func<ResidentPageRecord, bool>? predicate = null)
    {
        return Pages.RemovePagesInRange(startPageIndex, endPageIndex, predicate);
    }

    internal void AddRmapAttachment(VMAManager mm, VmArea vma, uint startPageIndex, uint endPageIndexExclusive,
        TbCohWorkSet? tbCohWorkSet = null)
    {
        if (startPageIndex >= endPageIndexExclusive)
            return;

        var attachment = new RmapAttachment
        {
            Mm = mm,
            Vma = vma,
            StartPageIndex = startPageIndex,
            EndPageIndexExclusive = endPageIndexExclusive
        };

        lock (_rmapLock)
        {
            _rmapAttachments.Add(attachment);
            SyncResidentPagesForAttachmentLocked(attachment, true, tbCohWorkSet);
        }
    }

    internal void RemoveRmapAttachments(VmArea vma, TbCohWorkSet? tbCohWorkSet = null)
    {
        lock (_rmapLock)
        {
            for (var i = _rmapAttachments.Count - 1; i >= 0; i--)
            {
                var attachment = _rmapAttachments[i];
                if (!ReferenceEquals(attachment.Vma, vma))
                    continue;

                SyncResidentPagesForAttachmentLocked(attachment, false, tbCohWorkSet);
                _rmapAttachments.RemoveAt(i);
            }
        }
    }

    internal void ResetRmapAttachmentsForSplit(VMAManager mm, VmArea retainedVma, VmArea? extraVma0 = null,
        VmArea? extraVma1 = null)
    {
        lock (_rmapLock)
        {
            RemoveAttachmentLocked(retainedVma);
            RemoveAttachmentLocked(extraVma0);
            RemoveAttachmentLocked(extraVma1);
            AddAttachmentLocked(mm, retainedVma);
            AddAttachmentLocked(mm, extraVma0);
            AddAttachmentLocked(mm, extraVma1);
        }
    }

    private void RemoveAttachmentLocked(VmArea? vma)
    {
        if (vma == null)
            return;

        for (var i = _rmapAttachments.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_rmapAttachments[i].Vma, vma))
                _rmapAttachments.RemoveAt(i);
    }

    private void AddAttachmentLocked(VMAManager mm, VmArea? vma)
    {
        if (vma == null || vma.Length == 0 || !ReferenceEquals(vma.VmMapping, this))
            return;

        var startPageIndex = vma.GetPageIndex(vma.Start);
        _rmapAttachments.Add(new RmapAttachment
        {
            Mm = mm,
            Vma = vma,
            StartPageIndex = startPageIndex,
            EndPageIndexExclusive = startPageIndex + vma.Length / LinuxConstants.PageSize
        });
    }

    internal void AddOrUpdateOwnerRmap(uint pageIndex, IntPtr hostPagePtr, HostPageRmapRef entry,
        out HostPageRmapRef previous, out bool existed)
    {
        lock (_rmapLock)
            _ownerRmap.AddOrUpdate(pageIndex, hostPagePtr, entry, out previous, out existed);
    }

    internal bool RemoveOwnerRmap(uint pageIndex, IntPtr hostPagePtr, HostPageRmapKey key, out HostPageRmapRef removed)
    {
        lock (_rmapLock)
            return _ownerRmap.TryRemove(pageIndex, hostPagePtr, key, out removed);
    }

    internal bool ContainsOwnerRmap(uint pageIndex, IntPtr hostPagePtr, HostPageRmapKey key)
    {
        lock (_rmapLock)
            return _ownerRmap.Contains(pageIndex, hostPagePtr, key);
    }

    internal bool RebindOwnerRmap(uint pageIndex, IntPtr hostPagePtr, HostPageRmapKey oldKey,
        HostPageRmapRef newEntry, out HostPageRmapRef previous)
    {
        lock (_rmapLock)
            return _ownerRmap.TryRebind(pageIndex, hostPagePtr, oldKey, newEntry, out previous);
    }

    internal void CollectOwnerRmapHits(uint pageIndex, IntPtr hostPagePtr, HostPageRef hostPageRef, List<RmapHit> output)
    {
        lock (_rmapLock)
            _ownerRmap.CollectHits(pageIndex, hostPagePtr, hostPageRef, output);
    }

    internal bool VisitOwnerRmapRefs<TState>(uint pageIndex, IntPtr hostPagePtr, HostPageRef hostPageRef, ref TState state,
        HostPageRmapVisitor<TState> visitor)
    {
        lock (_rmapLock)
            return _ownerRmap.Visit(pageIndex, hostPagePtr, hostPageRef, ref state, visitor);
    }

    private HostPageOwnerBinding CreateOwnerBinding(uint pageIndex)
    {
        return new HostPageOwnerBinding
        {
            OwnerKind = HostPageOwnerKind.AddressSpace,
            Mapping = this,
            PageIndex = pageIndex
        };
    }

    private void OnPageBindingChanged(uint pageIndex, IntPtr oldHostPagePtr, IntPtr newHostPagePtr)
    {
        lock (_rmapLock)
        {
            foreach (var attachment in _rmapAttachments)
            {
                if (pageIndex < attachment.StartPageIndex || pageIndex >= attachment.EndPageIndexExclusive)
                    continue;

                if (oldHostPagePtr != IntPtr.Zero)
                    RemoveDirectRefLocked(attachment, pageIndex, oldHostPagePtr);
                if (newHostPagePtr != IntPtr.Zero)
                    AddDirectRefLocked(attachment, pageIndex, newHostPagePtr);
            }
        }
    }

    private void SyncResidentPagesForAttachmentLocked(RmapAttachment attachment, bool add, TbCohWorkSet? tbCohWorkSet)
    {
        Pages.VisitResidentPagesInRange(attachment.StartPageIndex, attachment.EndPageIndexExclusive,
            (pageIndex, page) =>
            {
                if (add)
                    AddDirectRefLocked(attachment, pageIndex, page.Ptr, tbCohWorkSet);
                else
                    RemoveDirectRefLocked(attachment, pageIndex, page.Ptr, tbCohWorkSet);
            });
    }

    private void AddDirectRefLocked(RmapAttachment attachment, uint pageIndex, IntPtr hostPagePtr,
        TbCohWorkSet? tbCohWorkSet = null)
    {
        if (!ReferenceEquals(attachment.Vma.VmMapping, this))
            return;
        if ((attachment.Vma.Flags & MapFlags.Private) != 0 &&
            (attachment.Vma.VmAnonVma?.PeekPage(pageIndex) ?? IntPtr.Zero) != IntPtr.Zero)
            return;

        var changed = _memoryContext.HostPages.AddOrUpdateRmapRef(hostPagePtr, _pageKind, attachment.Mm, attachment.Vma,
            HostPageOwnerKind.AddressSpace, pageIndex, attachment.Vma.GetGuestPageStart(pageIndex));
        tbCohWorkSet?.AddIfChanged(hostPagePtr, changed);
    }

    private void RemoveDirectRefLocked(RmapAttachment attachment, uint pageIndex, IntPtr hostPagePtr,
        TbCohWorkSet? tbCohWorkSet = null)
    {
        var changed = _memoryContext.HostPages.RemoveRmapRef(hostPagePtr, attachment.Mm, attachment.Vma,
            HostPageOwnerKind.AddressSpace, pageIndex);
        tbCohWorkSet?.AddIfChanged(hostPagePtr, changed);
    }

    private void ClearRmapAttachments()
    {
        lock (_rmapLock)
        {
            _rmapAttachments.Clear();
        }
    }
}

public sealed class AnonVma
{
    private readonly MemoryRuntimeContext _memoryContext;
    private readonly OwnerRmapTracker _ownerRmap = new();
    private readonly List<RmapAttachment> _rmapAttachments = [];
    private readonly Lock _rmapLock = new();
    private int _refCount = 1;

    public AnonVma(MemoryRuntimeContext memoryContext, AnonVma? parent = null)
    {
        _memoryContext = memoryContext;
        Parent = parent;
        Root = parent?.Root ?? this;
        Pages = new OwnerPageSlots(memoryContext, CreateOwnerBinding, OnPageBindingChanged);
    }

    internal AnonVma? Parent { get; }
    internal AnonVma Root { get; }
    internal OwnerPageSlots Pages { get; }
    public int PageCount => Pages.PageCount;

    public void AddRef()
    {
        Interlocked.Increment(ref _refCount);
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) > 0) return;
        Pages.ReleaseAll();
        ClearRmapAttachments();
    }

    public AnonVma CloneForFork()
    {
        var clone = new AnonVma(_memoryContext, this);
        Pages.VisitPageStates(state =>
        {
            var page = Pages.PeekVmPage(state.PageIndex)!.Value;
            clone.Pages.InstallExistingHostPage(state.PageIndex, page.Ptr, HostPageKind.Anon);
            var clonedPage = clone.Pages.PeekVmPage(state.PageIndex)!.Value;
            clonedPage.Dirty = page.Dirty;
            clonedPage.Uptodate = page.Uptodate;
            clonedPage.Writeback = page.Writeback;
        });

        return clone;
    }

    public IntPtr GetPage(uint pageIndex)
    {
        return Pages.GetPage(pageIndex);
    }

    public IntPtr PeekPage(uint pageIndex)
    {
        return Pages.PeekPage(pageIndex);
    }

    internal ResidentPageRecord? PeekVmPage(uint pageIndex)
    {
        return Pages.PeekVmPage(pageIndex);
    }

    public void SetPage(uint pageIndex, IntPtr ptr)
    {
        Pages.ReplacePage(pageIndex, ptr, HostPageKind.Anon);
    }

    public void SetPage(uint pageIndex, ref BackingPageHandle backingHandle)
    {
        Pages.ReplacePage(pageIndex, ref backingHandle, HostPageKind.Anon);
    }

    public void MarkDirty(uint pageIndex)
    {
        Pages.MarkDirty(pageIndex);
    }

    public IReadOnlyList<VmPageState> SnapshotPageStates()
    {
        return Pages.SnapshotPageStates();
    }

    public long CountPagesInRange(uint startPageIndex, uint endPageIndex)
    {
        return Pages.CountPagesInRange(startPageIndex, endPageIndex);
    }

    internal int RemovePagesInRange(uint startPageIndex, uint endPageIndex, Func<ResidentPageRecord, bool>? predicate = null)
    {
        return Pages.RemovePagesInRange(startPageIndex, endPageIndex, predicate);
    }

    internal bool RemovePageIfMatches(uint pageIndex, ResidentPageRecord pageRecord)
    {
        return Pages.RemovePageIfMatches(pageIndex, pageRecord);
    }

    public void TruncateToSize(long size)
    {
        Pages.TruncateToSize(size);
    }

    internal void AddRmapAttachment(VMAManager mm, VmArea vma, uint startPageIndex, uint endPageIndexExclusive,
        TbCohWorkSet? tbCohWorkSet = null)
    {
        if (startPageIndex >= endPageIndexExclusive)
            return;

        var attachment = new RmapAttachment
        {
            Mm = mm,
            Vma = vma,
            StartPageIndex = startPageIndex,
            EndPageIndexExclusive = endPageIndexExclusive
        };

        lock (_rmapLock)
        {
            _rmapAttachments.Add(attachment);
            SyncResidentPagesForAttachmentLocked(attachment, true, tbCohWorkSet);
        }
    }

    internal void RemoveRmapAttachments(VmArea vma, TbCohWorkSet? tbCohWorkSet = null)
    {
        lock (_rmapLock)
        {
            for (var i = _rmapAttachments.Count - 1; i >= 0; i--)
            {
                var attachment = _rmapAttachments[i];
                if (!ReferenceEquals(attachment.Vma, vma))
                    continue;

                SyncResidentPagesForAttachmentLocked(attachment, false, tbCohWorkSet);
                _rmapAttachments.RemoveAt(i);
            }
        }
    }

    internal void ResetRmapAttachmentsForSplit(VMAManager mm, VmArea retainedVma, VmArea? extraVma0 = null,
        VmArea? extraVma1 = null)
    {
        lock (_rmapLock)
        {
            RemoveAttachmentLocked(retainedVma);
            RemoveAttachmentLocked(extraVma0);
            RemoveAttachmentLocked(extraVma1);
            AddAttachmentLocked(mm, retainedVma);
            AddAttachmentLocked(mm, extraVma0);
            AddAttachmentLocked(mm, extraVma1);
        }
    }

    private void RemoveAttachmentLocked(VmArea? vma)
    {
        if (vma == null)
            return;

        for (var i = _rmapAttachments.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_rmapAttachments[i].Vma, vma))
                _rmapAttachments.RemoveAt(i);
    }

    private void AddAttachmentLocked(VMAManager mm, VmArea? vma)
    {
        if (vma == null || vma.Length == 0 || !ReferenceEquals(vma.VmAnonVma, this))
            return;

        var startPageIndex = vma.GetPageIndex(vma.Start);
        _rmapAttachments.Add(new RmapAttachment
        {
            Mm = mm,
            Vma = vma,
            StartPageIndex = startPageIndex,
            EndPageIndexExclusive = startPageIndex + vma.Length / LinuxConstants.PageSize
        });
    }

    internal void AddOrUpdateOwnerRmap(uint pageIndex, IntPtr hostPagePtr, HostPageRmapRef entry,
        out HostPageRmapRef previous, out bool existed)
    {
        lock (Root._rmapLock)
            Root._ownerRmap.AddOrUpdate(pageIndex, hostPagePtr, entry, out previous, out existed);
    }

    internal bool RemoveOwnerRmap(uint pageIndex, IntPtr hostPagePtr, HostPageRmapKey key, out HostPageRmapRef removed)
    {
        lock (Root._rmapLock)
            return Root._ownerRmap.TryRemove(pageIndex, hostPagePtr, key, out removed);
    }

    internal bool ContainsOwnerRmap(uint pageIndex, IntPtr hostPagePtr, HostPageRmapKey key)
    {
        lock (Root._rmapLock)
            return Root._ownerRmap.Contains(pageIndex, hostPagePtr, key);
    }

    internal bool RebindOwnerRmap(uint pageIndex, IntPtr hostPagePtr, HostPageRmapKey oldKey,
        HostPageRmapRef newEntry, out HostPageRmapRef previous)
    {
        lock (Root._rmapLock)
            return Root._ownerRmap.TryRebind(pageIndex, hostPagePtr, oldKey, newEntry, out previous);
    }

    internal void CollectOwnerRmapHits(uint pageIndex, IntPtr hostPagePtr, HostPageRef hostPageRef, List<RmapHit> output)
    {
        lock (Root._rmapLock)
            Root._ownerRmap.CollectHits(pageIndex, hostPagePtr, hostPageRef, output);
    }

    internal bool VisitOwnerRmapRefs<TState>(uint pageIndex, IntPtr hostPagePtr, HostPageRef hostPageRef, ref TState state,
        HostPageRmapVisitor<TState> visitor)
    {
        lock (Root._rmapLock)
            return Root._ownerRmap.Visit(pageIndex, hostPagePtr, hostPageRef, ref state, visitor);
    }

    private HostPageOwnerBinding CreateOwnerBinding(uint pageIndex)
    {
        return new HostPageOwnerBinding
        {
            OwnerKind = HostPageOwnerKind.AnonVma,
            AnonVmaRoot = Root,
            PageIndex = pageIndex
        };
    }

    private void OnPageBindingChanged(uint pageIndex, IntPtr oldHostPagePtr, IntPtr newHostPagePtr)
    {
        lock (_rmapLock)
        {
            foreach (var attachment in _rmapAttachments)
            {
                if (pageIndex < attachment.StartPageIndex || pageIndex >= attachment.EndPageIndexExclusive)
                    continue;

                if (oldHostPagePtr != IntPtr.Zero)
                    RemoveDirectRefLocked(attachment, pageIndex, oldHostPagePtr);

                if (newHostPagePtr != IntPtr.Zero)
                {
                    RemoveSharedMappingRefLocked(attachment, pageIndex);
                    AddDirectRefLocked(attachment, pageIndex, newHostPagePtr);
                }
                else
                {
                    RestoreSharedMappingRefLocked(attachment, pageIndex);
                }
            }
        }
    }

    private void SyncResidentPagesForAttachmentLocked(RmapAttachment attachment, bool add, TbCohWorkSet? tbCohWorkSet)
    {
        Pages.VisitResidentPagesInRange(attachment.StartPageIndex, attachment.EndPageIndexExclusive,
            (pageIndex, page) =>
            {
                if (add)
                    AddDirectRefLocked(attachment, pageIndex, page.Ptr, tbCohWorkSet);
                else
                    RemoveDirectRefLocked(attachment, pageIndex, page.Ptr, tbCohWorkSet);
            });
    }

    private void AddDirectRefLocked(RmapAttachment attachment, uint pageIndex, IntPtr hostPagePtr,
        TbCohWorkSet? tbCohWorkSet = null)
    {
        if (!ReferenceEquals(attachment.Vma.VmAnonVma, this))
            return;

        var changed = _memoryContext.HostPages.AddOrUpdateRmapRef(hostPagePtr, HostPageKind.Anon, attachment.Mm,
            attachment.Vma, HostPageOwnerKind.AnonVma, pageIndex, attachment.Vma.GetGuestPageStart(pageIndex));
        tbCohWorkSet?.AddIfChanged(hostPagePtr, changed);
    }

    private void RemoveDirectRefLocked(RmapAttachment attachment, uint pageIndex, IntPtr hostPagePtr,
        TbCohWorkSet? tbCohWorkSet = null)
    {
        var changed = _memoryContext.HostPages.RemoveRmapRef(hostPagePtr, attachment.Mm, attachment.Vma,
            HostPageOwnerKind.AnonVma, pageIndex);
        tbCohWorkSet?.AddIfChanged(hostPagePtr, changed);
    }

    private void RemoveSharedMappingRefLocked(RmapAttachment attachment, uint pageIndex,
        TbCohWorkSet? tbCohWorkSet = null)
    {
        var sharedHostPagePtr = attachment.Vma.VmMapping?.PeekPage(pageIndex) ?? IntPtr.Zero;
        if (sharedHostPagePtr == IntPtr.Zero)
            return;

        var changed = _memoryContext.HostPages.RemoveRmapRef(sharedHostPagePtr, attachment.Mm, attachment.Vma,
            HostPageOwnerKind.AddressSpace, pageIndex);
        tbCohWorkSet?.AddIfChanged(sharedHostPagePtr, changed);
    }

    private void RestoreSharedMappingRefLocked(RmapAttachment attachment, uint pageIndex,
        TbCohWorkSet? tbCohWorkSet = null)
    {
        if ((attachment.Vma.VmAnonVma?.PeekPage(pageIndex) ?? IntPtr.Zero) != IntPtr.Zero)
            return;

        var sharedHostPagePtr = attachment.Vma.VmMapping?.PeekPage(pageIndex) ?? IntPtr.Zero;
        if (sharedHostPagePtr == IntPtr.Zero)
            return;

        var preferredKind = attachment.Vma.VmMapping?.IsZeroBacking == true
            ? HostPageKind.Zero
            : HostPageKind.PageCache;
        var changed = _memoryContext.HostPages.AddOrUpdateRmapRef(sharedHostPagePtr, preferredKind, attachment.Mm,
            attachment.Vma, HostPageOwnerKind.AddressSpace, pageIndex, attachment.Vma.GetGuestPageStart(pageIndex));
        tbCohWorkSet?.AddIfChanged(sharedHostPagePtr, changed);
    }

    private void ClearRmapAttachments()
    {
        lock (_rmapLock)
        {
            _rmapAttachments.Clear();
        }
    }
}
