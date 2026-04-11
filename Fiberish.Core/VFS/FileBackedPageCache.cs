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
    public PageHandle Handle;
    public required uint PageIndex { get; init; }
    public required IntPtr Ptr { get; init; }
    public required FilePageBackingKind BackingKind { get; init; }

    public HostPageKind HostPageKind => BackingKind == FilePageBackingKind.ZeroSharedPage
        ? HostPageKind.Zero
        : HostPageKind.PageCache;

    public void ReleaseOwnership()
    {
        PageHandle.Release(ref Handle);
        HostPageManager.TryRemoveIfUnused(Ptr);
    }
}
