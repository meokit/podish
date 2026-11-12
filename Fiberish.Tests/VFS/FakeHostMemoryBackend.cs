using System.Runtime.InteropServices;
using Fiberish.Memory;
using Fiberish.Native;

namespace Fiberish.Tests.VFS;

internal sealed class FakeHostMemoryBackend : IFilePageBackend
{
    private readonly byte[] _memory;
    private readonly GCHandle _handle;
    private readonly IntPtr _baseAddress;
    private readonly Lock _lock = new();
    private readonly Dictionary<long, long> _leases = [];
    private long _nextLease = 1;
    private bool _disposed;

    public FakeHostMemoryBackend(int capacityPages = 64)
    {
        _memory = new byte[capacityPages * LinuxConstants.PageSize];
        _handle = GCHandle.Alloc(_memory, GCHandleType.Pinned);
        _baseAddress = _handle.AddrOfPinnedObject();
    }

    public bool FailLeaseAcquisition { get; set; }
    public bool FailLock { get; set; }
    public bool FailPrefault { get; set; }
    public HostPageResidencyStatus ConfiguredResidency { get; set; } = HostPageResidencyStatus.Resident;
    public int ActiveLeaseCount { get { lock (_lock) return _leases.Count; } }
    public int TotalLockedPages { get; private set; }

    public void UpdatePath(string path)
    {
    }

    public int TryAcquirePageLeases(long startFilePageIndex, int maxPageCount, long fileSize, bool writable,
        Span<IntPtr> pointers, Span<long> releaseTokens)
    {
        if (FailLeaseAcquisition || _disposed)
            return 0;

        var count = Math.Min(maxPageCount, pointers.Length);
        for (var i = 0; i < count; i++)
        {
            if (!TryAcquirePageLease(startFilePageIndex + i, fileSize, writable, out pointers[i], out releaseTokens[i]))
                return i;
        }
        return count;
    }

    public bool TryAcquirePageLease(long filePageIndex, long fileSize, bool writable, out IntPtr pointer,
        out long releaseToken)
    {
        pointer = IntPtr.Zero;
        releaseToken = 0;
        if (FailLeaseAcquisition || _disposed || filePageIndex < 0)
            return false;

        var offset = filePageIndex * LinuxConstants.PageSize;
        if (offset >= fileSize || offset + LinuxConstants.PageSize > _memory.Length)
            return false;

        lock (_lock)
        {
            pointer = _baseAddress + (nint)offset;
            releaseToken = _nextLease++;
            _leases[releaseToken] = filePageIndex;
            return true;
        }
    }

    public void ReleasePageLease(long releaseToken)
    {
        lock (_lock)
        {
            _leases.Remove(releaseToken);
        }
    }

    public bool TryFlushPage(long filePageIndex) => true;

    public bool TryFlushAllActiveWritableWindows() => true;

    public void Truncate(long size)
    {
    }

    public long Trim(bool aggressive) => 0;

    public FilePageBackendDiagnostics GetDiagnostics()
    {
        lock (_lock)
        {
            return new FilePageBackendDiagnostics(1, _memory.Length, _leases.Count);
        }
    }

    public bool TryDescribePagePointer(long filePageIndex, IntPtr pointer, out FilePageLeaseDiagnostics diagnostics)
    {
        diagnostics = default;
        if (_disposed || filePageIndex < 0) return false;
        var offset = filePageIndex * LinuxConstants.PageSize;
        if (_baseAddress + (nint)offset != pointer) return false;

        lock (_lock)
        {
            diagnostics = new FilePageLeaseDiagnostics(1, filePageIndex, _baseAddress, _memory.Length, true, false, _leases.Count);
            return true;
        }
    }

    public HostPageResidencyStatus CheckPageResidency(long filePageIndex)
    {
        return ConfiguredResidency;
    }

    public bool TryPrefaultPage(long filePageIndex, bool writable)
    {
        if (FailPrefault || _disposed) return false;
        return true;
    }

    public bool TryLockPageRange(long startFilePageIndex, int pageCount, out int lockedPages)
    {
        lockedPages = 0;
        if (FailLock || _disposed) return false;
        lockedPages = pageCount;
        TotalLockedPages += pageCount;
        return true;
    }

    public bool TryUnlockPageRange(long startFilePageIndex, int pageCount)
    {
        TotalLockedPages = Math.Max(0, TotalLockedPages - pageCount);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle.IsAllocated)
            _handle.Free();
    }
}
