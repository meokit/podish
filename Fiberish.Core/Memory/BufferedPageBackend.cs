namespace Fiberish.Memory;

/// <summary>
///     No-op backend used when host memory-mapped files are unavailable (for example Wasm).
/// </summary>
internal sealed class BufferedPageBackend : IFilePageBackend
{
    public void UpdatePath(string path)
    {
    }

    public int TryAcquirePageLeases(long startFilePageIndex, int maxPageCount, long fileSize, bool writable,
        Span<IntPtr> pointers, Span<long> releaseTokens)
    {
        _ = startFilePageIndex;
        _ = maxPageCount;
        _ = fileSize;
        _ = writable;
        _ = pointers;
        _ = releaseTokens;
        return 0;
    }

    public bool TryAcquirePageLease(long filePageIndex, long fileSize, bool writable, out IntPtr pointer,
        out long releaseToken)
    {
        pointer = IntPtr.Zero;
        releaseToken = 0;
        return false;
    }

    public void ReleasePageLease(long releaseToken)
    {
        _ = releaseToken;
    }

    public bool TryFlushPage(long filePageIndex)
    {
        return false;
    }

    public bool TryFlushAllActiveWritableWindows()
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
