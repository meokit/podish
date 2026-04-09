using Fiberish.Memory;

namespace Fiberish.VFS;

internal enum FilePageBackingKind
{
    AllocatedPageCache,
    HostMappedWindow,
    ZeroSharedPage
}

internal enum PageCacheAccessMode
{
    Read,
    Write
}

internal readonly record struct PageSyncRequest(uint PageIndex, long FileOffset, int Length);

internal sealed class InodePageRecord
{
    public required uint PageIndex { get; init; }
    public required HostPage HostPage { get; init; }
    public required FilePageBackingKind BackingKind { get; init; }
    public IDisposable? ExternalOwner { get; init; }

    public IntPtr Ptr => HostPage.Ptr;

    public void ReleaseOwnership()
    {
        if (BackingKind != FilePageBackingKind.ZeroSharedPage)
            PageManager.ReleasePtr(Ptr);
    }
}
