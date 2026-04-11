using Fiberish.VFS;

namespace Fiberish.Memory;

internal interface IBackingPageHandleReleaseOwner
{
    void ReleaseBackingPageHandle(IntPtr pointer, long releaseToken);
}

/// <summary>
///     Ownership record for an externally backed page pointer.
///     This is a value type and must be released through <see cref="Release(ref BackingPageHandle)" /> on the owner slot.
/// </summary>
public struct BackingPageHandle
{
    public IntPtr Pointer;
    private IBackingPageHandleReleaseOwner? _releaseOwner;
    private long _releaseToken;

    public readonly bool IsValid => Pointer != IntPtr.Zero;

    public static BackingPageHandle CreateOwned(IntPtr pointer, Inode releaseOwner, long releaseToken)
    {
        ArgumentNullException.ThrowIfNull(releaseOwner);
        return pointer == IntPtr.Zero
            ? default
            : new BackingPageHandle
            {
                Pointer = pointer,
                _releaseOwner = releaseOwner,
                _releaseToken = releaseToken
            };
    }

    internal static BackingPageHandle CreatePooled(BackingPagePool releaseOwner, IntPtr pointer, long segmentId, int pageIndex,
        AllocationClass allocationClass, AllocationSource allocationSource, bool countsTowardAnonymousAllocationTotals)
    {
        ArgumentNullException.ThrowIfNull(releaseOwner);
        return pointer == IntPtr.Zero
            ? default
            : new BackingPageHandle
            {
                Pointer = pointer,
                _releaseOwner = releaseOwner,
                _releaseToken = BackingPagePool.CreatePooledReleaseToken(
                    segmentId,
                    pageIndex,
                    allocationClass,
                    allocationSource,
                    countsTowardAnonymousAllocationTotals)
            };
    }

    public static void Release(ref BackingPageHandle handle)
    {
        var releasedPtr = Interlocked.Exchange(ref handle.Pointer, IntPtr.Zero);
        if (releasedPtr == IntPtr.Zero)
            return;

        var releaseOwner = handle._releaseOwner;
        var releaseToken = handle._releaseToken;
        handle._releaseOwner = null;
        handle._releaseToken = 0;

        if (releaseOwner == null)
            throw new InvalidOperationException("Backing page handle is missing its release owner.");

        releaseOwner.ReleaseBackingPageHandle(releasedPtr, releaseToken);
    }
}
