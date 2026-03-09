namespace Fiberish.Memory;

internal interface IFilePageBackend : IDisposable
{
    void UpdatePath(string path);
    bool TryAcquirePageHandle(long filePageIndex, long fileSize, out IPageHandle? handle);
    void Truncate(long size);
}
