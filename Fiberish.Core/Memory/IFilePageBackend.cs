namespace Fiberish.Memory;

internal readonly record struct FilePageBackendDiagnostics(
    int WindowCount,
    long WindowBytes,
    int GuestPageCount);

internal interface IFilePageBackend : IDisposable
{
    void UpdatePath(string path);

    bool TryAcquirePageLease(long filePageIndex, long fileSize, bool writable, out IntPtr pointer,
        out long releaseToken);

    void ReleasePageLease(long releaseToken);
    bool TryFlushPage(long filePageIndex);
    void Truncate(long size);
    long Trim(bool aggressive);
    FilePageBackendDiagnostics GetDiagnostics();
}