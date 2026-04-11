using Fiberish.VFS;

namespace Fiberish.Memory;

internal enum BackingPageHandleReleaseKind
{
    None,
    PooledPage,
    OwnedReleaseToken,
}

/// <summary>
///     Ownership record for an externally backed page pointer.
///     This is a value type and must be released through <see cref="Release(ref BackingPageHandle)" /> on the owner slot.
/// </summary>
public struct BackingPageHandle
{
    public IntPtr Pointer;
    internal long ReleaseToken;
    internal int PooledPageIndex;
    internal BackingPageHandleReleaseKind ReleaseKind;
    internal AllocationClass AllocationClass;
    internal AllocationSource AllocationSource;
    internal bool CountsTowardAnonymousAllocationTotals;
    internal Inode? ReleaseOwner;

    public readonly bool IsValid => Pointer != IntPtr.Zero;

    public static BackingPageHandle CreateOwned(IntPtr pointer, Inode releaseOwner, long releaseToken)
    {
        ArgumentNullException.ThrowIfNull(releaseOwner);
        return pointer == IntPtr.Zero
            ? default
            : new BackingPageHandle
            {
                Pointer = pointer,
                ReleaseKind = BackingPageHandleReleaseKind.OwnedReleaseToken,
                ReleaseOwner = releaseOwner,
                ReleaseToken = releaseToken
            };
    }

    internal static BackingPageHandle CreatePooled(IntPtr pointer, long segmentId, int pageIndex,
        AllocationClass allocationClass, AllocationSource allocationSource, bool countsTowardAnonymousAllocationTotals)
    {
        return pointer == IntPtr.Zero
            ? default
            : new BackingPageHandle
            {
                Pointer = pointer,
                ReleaseKind = BackingPageHandleReleaseKind.PooledPage,
                ReleaseToken = segmentId,
                PooledPageIndex = pageIndex,
                AllocationClass = allocationClass,
                AllocationSource = allocationSource,
                CountsTowardAnonymousAllocationTotals = countsTowardAnonymousAllocationTotals
            };
    }

    public static void Release(ref BackingPageHandle handle)
    {
        var releasedPtr = Interlocked.Exchange(ref handle.Pointer, IntPtr.Zero);
        if (releasedPtr == IntPtr.Zero)
            return;

        var releaseKind = handle.ReleaseKind;
        var releaseOwner = handle.ReleaseOwner;
        var releaseToken = handle.ReleaseToken;
        var pooledPageIndex = handle.PooledPageIndex;
        var allocationClass = handle.AllocationClass;
        var allocationSource = handle.AllocationSource;
        var countsTowardAnonymousAllocationTotals = handle.CountsTowardAnonymousAllocationTotals;
        handle.ReleaseKind = BackingPageHandleReleaseKind.None;
        handle.ReleaseOwner = null;
        handle.ReleaseToken = 0;
        handle.PooledPageIndex = 0;
        handle.AllocationClass = default;
        handle.AllocationSource = default;
        handle.CountsTowardAnonymousAllocationTotals = false;

        if (releaseKind == BackingPageHandleReleaseKind.OwnedReleaseToken)
        {
            if (releaseOwner == null)
                throw new InvalidOperationException("Owned backing page handle is missing its release owner.");

            releaseOwner.ReleaseMappedPageHandle(releaseToken);
            return;
        }

        if (releaseKind == BackingPageHandleReleaseKind.PooledPage)
        {
            PageManager.ReleasePooledPage(releasedPtr, releaseToken, pooledPageIndex, allocationClass,
                allocationSource, countsTowardAnonymousAllocationTotals);
        }
    }
}
