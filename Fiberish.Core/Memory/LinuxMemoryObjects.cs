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
    private int _refCount = 1;

    public AddressSpace(AddressSpaceKind kind)
    {
        Kind = kind;
        _pageKind = kind switch
        {
            AddressSpaceKind.Zero => HostPageKind.Zero,
            _ => HostPageKind.PageCache
        };
        Pages = new VmPageSlots(CreateOwnerRef, OnPageBindingChanged);
    }

    public AddressSpaceKind Kind { get; }
    internal VmPageSlots Pages { get; }
    public bool IsRecoverableWithoutSwap => Kind == AddressSpaceKind.File;
    internal bool IsZeroBacking => Kind == AddressSpaceKind.Zero;
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

    public IntPtr SetPageIfAbsent(uint pageIndex, IntPtr ptr, out bool inserted)
    {
        return Pages.InstallPageIfAbsent(pageIndex, ptr, _pageKind, out inserted);
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

    public void VisitPageStates(Action<VmPageState> visitor)
    {
        Pages.VisitPageStates(visitor);
    }

    public long CountPagesInRange(uint startPageIndex, uint endPageIndex)
    {
        return Pages.CountPagesInRange(startPageIndex, endPageIndex);
    }

    public bool TryEvictCleanPage(uint pageIndex)
    {
        return Pages.TryEvictCleanPage(pageIndex);
    }

    internal int RemovePagesInRange(uint startPageIndex, uint endPageIndex, Func<VmPage, bool>? predicate = null)
    {
        return Pages.RemovePagesInRange(startPageIndex, endPageIndex, predicate);
    }

    internal void AddRmapAttachment(VMAManager mm, VmArea vma, uint startPageIndex, uint endPageIndexExclusive)
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
            SyncResidentPagesForAttachmentLocked(attachment, true);
        }
    }

    internal void RemoveRmapAttachments(VmArea vma)
    {
        lock (_rmapLock)
        {
            for (var i = _rmapAttachments.Count - 1; i >= 0; i--)
            {
                var attachment = _rmapAttachments[i];
                if (!ReferenceEquals(attachment.Vma, vma))
                    continue;

                SyncResidentPagesForAttachmentLocked(attachment, false);
                _rmapAttachments.RemoveAt(i);
            }
        }
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

    private void SyncResidentPagesForAttachmentLocked(RmapAttachment attachment, bool add)
    {
        Pages.VisitResidentPagesInRange(attachment.StartPageIndex, attachment.EndPageIndexExclusive,
            (pageIndex, page) =>
            {
                if (add)
                    AddDirectRefLocked(attachment, pageIndex, page.HostPage);
                else
                    RemoveDirectRefLocked(attachment, pageIndex, page.HostPage);
            });
    }

    private void AddDirectRefLocked(RmapAttachment attachment, uint pageIndex, HostPage hostPage)
    {
        if (!ReferenceEquals(attachment.Vma.VmMapping, this))
            return;
        if ((attachment.Vma.Flags & MapFlags.Private) != 0 &&
            attachment.Vma.VmAnonVma?.PeekHostPage(pageIndex) != null)
            return;

        hostPage.AddOrUpdateRmapRef(attachment.Mm, attachment.Vma, HostPageOwnerKind.AddressSpace, pageIndex,
            attachment.Vma.GetGuestPageStart(pageIndex));
    }

    private static void RemoveDirectRefLocked(RmapAttachment attachment, uint pageIndex, HostPage hostPage)
    {
        hostPage.RemoveRmapRef(attachment.Mm, attachment.Vma, HostPageOwnerKind.AddressSpace, pageIndex);
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
            ExternalPageManager.AddRef(page.Ptr);
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

    public bool IsDirty(uint pageIndex)
    {
        return Pages.IsDirty(pageIndex);
    }

    public void ClearDirty(uint pageIndex)
    {
        Pages.ClearDirty(pageIndex);
    }

    public IReadOnlyList<VmPageState> SnapshotPageStates()
    {
        return Pages.SnapshotPageStates();
    }

    public void VisitPageStates(Action<VmPageState> visitor)
    {
        Pages.VisitPageStates(visitor);
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

    internal void AddRmapAttachment(VMAManager mm, VmArea vma, uint startPageIndex, uint endPageIndexExclusive)
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
            SyncResidentPagesForAttachmentLocked(attachment, true);
        }
    }

    internal void RemoveRmapAttachments(VmArea vma)
    {
        lock (_rmapLock)
        {
            for (var i = _rmapAttachments.Count - 1; i >= 0; i--)
            {
                var attachment = _rmapAttachments[i];
                if (!ReferenceEquals(attachment.Vma, vma))
                    continue;

                SyncResidentPagesForAttachmentLocked(attachment, false);
                _rmapAttachments.RemoveAt(i);
            }
        }
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

    private void SyncResidentPagesForAttachmentLocked(RmapAttachment attachment, bool add)
    {
        Pages.VisitResidentPagesInRange(attachment.StartPageIndex, attachment.EndPageIndexExclusive,
            (pageIndex, page) =>
            {
                if (add)
                    AddDirectRefLocked(attachment, pageIndex, page.HostPage);
                else
                    RemoveDirectRefLocked(attachment, pageIndex, page.HostPage);
            });
    }

    private void AddDirectRefLocked(RmapAttachment attachment, uint pageIndex, HostPage hostPage)
    {
        if (!ReferenceEquals(attachment.Vma.VmAnonVma, this))
            return;

        hostPage.AddOrUpdateRmapRef(attachment.Mm, attachment.Vma, HostPageOwnerKind.AnonVma, pageIndex,
            attachment.Vma.GetGuestPageStart(pageIndex));
    }

    private static void RemoveDirectRefLocked(RmapAttachment attachment, uint pageIndex, HostPage hostPage)
    {
        hostPage.RemoveRmapRef(attachment.Mm, attachment.Vma, HostPageOwnerKind.AnonVma, pageIndex);
    }

    private static void RemoveSharedMappingRefLocked(RmapAttachment attachment, uint pageIndex)
    {
        var sharedHostPage = attachment.Vma.VmMapping?.PeekHostPage(pageIndex);
        if (sharedHostPage == null)
            return;

        sharedHostPage.RemoveRmapRef(attachment.Mm, attachment.Vma, HostPageOwnerKind.AddressSpace, pageIndex);
    }

    private static void RestoreSharedMappingRefLocked(RmapAttachment attachment, uint pageIndex)
    {
        if (attachment.Vma.VmAnonVma?.PeekHostPage(pageIndex) != null)
            return;

        var sharedHostPage = attachment.Vma.VmMapping?.PeekHostPage(pageIndex);
        if (sharedHostPage == null)
            return;

        sharedHostPage.AddOrUpdateRmapRef(attachment.Mm, attachment.Vma, HostPageOwnerKind.AddressSpace, pageIndex,
            attachment.Vma.GetGuestPageStart(pageIndex));
    }

    private void ClearRmapAttachments()
    {
        lock (_rmapLock)
        {
            _rmapAttachments.Clear();
        }
    }
}