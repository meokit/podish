namespace Fiberish.Memory;

/// <summary>
/// Unified file page cache facade that routes to a runtime-selected backend.
/// </summary>
internal sealed class MappedFilePageCache : IDisposable
{
    private readonly IFilePageBackend _backend;

    public MappedFilePageCache(string path, bool writable)
    {
        _backend = FilePageBackendSelector.Create(path, writable);
    }

    public void UpdatePath(string path)
    {
        _backend.UpdatePath(path);
    }

    public bool TryAcquirePageHandle(long filePageIndex, long fileSize, out IPageHandle? handle)
    {
        return _backend.TryAcquirePageHandle(filePageIndex, fileSize, out handle);
    }

    public void Truncate(long size)
    {
        _backend.Truncate(size);
    }

    public void Dispose()
    {
        _backend.Dispose();
    }
}
