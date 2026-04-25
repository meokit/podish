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

    public int TryAcquirePageLeases(long startFilePageIndex, int maxPageCount, long fileSize, bool writable,
        Span<IntPtr> pointers, Span<long> releaseTokens)
    {
        return _backend.TryAcquirePageLeases(startFilePageIndex, maxPageCount, fileSize, writable, pointers,
            releaseTokens);
    }

    public bool TryAcquirePageLease(long filePageIndex, long fileSize, bool writable, out IntPtr pointer,
        out long releaseToken)
    {
        return _backend.TryAcquirePageLease(filePageIndex, fileSize, writable, out pointer, out releaseToken);
    }

    public void ReleasePageLease(long releaseToken)
    {
        _backend.ReleasePageLease(releaseToken);
    }

    public bool TryFlushPage(long filePageIndex)
    {
        return _backend.TryFlushPage(filePageIndex);
    }

    public bool TryFlushAllActiveWritableWindows()
    {
        return _backend.TryFlushAllActiveWritableWindows();
    }

    public void Truncate(long size)
    {
        _backend.Truncate(size);
    }

    public long Trim(bool aggressive)
    {
        return _backend.Trim(aggressive);
    }

    public FilePageBackendDiagnostics GetDiagnostics()
    {
        return _backend.GetDiagnostics();
    }
}
