namespace Fiberish.Memory;

internal readonly record struct FilePageBackendDiagnostics(
    int WindowCount,
    long WindowBytes,
    int GuestPageCount);

// Read-only provenance for a currently leased pointer; not a residency or RAM-lock claim.
internal readonly record struct FilePageLeaseDiagnostics(
    long WindowId, long FilePageIndex, IntPtr WindowPointer, long WindowLength,
    bool Writable, bool Retired, int WindowRefCount);

internal interface IFilePageBackend : IDisposable
{
    void UpdatePath(string path);

    int TryAcquirePageLeases(long startFilePageIndex, int maxPageCount, long fileSize, bool writable,
        Span<IntPtr> pointers, Span<long> releaseTokens);

    bool TryAcquirePageLease(long filePageIndex, long fileSize, bool writable, out IntPtr pointer,
        out long releaseToken);

    void ReleasePageLease(long releaseToken);
    bool TryFlushPage(long filePageIndex);
    bool TryFlushAllActiveWritableWindows();
    void Truncate(long size);
    long Trim(bool aggressive);
    FilePageBackendDiagnostics GetDiagnostics();
    bool TryDescribePagePointer(long filePageIndex, IntPtr pointer, out FilePageLeaseDiagnostics diagnostics);
}
