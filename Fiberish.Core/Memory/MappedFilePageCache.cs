namespace Fiberish.Memory;

/// <summary>
///     Unified file page cache facade that routes to a runtime-selected backend.
/// </summary>
internal sealed class MappedFilePageCache : IDisposable
{
    private readonly IFilePageBackend _backend;

    public MappedFilePageCache(string path)
        : this(path, HostMemoryMapGeometry.CreateCurrent())
    {
    }

    public MappedFilePageCache(string path, HostMemoryMapGeometry geometry)
    {
        _backend = FilePageBackendSelector.Create(path, geometry);
    }

    public void Dispose()
    {
        _backend.Dispose();
    }

    public void UpdatePath(string path)
    {
        _backend.UpdatePath(path);
    }

    public bool TryAcquirePageHandle(long filePageIndex, long fileSize, bool writable, out IPageHandle? handle)
    {
        return _backend.TryAcquirePageHandle(filePageIndex, fileSize, writable, out handle);
    }

    public bool TryFlushPage(long filePageIndex)
    {
        return _backend.TryFlushPage(filePageIndex);
    }

    public void Truncate(long size)
    {
        _backend.Truncate(size);
    }

    public FilePageBackendDiagnostics GetDiagnostics()
    {
        return _backend.GetDiagnostics();
    }
}
