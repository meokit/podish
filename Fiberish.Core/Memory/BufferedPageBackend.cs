namespace Fiberish.Memory;

/// <summary>
///     No-op backend used when host memory-mapped files are unavailable (for example Wasm).
/// </summary>
internal sealed class BufferedPageBackend : IFilePageBackend
{
    public void UpdatePath(string path)
    {
    }

    public bool TryAcquirePageHandle(long filePageIndex, long fileSize, bool writable, out IPageHandle? handle)
    {
        handle = null;
        return false;
    }

    public bool TryFlushPage(long filePageIndex)
    {
        return false;
    }

    public void Truncate(long size)
    {
    }

    public long Trim(bool aggressive)
    {
        return 0;
    }

    public FilePageBackendDiagnostics GetDiagnostics()
    {
        return default;
    }

    public void Dispose()
    {
    }
}