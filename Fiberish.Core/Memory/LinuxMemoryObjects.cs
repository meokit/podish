namespace Fiberish.Memory;

public enum AddressSpaceKind
{
    File,
    Shmem
}

public sealed class AddressSpace
{
    private int _refCount = 1;

    public AddressSpace(AddressSpaceKind kind)
    {
        Kind = kind;
    }

    public AddressSpaceKind Kind { get; }
    public VmPageSlots Pages { get; } = new();
    public bool IsRecoverableWithoutSwap => Kind == AddressSpaceKind.File;
    public int PageCount => Pages.PageCount;

    public void AddRef()
    {
        Interlocked.Increment(ref _refCount);
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) > 0) return;
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

    public VmPage? PeekVmPage(uint pageIndex)
    {
        return Pages.PeekVmPage(pageIndex);
    }

    public IntPtr SetPageIfAbsent(uint pageIndex, IntPtr ptr, out bool inserted)
    {
        return Pages.InstallPageIfAbsent(pageIndex, ptr, out inserted);
    }

    public IntPtr GetOrCreatePage(uint pageIndex, Func<IntPtr, bool>? onFirstCreate, out bool isNew)
    {
        return Pages.GetOrCreatePage(pageIndex, onFirstCreate, out isNew);
    }

    public IntPtr GetOrCreatePage(uint pageIndex, Func<IntPtr, bool>? onFirstCreate, out bool isNew,
        bool strictQuota, AllocationClass allocationClass,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        return Pages.GetOrCreatePage(pageIndex, onFirstCreate, out isNew, strictQuota, allocationClass,
            allocationSource);
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

    public int RemovePagesInRange(uint startPageIndex, uint endPageIndex, Func<VmPage, bool>? predicate = null)
    {
        return Pages.RemovePagesInRange(startPageIndex, endPageIndex, predicate);
    }
}

public sealed class AnonVma
{
    private int _refCount = 1;

    public VmPageSlots Pages { get; } = new();
    public int PageCount => Pages.PageCount;

    public void AddRef()
    {
        Interlocked.Increment(ref _refCount);
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) > 0) return;
        Pages.ReleaseAll();
    }

    public AnonVma CloneForFork()
    {
        var clone = new AnonVma();
        Pages.VisitPageStates(state =>
        {
            var page = Pages.PeekVmPage(state.PageIndex)!;
            ExternalPageManager.AddRef(page.Ptr);
            clone.Pages.InstallPage(state.PageIndex, page.Ptr);
            var clonedPage = clone.Pages.PeekVmPage(state.PageIndex)!;
            clonedPage.Dirty = page.Dirty;
            clonedPage.Uptodate = page.Uptodate;
            clonedPage.Writeback = page.Writeback;
            clonedPage.PinCount = page.PinCount;
            clonedPage.MapCount = page.MapCount;
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

    public VmPage? PeekVmPage(uint pageIndex)
    {
        return Pages.PeekVmPage(pageIndex);
    }

    public void SetPage(uint pageIndex, IntPtr ptr)
    {
        Pages.InstallPage(pageIndex, ptr);
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

    public int RemovePagesInRange(uint startPageIndex, uint endPageIndex, Func<VmPage, bool>? predicate = null)
    {
        return Pages.RemovePagesInRange(startPageIndex, endPageIndex, predicate);
    }

    public bool RemovePageIfMatches(uint pageIndex, VmPage page)
    {
        return Pages.RemovePageIfMatches(pageIndex, page);
    }

    public void TruncateToSize(long size)
    {
        Pages.TruncateToSize(size);
    }
}
