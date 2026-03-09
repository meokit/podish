namespace Fiberish.Memory;

/// <summary>
/// No-op backend used when host memory-mapped files are unavailable (for example Wasm).
/// </summary>
internal sealed class BufferedPageBackend : IFilePageBackend
{
    public void UpdatePath(string path)
    {
    }

    public bool TryAcquirePageHandle(long filePageIndex, long fileSize, out IPageHandle? handle)
    {
        handle = null;
        return false;
    }

    public void Truncate(long size)
    {
    }

    public void Dispose()
    {
    }
}
