namespace Fiberish.Memory;

internal readonly record struct FilePageBackendDiagnostics(
    int WindowCount,
    long WindowBytes,
    int GuestPageCount);

internal interface IFilePageBackend : IDisposable
{
    void UpdatePath(string path);
    bool TryAcquirePageHandle(long filePageIndex, long fileSize, bool writable, out IPageHandle? handle);
    bool TryFlushPage(long filePageIndex);
    void Truncate(long size);
    long Trim(bool aggressive);
    FilePageBackendDiagnostics GetDiagnostics();
}
