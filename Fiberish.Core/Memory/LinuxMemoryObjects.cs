namespace Fiberish.Memory;

public enum AddressSpaceKind
{
    File,
    Shmem,
    Zero
}

public sealed class AddressSpace
{
    private int _refCount = 1;
    private readonly Lock _rmapLock = new();
    private readonly List<RmapAttachment> _rmapAttachments = [];
    private readonly HostPageKind _pageKind;

    public AddressSpace(AddressSpaceKind kind)
    {
        Kind = kind;
        _pageKind = kind switch
        {
            AddressSpaceKind.Zero => HostPageKind.Zero,
            _ => HostPageKind.PageCache
        };
        Pages = new VmPageSlots(pageIndex => new HostPageOwnerRef
        {
            OwnerKind = HostPageOwnerKind.AddressSpace,
            Mapping = this,
            PageIndex = pageIndex
        });
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
        ClearRmapAttachments();
        Pages.ReleaseAll();
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

        lock (_rmapLock)
        {
            _rmapAttachments.Add(new RmapAttachment
            {
                Mm = mm,
                Vma = vma,
                StartPageIndex = startPageIndex,
                EndPageIndexExclusive = endPageIndexExclusive
            });
        }
    }

    internal void RemoveRmapAttachments(VmArea vma)
    {
        lock (_rmapLock)
        {
            _rmapAttachments.RemoveAll(attachment => ReferenceEquals(attachment.Vma, vma));
        }
    }

    internal void CollectRmapHits(HostPage hostPage, uint pageIndex, List<RmapHit> output,
        HashSet<(VMAManager Mm, VmArea Vma, HostPageOwnerKind OwnerKind, uint PageIndex)> seen)
    {
        List<RmapAttachment> attachments;
        lock (_rmapLock)
        {
            attachments = _rmapAttachments.Where(attachment => attachment.Covers(pageIndex)).ToList();
        }

        foreach (var attachment in attachments)
        {
            if (!ReferenceEquals(attachment.Vma.VmMapping, this))
                continue;
            if ((attachment.Vma.Flags & MapFlags.Private) != 0 &&
                attachment.Vma.VmAnonVma?.PeekHostPage(pageIndex) != null)
                continue;
            if (PeekHostPage(pageIndex) != hostPage)
                continue;

            if (seen.Add((attachment.Mm, attachment.Vma, HostPageOwnerKind.AddressSpace, pageIndex)))
                output.Add(new RmapHit(hostPage, attachment.Mm, attachment.Vma, HostPageOwnerKind.AddressSpace,
                    pageIndex));
        }
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
    private int _refCount = 1;
    private readonly Lock _rmapLock = new();
    private readonly List<RmapAttachment> _rmapAttachments = [];

    public AnonVma()
    {
        Pages = new VmPageSlots(pageIndex => new HostPageOwnerRef
        {
            OwnerKind = HostPageOwnerKind.AnonVma,
            AnonVma = this,
            PageIndex = pageIndex
        });
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
        ClearRmapAttachments();
        Pages.ReleaseAll();
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

        lock (_rmapLock)
        {
            _rmapAttachments.Add(new RmapAttachment
            {
                Mm = mm,
                Vma = vma,
                StartPageIndex = startPageIndex,
                EndPageIndexExclusive = endPageIndexExclusive
            });
        }
    }

    internal void RemoveRmapAttachments(VmArea vma)
    {
        lock (_rmapLock)
        {
            _rmapAttachments.RemoveAll(attachment => ReferenceEquals(attachment.Vma, vma));
        }
    }

    internal void CollectRmapHits(HostPage hostPage, uint pageIndex, List<RmapHit> output,
        HashSet<(VMAManager Mm, VmArea Vma, HostPageOwnerKind OwnerKind, uint PageIndex)> seen)
    {
        List<RmapAttachment> attachments;
        lock (_rmapLock)
        {
            attachments = _rmapAttachments.Where(attachment => attachment.Covers(pageIndex)).ToList();
        }

        foreach (var attachment in attachments)
        {
            if (!ReferenceEquals(attachment.Vma.VmAnonVma, this))
                continue;
            if (!ReferenceEquals(PeekHostPage(pageIndex), hostPage))
                continue;

            if (seen.Add((attachment.Mm, attachment.Vma, HostPageOwnerKind.AnonVma, pageIndex)))
                output.Add(new RmapHit(hostPage, attachment.Mm, attachment.Vma, HostPageOwnerKind.AnonVma,
                    pageIndex));
        }
    }

    private void ClearRmapAttachments()
    {
        lock (_rmapLock)
        {
            _rmapAttachments.Clear();
        }
    }
}
