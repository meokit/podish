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

public enum PageWritebackMode
{
    WritebackOnly,
    Durable
}

internal readonly record struct PageSyncRequest(uint PageIndex, long FileOffset, int Length,
    PageWritebackMode Mode = PageWritebackMode.Durable);

internal sealed class InodePageRecord
{
    public BackingPageHandle Handle;
    public required uint PageIndex { get; init; }
    public required IntPtr Ptr { get; init; }
    public required FilePageBackingKind BackingKind { get; init; }
    public bool IsWritable { get; init; } = true;

    public HostPageKind HostPageKind => BackingKind == FilePageBackingKind.ZeroSharedPage
        ? HostPageKind.Zero
        : HostPageKind.PageCache;

    public void ReleaseOwnership()
    {
        BackingPageHandle.Release(ref Handle);
    }
}
