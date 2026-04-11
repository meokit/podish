namespace Fiberish.Memory;

public readonly record struct HostPageKindRefStats(
    long PageCount,
    long OwnerResidentRefCount,
    long MapRefCount,
    long PinRefCount,
    long TotalRefCount,
    long ZeroRefPageCount,
    long DirtyPageCount,
    long WritebackPageCount,
    long OwnerRootPageCount,
    long BackedPageCount,
    int MaxOwnerResidentRefCount,
    int MaxMapRefCount,
    int MaxPinRefCount);

public readonly record struct HostPageRefStatsSnapshot(
    HostPageKindRefStats PageCache,
    HostPageKindRefStats Anon,
    HostPageKindRefStats Zero);

internal struct HostPageKindRefStatsAccumulator
{
    public long PageCount;
    public long OwnerResidentRefCount;
    public long MapRefCount;
    public long PinRefCount;
    public long TotalRefCount;
    public long ZeroRefPageCount;
    public long DirtyPageCount;
    public long WritebackPageCount;
    public long OwnerRootPageCount;
    public long BackedPageCount;
    public int MaxOwnerResidentRefCount;
    public int MaxMapRefCount;
    public int MaxPinRefCount;

    public void Include(in HostPageData page)
    {
        PageCount++;
        OwnerResidentRefCount += page.OwnerResidentCount;
        MapRefCount += page.MapCount;
        PinRefCount += page.PinCount;
        TotalRefCount += page.OwnerResidentCount + page.MapCount + page.PinCount;
        if (page.OwnerResidentCount == 0 && page.MapCount == 0 && page.PinCount == 0)
            ZeroRefPageCount++;
        if (page.Dirty)
            DirtyPageCount++;
        if (page.Writeback)
            WritebackPageCount++;
        if (page.HasOwnerRoot)
            OwnerRootPageCount++;
        if (page.BackingHandle.IsValid)
            BackedPageCount++;
        MaxOwnerResidentRefCount = Math.Max(MaxOwnerResidentRefCount, page.OwnerResidentCount);
        MaxMapRefCount = Math.Max(MaxMapRefCount, page.MapCount);
        MaxPinRefCount = Math.Max(MaxPinRefCount, page.PinCount);
    }

    public readonly HostPageKindRefStats ToSnapshot()
    {
        return new HostPageKindRefStats(
            PageCount,
            OwnerResidentRefCount,
            MapRefCount,
            PinRefCount,
            TotalRefCount,
            ZeroRefPageCount,
            DirtyPageCount,
            WritebackPageCount,
            OwnerRootPageCount,
            BackedPageCount,
            MaxOwnerResidentRefCount,
            MaxMapRefCount,
            MaxPinRefCount);
    }
}
