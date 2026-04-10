using Fiberish.Native;

namespace Fiberish.Memory;

public enum AddressSpaceKind
{
    File,
    Shmem,
    Zero
}

public sealed class AddressSpace
{
    private readonly HostPageKind _pageKind;
    private readonly List<RmapAttachment> _rmapAttachments = [];
    private readonly Lock _rmapLock = new();
    private Action<int>? _trackedPageCountDelta;
    private int _refCount = 1;

    public AddressSpace(AddressSpaceKind kind)
    {
        Kind = kind;
        _pageKind = kind switch
        {
            AddressSpaceKind.Zero => HostPageKind.Zero,
            _ => HostPageKind.PageCache
        };
        Pages = new VmPageSlots(CreateOwnerRef, OnPageBindingChanged, OnPageCountChanged);
    }

    public AddressSpaceKind Kind { get; }
    internal VmPageSlots Pages { get; }
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

    internal HostPage? PeekHostPage(uint pageIndex)
    {
        return Pages.PeekHostPage(pageIndex);
    }

    internal VmPage? PeekVmPage(uint pageIndex)
    {
        return Pages.PeekVmPage(pageIndex);
    }

    internal IntPtr InstallHostPageIfAbsent(uint pageIndex, HostPage hostPage, Action<VmPage>? onReleased,
        out bool inserted)
    {
        return Pages.InstallHostPageIfAbsent(pageIndex, hostPage, onReleased, out inserted);
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

    internal int RemovePagesInRange(uint startPageIndex, uint endPageIndex, Func<VmPage, bool>? predicate = null)
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

    private HostPageOwnerRef CreateOwnerRef(uint pageIndex)
    {
        return new HostPageOwnerRef
        {
            OwnerKind = HostPageOwnerKind.AddressSpace,
            Mapping = this,
            PageIndex = pageIndex
        };
    }

    private void OnPageBindingChanged(uint pageIndex, HostPage? oldHostPage, HostPage? newHostPage)
    {
        lock (_rmapLock)
        {
            foreach (var attachment in _rmapAttachments)
            {
                if (pageIndex < attachment.StartPageIndex || pageIndex >= attachment.EndPageIndexExclusive)
                    continue;

                if (oldHostPage != null)
                    RemoveDirectRefLocked(attachment, pageIndex, oldHostPage);
                if (newHostPage != null)
                    AddDirectRefLocked(attachment, pageIndex, newHostPage);
            }
        }
    }

    private void SyncResidentPagesForAttachmentLocked(RmapAttachment attachment, bool add, TbCohWorkSet? tbCohWorkSet)
    {
        Pages.VisitResidentPagesInRange(attachment.StartPageIndex, attachment.EndPageIndexExclusive,
            (pageIndex, page) =>
            {
                if (add)
                    AddDirectRefLocked(attachment, pageIndex, page.HostPage, tbCohWorkSet);
                else
                    RemoveDirectRefLocked(attachment, pageIndex, page.HostPage, tbCohWorkSet);
            });
    }

    private void AddDirectRefLocked(RmapAttachment attachment, uint pageIndex, HostPage hostPage,
        TbCohWorkSet? tbCohWorkSet = null)
    {
        if (!ReferenceEquals(attachment.Vma.VmMapping, this))
            return;
        if ((attachment.Vma.Flags & MapFlags.Private) != 0 &&
            attachment.Vma.VmAnonVma?.PeekHostPage(pageIndex) != null)
            return;

        var changed = hostPage.AddOrUpdateRmapRef(attachment.Mm, attachment.Vma, HostPageOwnerKind.AddressSpace,
            pageIndex, attachment.Vma.GetGuestPageStart(pageIndex));
        tbCohWorkSet?.AddIfChanged(hostPage, changed);
    }

    private static void RemoveDirectRefLocked(RmapAttachment attachment, uint pageIndex, HostPage hostPage,
        TbCohWorkSet? tbCohWorkSet = null)
    {
        var changed = hostPage.RemoveRmapRef(attachment.Mm, attachment.Vma, HostPageOwnerKind.AddressSpace, pageIndex);
        tbCohWorkSet?.AddIfChanged(hostPage, changed);
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
    private readonly List<RmapAttachment> _rmapAttachments = [];
    private readonly Lock _rmapLock = new();
    private int _refCount = 1;

    public AnonVma()
    {
        Pages = new VmPageSlots(CreateOwnerRef, OnPageBindingChanged);
    }

    internal VmPageSlots Pages { get; }
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
        var clone = new AnonVma();
        Pages.VisitPageStates(state =>
        {
            var page = Pages.PeekVmPage(state.PageIndex)!;
            PageManager.AddRef(page.Ptr);
            clone.Pages.InstallExistingHostPage(state.PageIndex, page.HostPage);
            var clonedPage = clone.Pages.PeekVmPage(state.PageIndex)!;
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

    internal HostPage? PeekHostPage(uint pageIndex)
    {
        return Pages.PeekHostPage(pageIndex);
    }

    internal VmPage? PeekVmPage(uint pageIndex)
    {
        return Pages.PeekVmPage(pageIndex);
    }

    public void SetPage(uint pageIndex, IntPtr ptr)
    {
        Pages.ReplacePage(pageIndex, ptr, HostPageKind.Anon);
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

    internal int RemovePagesInRange(uint startPageIndex, uint endPageIndex, Func<VmPage, bool>? predicate = null)
    {
        return Pages.RemovePagesInRange(startPageIndex, endPageIndex, predicate);
    }

    internal bool RemovePageIfMatches(uint pageIndex, VmPage page)
    {
        return Pages.RemovePageIfMatches(pageIndex, page);
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

    private HostPageOwnerRef CreateOwnerRef(uint pageIndex)
    {
        return new HostPageOwnerRef
        {
            OwnerKind = HostPageOwnerKind.AnonVma,
            AnonVma = this,
            PageIndex = pageIndex
        };
    }

    private void OnPageBindingChanged(uint pageIndex, HostPage? oldHostPage, HostPage? newHostPage)
    {
        lock (_rmapLock)
        {
            foreach (var attachment in _rmapAttachments)
            {
                if (pageIndex < attachment.StartPageIndex || pageIndex >= attachment.EndPageIndexExclusive)
                    continue;

                if (oldHostPage != null)
                    RemoveDirectRefLocked(attachment, pageIndex, oldHostPage);

                if (newHostPage != null)
                {
                    RemoveSharedMappingRefLocked(attachment, pageIndex);
                    AddDirectRefLocked(attachment, pageIndex, newHostPage);
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
                    AddDirectRefLocked(attachment, pageIndex, page.HostPage, tbCohWorkSet);
                else
                    RemoveDirectRefLocked(attachment, pageIndex, page.HostPage, tbCohWorkSet);
            });
    }

    private void AddDirectRefLocked(RmapAttachment attachment, uint pageIndex, HostPage hostPage,
        TbCohWorkSet? tbCohWorkSet = null)
    {
        if (!ReferenceEquals(attachment.Vma.VmAnonVma, this))
            return;

        var changed = hostPage.AddOrUpdateRmapRef(attachment.Mm, attachment.Vma, HostPageOwnerKind.AnonVma,
            pageIndex, attachment.Vma.GetGuestPageStart(pageIndex));
        tbCohWorkSet?.AddIfChanged(hostPage, changed);
    }

    private static void RemoveDirectRefLocked(RmapAttachment attachment, uint pageIndex, HostPage hostPage,
        TbCohWorkSet? tbCohWorkSet = null)
    {
        var changed = hostPage.RemoveRmapRef(attachment.Mm, attachment.Vma, HostPageOwnerKind.AnonVma, pageIndex);
        tbCohWorkSet?.AddIfChanged(hostPage, changed);
    }

    private static void RemoveSharedMappingRefLocked(RmapAttachment attachment, uint pageIndex,
        TbCohWorkSet? tbCohWorkSet = null)
    {
        var sharedHostPage = attachment.Vma.VmMapping?.PeekHostPage(pageIndex);
        if (sharedHostPage == null)
            return;

        var changed =
            sharedHostPage.RemoveRmapRef(attachment.Mm, attachment.Vma, HostPageOwnerKind.AddressSpace, pageIndex);
        tbCohWorkSet?.AddIfChanged(sharedHostPage, changed);
    }

    private static void RestoreSharedMappingRefLocked(RmapAttachment attachment, uint pageIndex,
        TbCohWorkSet? tbCohWorkSet = null)
    {
        if (attachment.Vma.VmAnonVma?.PeekHostPage(pageIndex) != null)
            return;

        var sharedHostPage = attachment.Vma.VmMapping?.PeekHostPage(pageIndex);
        if (sharedHostPage == null)
            return;

        var changed = sharedHostPage.AddOrUpdateRmapRef(attachment.Mm, attachment.Vma,
            HostPageOwnerKind.AddressSpace, pageIndex, attachment.Vma.GetGuestPageStart(pageIndex));
        tbCohWorkSet?.AddIfChanged(sharedHostPage, changed);
    }

    private void ClearRmapAttachments()
    {
        lock (_rmapLock)
        {
            _rmapAttachments.Clear();
        }
    }
}
