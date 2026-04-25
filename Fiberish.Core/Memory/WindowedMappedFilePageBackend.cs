using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fiberish.Native;

namespace Fiberish.Memory;

internal sealed partial class WindowedMappedFilePageBackend(string path, HostMemoryMapGeometry geometry) : IFilePageBackend
{
    internal static Func<nint, long, bool>? FlushOverrideForTests { get; set; }

    private readonly Dictionary<long, long> _activeWindowIdsByStart = [];
    private SafeFileHandle? _cachedReadHandle;
    private SafeFileHandle? _cachedReadWriteHandle;
    private readonly Dictionary<long, int> _guestPageRefs = [];
    private readonly Dictionary<long, PageLease> _leases = [];
    private readonly Lock _lock = new();
    private readonly Dictionary<long, Window> _windowsById = [];
    private long _nextLeaseToken = 1;
    private long _nextWindowId = 1;
    private string _path = path;

    public void UpdatePath(string path)
    {
        List<WindowMapping> disposeNow;
        List<SafeFileHandle> disposeHandles;
        lock (_lock)
        {
            if (string.Equals(_path, path, StringComparison.Ordinal))
                return;
            _path = path;
            disposeNow = ResetWindowsLocked();
            disposeHandles = ResetCachedHandlesLocked();
        }

        DisposeMappings(disposeNow);
        DisposeHandles(disposeHandles);
    }

    public bool TryAcquirePageLease(long filePageIndex, long fileSize, bool writable, out IntPtr pointer,
        out long releaseToken)
    {
        Span<IntPtr> pointers = stackalloc IntPtr[1];
        Span<long> releaseTokens = stackalloc long[1];
        if (TryAcquirePageLeases(filePageIndex, 1, fileSize, writable, pointers, releaseTokens) != 1)
        {
            pointer = IntPtr.Zero;
            releaseToken = 0;
            return false;
        }

        pointer = pointers[0];
        releaseToken = releaseTokens[0];
        return true;
    }

    public int TryAcquirePageLeases(long startFilePageIndex, int maxPageCount, long fileSize, bool writable,
        Span<IntPtr> pointers, Span<long> releaseTokens)
    {
        if (maxPageCount <= 0 || pointers.Length < maxPageCount || releaseTokens.Length < maxPageCount)
            return 0;
        if (startFilePageIndex < 0 || fileSize <= 0)
            return 0;

        List<WindowMapping>? retiredMappings = null;
        var acquiredCount = 0;
        try
        {
            lock (_lock)
            {
                while (acquiredCount < maxPageCount)
                {
                    var runCount = TryAcquirePageLeaseRunLocked(
                        startFilePageIndex + acquiredCount,
                        maxPageCount - acquiredCount,
                        fileSize,
                        writable,
                        pointers.Slice(acquiredCount, maxPageCount - acquiredCount),
                        releaseTokens.Slice(acquiredCount, maxPageCount - acquiredCount),
                        ref retiredMappings);
                    if (runCount <= 0)
                        break;

                    acquiredCount += runCount;
                }
            }
        }
        finally
        {
            if (retiredMappings != null)
                DisposeMappings(retiredMappings);
            for (var i = acquiredCount; i < maxPageCount; i++)
            {
                pointers[i] = IntPtr.Zero;
                releaseTokens[i] = 0;
            }
        }

        return acquiredCount;
    }

    private int TryAcquirePageLeaseRunLocked(long startFilePageIndex, int maxPageCount, long fileSize, bool writable,
        Span<IntPtr> pointers, Span<long> releaseTokens, ref List<WindowMapping>? retiredMappings)
    {
        if (maxPageCount <= 0)
            return 0;

        var pageStart = checked(startFilePageIndex * LinuxConstants.PageSize);
        if (pageStart >= fileSize)
            return 0;

        var windowStart = AlignDown(pageStart, geometry.AllocationGranularity);
        var offsetInWindow = pageStart - windowStart;
        if (offsetInWindow < 0)
            return 0;

        var runCount = GetMaxLeaseRunPageCountLocked(pageStart, offsetInWindow, maxPageCount, fileSize);
        if (runCount <= 0)
            return 0;

        var requiredCoverage = checked(offsetInWindow + (long)runCount * LinuxConstants.PageSize);
        if (_activeWindowIdsByStart.TryGetValue(windowStart, out var activeWindowId))
        {
            if (TryAcquireRunFromActiveWindowLocked(activeWindowId, writable, requiredCoverage, out var windowPtr))
                return PopulateLeaseRunLocked(startFilePageIndex, runCount, activeWindowId, windowPtr, offsetInWindow,
                    pointers, releaseTokens);

            WindowMapping retiredMapping = default;
            RetireActiveWindowLocked(windowStart, activeWindowId, ref retiredMapping);
            if (retiredMapping.IsAllocated)
            {
                retiredMappings ??= [];
                retiredMappings.Add(retiredMapping);
            }
        }

        var created = CreateWindowLocked(windowStart, fileSize, writable, requiredCoverage);
        if (created == null)
            return 0;

        var window = created.Value;
        _activeWindowIdsByStart[windowStart] = window.Id;
        _windowsById.Add(window.Id, window);
        return PopulateLeaseRunLocked(startFilePageIndex, runCount, window.Id, window.Ptr, offsetInWindow, pointers,
            releaseTokens);
    }

    private int GetMaxLeaseRunPageCountLocked(long pageStart, long offsetInWindow, int maxPageCount, long fileSize)
    {
        if (maxPageCount <= 0)
            return 0;

        var fileRemaining = fileSize - pageStart;
        if (fileRemaining <= 0)
            return 0;

        long pagesByFile;
        if (geometry.SupportsDirectMappedTailPage)
            pagesByFile = (fileRemaining + LinuxConstants.PageSize - 1) / LinuxConstants.PageSize;
        else
            pagesByFile = fileRemaining / LinuxConstants.PageSize;
        if (pagesByFile <= 0)
            return 0;

        var windowRemaining = geometry.AllocationGranularity - offsetInWindow;
        if (windowRemaining < LinuxConstants.PageSize)
            return 0;

        var pagesByWindow = windowRemaining / LinuxConstants.PageSize;
        if (pagesByWindow <= 0)
            return 0;

        return (int)Math.Min((long)maxPageCount, Math.Min(pagesByFile, pagesByWindow));
    }

    private bool TryAcquireRunFromActiveWindowLocked(long windowId, bool writable, long requiredCoverage,
        out nint windowPtr)
    {
        windowPtr = 0;

        ref var window = ref GetWindowRef(windowId);
        if (Unsafe.IsNullRef(ref window) || window.Retired)
            return false;
        if (window.Length < requiredCoverage)
            return false;
        if (writable && window.AccessMode != WindowAccessMode.ReadWrite)
            return false;

        windowPtr = window.Ptr;
        return true;
    }

    private int PopulateLeaseRunLocked(long startFilePageIndex, int runCount, long windowId, nint windowPtr,
        long offsetInWindow, Span<IntPtr> pointers, Span<long> releaseTokens)
    {
        if (runCount <= 0)
            return 0;

        _leases.EnsureCapacity(_leases.Count + runCount);
        _guestPageRefs.EnsureCapacity(_guestPageRefs.Count + runCount);

        ref var window = ref GetWindowRef(windowId);
        if (Unsafe.IsNullRef(ref window) || window.Retired)
            return 0;

        window.RefCount += runCount;
        var basePtr = windowPtr + (nint)offsetInWindow;
        for (var i = 0; i < runCount; i++)
        {
            var pageIndex = startFilePageIndex + i;
            IncrementGuestPageRefLocked(pageIndex);
            pointers[i] = basePtr + i * LinuxConstants.PageSize;
            releaseTokens[i] = CreateLeaseLocked(pageIndex, windowId);
        }

        return runCount;
    }

    private bool TryAcquirePageLeaseLocked(long filePageIndex, long fileSize, bool writable, out IntPtr pointer,
        out long releaseToken, ref List<WindowMapping>? retiredMappings)
    {
        pointer = IntPtr.Zero;
        releaseToken = 0;

        var pageStart = checked(filePageIndex * LinuxConstants.PageSize);
        if (pageStart >= fileSize) return false;

        var isTailPage = checked(pageStart + LinuxConstants.PageSize) > fileSize;
        if (isTailPage && !geometry.SupportsDirectMappedTailPage)
            return false;

        var windowStart = AlignDown(pageStart, geometry.AllocationGranularity);
        var offsetInWindow = pageStart - windowStart;
        if (offsetInWindow < 0) return false;
        var requiredCoverage = checked(offsetInWindow + LinuxConstants.PageSize);

        if (_activeWindowIdsByStart.TryGetValue(windowStart, out var activeWindowId))
        {
            if (TryAcquireFromActiveWindowLocked(activeWindowId, filePageIndex, writable, requiredCoverage,
                    offsetInWindow, out pointer, out releaseToken))
                return true;

            WindowMapping retiredMapping = default;
            RetireActiveWindowLocked(windowStart, activeWindowId, ref retiredMapping);
            if (retiredMapping.IsAllocated)
            {
                retiredMappings ??= [];
                retiredMappings.Add(retiredMapping);
            }
        }

        var created = CreateWindowLocked(windowStart, fileSize, writable, requiredCoverage);
        if (created == null)
            return false;

        var window = created.Value;
        window.RefCount = 1;
        _activeWindowIdsByStart[windowStart] = window.Id;
        _windowsById.Add(window.Id, window);
        IncrementGuestPageRefLocked(filePageIndex);
        pointer = window.Ptr + (nint)offsetInWindow;
        releaseToken = CreateLeaseLocked(filePageIndex, window.Id);
        return true;
    }

    public void ReleasePageLease(long releaseToken)
    {
        if (releaseToken == 0)
            return;

        WindowMapping disposeNow = default;
        List<SafeFileHandle>? disposeHandles = null;
        lock (_lock)
        {
            if (!_leases.Remove(releaseToken, out var lease))
                return;

            DecrementGuestPageRefLocked(lease.PageIndex);
            ReleaseWindowLeaseLocked(lease.WindowId, ref disposeNow);
            if (!HasActiveWindowsLocked())
                disposeHandles = ResetCachedHandlesLocked();
        }

        Dispose(ref disposeNow);
        DisposeHandles(disposeHandles);
    }

    public bool TryFlushPage(long filePageIndex)
    {
        if (filePageIndex < 0) return false;

        var pageStart = checked(filePageIndex * LinuxConstants.PageSize);
        var windowStart = AlignDown(pageStart, geometry.AllocationGranularity);
        var offsetInWindow = pageStart - windowStart;
        if (offsetInWindow < 0) return false;
        lock (_lock)
        {
            if (!_activeWindowIdsByStart.TryGetValue(windowStart, out var windowId))
                return false;

            ref var window = ref GetWindowRef(windowId);
            if (Unsafe.IsNullRef(ref window) || window.Retired)
                return false;

            if (window.AccessMode == WindowAccessMode.ReadOnly)
                return true;

            if (!TryGetPageFlushRange(window.Mapping.FlushLength, offsetInWindow, geometry.HostPageSize, out var flushOffset,
                    out var flushLength))
                return false;

            return Flush(window.Mapping.RawPtr + (nint)flushOffset, flushLength);
        }
    }

    public bool TryFlushAllActiveWritableWindows()
    {
        lock (_lock)
        {
            foreach (var windowId in _activeWindowIdsByStart.Values)
            {
                ref var window = ref GetWindowRef(windowId);
                if (Unsafe.IsNullRef(ref window) || window.Retired)
                    continue;
                if (window.AccessMode == WindowAccessMode.ReadOnly)
                    continue;
                if (window.Mapping.RawPtr == 0 || window.Mapping.FlushLength <= 0)
                    continue;
                if (!Flush(window.Mapping.RawPtr, window.Mapping.FlushLength))
                    return false;
            }
        }

        return true;
    }

    public void Truncate(long size)
    {
        _ = size;
        List<WindowMapping> disposeNow;
        List<SafeFileHandle> disposeHandles;
        lock (_lock)
        {
            disposeNow = ResetWindowsLocked();
            disposeHandles = ResetCachedHandlesLocked();
        }

        DisposeMappings(disposeNow);
        DisposeHandles(disposeHandles);
    }

    public long Trim(bool aggressive)
    {
        List<WindowMapping> disposeNow;
        List<SafeFileHandle>? disposeHandles = null;
        lock (_lock)
        {
            disposeNow = TrimWindowsLocked(aggressive);
            if (!HasActiveWindowsLocked())
                disposeHandles = ResetCachedHandlesLocked();
        }

        long reclaimedBytes = 0;
        foreach (var mapping in disposeNow)
        {
            reclaimedBytes += mapping.Length;
            var detached = mapping;
            Dispose(ref detached);
        }

        DisposeHandles(disposeHandles);
        return reclaimedBytes;
    }

    public FilePageBackendDiagnostics GetDiagnostics()
    {
        lock (_lock)
        {
            long windowBytes = 0;
            var windowCount = 0;
            foreach (var windowId in _activeWindowIdsByStart.Values)
            {
                ref var window = ref GetWindowRef(windowId);
                if (Unsafe.IsNullRef(ref window) || window.Retired)
                    continue;

                windowCount++;
                windowBytes += window.Length;
            }

            return new FilePageBackendDiagnostics(windowCount, windowBytes, _guestPageRefs.Count);
        }
    }

    public void Dispose()
    {
        List<WindowMapping> disposeNow;
        List<SafeFileHandle> disposeHandles;
        lock (_lock)
        {
            disposeNow = ResetWindowsLocked();
            disposeHandles = ResetCachedHandlesLocked();
        }

        DisposeMappings(disposeNow);
        DisposeHandles(disposeHandles);
    }

    private bool TryAcquireFromActiveWindowLocked(long windowId, long filePageIndex, bool writable,
        long requiredCoverage,
        long offsetInWindow, out IntPtr pointer, out long releaseToken)
    {
        pointer = IntPtr.Zero;
        releaseToken = 0;

        ref var window = ref GetWindowRef(windowId);
        if (Unsafe.IsNullRef(ref window) || window.Retired)
            return false;
        if (window.Length < requiredCoverage)
            return false;
        if (writable && window.AccessMode != WindowAccessMode.ReadWrite)
            return false;

        window.RefCount++;
        IncrementGuestPageRefLocked(filePageIndex);
        pointer = window.Ptr + (nint)offsetInWindow;
        releaseToken = CreateLeaseLocked(filePageIndex, window.Id);
        return true;
    }

    private Window? CreateWindowLocked(long windowStart, long fileSize, bool writable, long requiredCoverage)
    {
        var remaining = fileSize - windowStart;
        if (remaining <= 0) return null;

        var windowLength = Math.Min(geometry.AllocationGranularity, Math.Max(remaining, requiredCoverage));
        if (windowLength < requiredCoverage)
            return null;

        var mappedLength = Math.Min(windowLength, remaining);
        var mapping = CreateNativeWindow(windowStart, mappedLength, windowLength, writable);
        if (mapping == null)
            return null;

        return new Window
        {
            Id = _nextWindowId++,
            Start = windowStart,
            Mapping = mapping.Value,
            AccessMode = writable ? WindowAccessMode.ReadWrite : WindowAccessMode.ReadOnly,
            RefCount = 0,
            Retired = false
        };
    }

    private WindowMapping? CreateNativeWindow(long windowStart, long mappedLength, long logicalLength, bool writable)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? CreateWindowsWindow(windowStart, mappedLength, logicalLength, writable)
                : CreateUnixWindow(windowStart, mappedLength, logicalLength, writable);
        }
        catch
        {
            return null;
        }
    }

    private WindowMapping? CreateUnixWindow(long windowStart, long mappedLength, long logicalLength, bool writable)
    {
        if (mappedLength <= 0)
            return null;

        var handle = GetOrOpenCachedHandleLocked(writable);
        if (handle == null)
            return null;
        var prot = writable ? ProtRead | ProtWrite : ProtRead;
        var mapped = mmap(
            0,
            checked((nuint)mappedLength),
            prot,
            MapShared,
            handle.DangerousGetHandle().ToInt32(),
            (nint)windowStart);
        if (mapped == MmapFailed)
            return null;

        return new WindowMapping
        {
            RawPtr = mapped,
            Ptr = mapped,
            Length = logicalLength,
            FlushLength = mappedLength,
            MappedLength = mappedLength
        };
    }

    private WindowMapping? CreateWindowsWindow(long windowStart, long mappedLength, long logicalLength,
        bool writable)
    {
        if (mappedLength <= 0)
            return null;

        var handle = GetOrOpenCachedHandleLocked(writable);
        if (handle == null)
            return null;

        var protect = writable ? PageReadWrite : PageReadOnly;
        var mappingHandle = CreateFileMapping(handle.DangerousGetHandle(), 0, protect, 0, 0, null);
        if (mappingHandle == 0)
            return null;

        try
        {
            SplitUInt64(windowStart, out var offsetHigh, out var offsetLow);
            var desiredAccess = writable ? FileMapWrite : FileMapRead;
            var mapped = MapViewOfFile(
                mappingHandle,
                desiredAccess,
                offsetHigh,
                offsetLow,
                checked((nuint)mappedLength));
            if (mapped == 0)
                return null;

            return new WindowMapping
            {
                RawPtr = mapped,
                Ptr = mapped,
                Length = logicalLength,
                FlushLength = mappedLength,
                MappedLength = mappedLength
            };
        }
        finally
        {
            CloseHandle(mappingHandle);
        }
    }

    private SafeFileHandle? GetOrOpenCachedHandleLocked(bool writable)
    {
        if (writable)
        {
            if (IsUsableHandle(_cachedReadWriteHandle))
                return _cachedReadWriteHandle;

            return _cachedReadWriteHandle = File.OpenHandle(
                _path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
        }

        if (IsUsableHandle(_cachedReadHandle))
            return _cachedReadHandle;
        if (IsUsableHandle(_cachedReadWriteHandle))
            return _cachedReadWriteHandle;

        return _cachedReadHandle = File.OpenHandle(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
    }

    private void RetireActiveWindowLocked(long windowStart, long windowId, ref WindowMapping disposeNow)
    {
        _activeWindowIdsByStart.Remove(windowStart);
        RetireWindowLocked(windowId, ref disposeNow);
    }

    private void RetireWindowLocked(long windowId, ref WindowMapping disposeNow)
    {
        var shouldRemove = false;
        {
            ref var window = ref GetWindowRef(windowId);
            if (Unsafe.IsNullRef(ref window))
                return;

            window.Retired = true;
            if (window.RefCount != 0)
                return;

            disposeNow = DetachWindowMapping(ref window);
            shouldRemove = true;
        }

        if (shouldRemove)
            _windowsById.Remove(windowId);
    }

    private void ReleaseWindowLeaseLocked(long windowId, ref WindowMapping disposeNow)
    {
        var shouldRemove = false;
        {
            ref var window = ref GetWindowRef(windowId);
            if (Unsafe.IsNullRef(ref window))
                return;

            if (window.RefCount > 0)
                window.RefCount--;
            if (window.RefCount != 0 || !window.Retired)
                return;

            disposeNow = DetachWindowMapping(ref window);
            shouldRemove = true;
        }

        if (shouldRemove)
            _windowsById.Remove(windowId);
    }

    private List<WindowMapping> ResetWindowsLocked()
    {
        var disposeNow = new List<WindowMapping>(_activeWindowIdsByStart.Count);
        var activeWindows = new List<KeyValuePair<long, long>>(_activeWindowIdsByStart);
        _activeWindowIdsByStart.Clear();
        foreach (var (_, windowId) in activeWindows)
        {
            WindowMapping mapping = default;
            RetireWindowLocked(windowId, ref mapping);
            if (mapping.IsAllocated)
                disposeNow.Add(mapping);
        }

        return disposeNow;
    }

    private List<WindowMapping> TrimWindowsLocked(bool aggressive)
    {
        var disposeNow = new List<WindowMapping>();
        var startsToRetire = new List<KeyValuePair<long, long>>();
        foreach (var pair in _activeWindowIdsByStart)
        {
            ref var window = ref GetWindowRef(pair.Value);
            if (Unsafe.IsNullRef(ref window) || window.Retired)
            {
                startsToRetire.Add(pair);
                continue;
            }

            if (!aggressive && window.RefCount > 0)
                continue;

            startsToRetire.Add(pair);
        }

        foreach (var (start, windowId) in startsToRetire)
        {
            _activeWindowIdsByStart.Remove(start);
            WindowMapping mapping = default;
            RetireWindowLocked(windowId, ref mapping);
            if (mapping.IsAllocated)
                disposeNow.Add(mapping);
        }

        return disposeNow;
    }

    private bool HasActiveWindowsLocked()
    {
        foreach (var windowId in _activeWindowIdsByStart.Values)
        {
            ref var window = ref GetWindowRef(windowId);
            if (Unsafe.IsNullRef(ref window) || window.Retired)
                continue;
            return true;
        }

        return false;
    }

    private List<SafeFileHandle> ResetCachedHandlesLocked()
    {
        var disposeHandles = new List<SafeFileHandle>(2);
        if (IsUsableHandle(_cachedReadHandle))
            disposeHandles.Add(_cachedReadHandle!);
        if (IsUsableHandle(_cachedReadWriteHandle) &&
            !ReferenceEquals(_cachedReadWriteHandle, _cachedReadHandle))
            disposeHandles.Add(_cachedReadWriteHandle!);
        _cachedReadHandle = null;
        _cachedReadWriteHandle = null;
        return disposeHandles;
    }

    private void IncrementGuestPageRefLocked(long filePageIndex)
    {
        _guestPageRefs.TryGetValue(filePageIndex, out var count);
        _guestPageRefs[filePageIndex] = count + 1;
    }

    private void DecrementGuestPageRefLocked(long filePageIndex)
    {
        if (!_guestPageRefs.TryGetValue(filePageIndex, out var count))
            return;
        if (count <= 1)
            _guestPageRefs.Remove(filePageIndex);
        else
            _guestPageRefs[filePageIndex] = count - 1;
    }

    private ref Window GetWindowRef(long windowId)
    {
        return ref CollectionsMarshal.GetValueRefOrNullRef(_windowsById, windowId);
    }

    private static WindowMapping DetachWindowMapping(ref Window window)
    {
        var mapping = window.Mapping;
        window.Mapping = default;
        return mapping;
    }

    internal static bool TryGetPageFlushRange(long mappedLength, long offsetInWindow, int hostPageSize,
        out long flushOffset, out long flushLength)
    {
        flushOffset = 0;
        flushLength = 0;

        if (offsetInWindow < 0 || offsetInWindow >= mappedLength)
            return false;

        var alignment = Math.Max(1, hostPageSize);
        var pageEndExclusive = Math.Min(mappedLength, checked(offsetInWindow + LinuxConstants.PageSize));
        var alignedOffset = AlignDown(offsetInWindow, alignment);
        if (pageEndExclusive <= alignedOffset)
            return false;

        flushOffset = alignedOffset;
        flushLength = pageEndExclusive - alignedOffset;
        return flushLength > 0;
    }

    private static bool Flush(nint rawPtr, long flushLength)
    {
        try
        {
            if (rawPtr == 0 || flushLength <= 0)
                return false;

            if (FlushOverrideForTests is { } hook)
                return hook(rawPtr, flushLength);

            return OperatingSystem.IsWindows()
                ? FlushViewOfFile(rawPtr, checked((nuint)flushLength))
                : msync(rawPtr, checked((nuint)flushLength), GetMsSyncFlag()) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Dispose(ref WindowMapping mapping)
    {
        var rawPtr = mapping.RawPtr;
        var mappedLength = mapping.MappedLength;
        mapping = default;

        if (rawPtr == 0)
            return;

        if (OperatingSystem.IsWindows())
        {
            UnmapViewOfFile(rawPtr);
            return;
        }

        munmap(rawPtr, checked((nuint)mappedLength));
    }

    private static void DisposeMappings(IEnumerable<WindowMapping> mappings)
    {
        foreach (var mapping in mappings)
        {
            var detached = mapping;
            Dispose(ref detached);
        }
    }

    private static void DisposeHandles(IEnumerable<SafeFileHandle>? handles)
    {
        if (handles == null)
            return;

        foreach (var handle in handles)
            handle.Dispose();
    }

    private long CreateLeaseLocked(long pageIndex, long windowId)
    {
        var releaseToken = _nextLeaseToken++;
        _leases.Add(releaseToken, new PageLease(pageIndex, windowId));
        return releaseToken;
    }

    private static long AlignDown(long value, int alignment)
    {
        if (alignment <= 0) return value;
        return value / alignment * alignment;
    }

    private static int GetMsSyncFlag()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() ||
            OperatingSystem.IsTvOS() || OperatingSystem.IsWatchOS())
            return 0x0010;
        return 0x0004;
    }

    private static bool IsUsableHandle(SafeFileHandle? handle)
    {
        return handle is { IsInvalid: false, IsClosed: false };
    }

    private static void SplitUInt64(long value, out uint high, out uint low)
    {
        var raw = unchecked((ulong)value);
        high = (uint)(raw >> 32);
        low = (uint)raw;
    }

    [LibraryImport("libc", EntryPoint = "msync")]
    private static partial int msync(nint addr, nuint length, int flags);

    [LibraryImport("libc", EntryPoint = "mmap", SetLastError = true)]
    private static partial nint mmap(nint addr, nuint length, int prot, int flags, int fd, nint offset);

    [LibraryImport("libc", EntryPoint = "munmap", SetLastError = true)]
    private static partial int munmap(nint addr, nuint length);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileMappingW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileMapping(
        nint hFile,
        nint lpFileMappingAttributes,
        uint flProtect,
        uint dwMaximumSizeHigh,
        uint dwMaximumSizeLow,
        string? lpName);

    [LibraryImport("kernel32.dll", EntryPoint = "MapViewOfFile", SetLastError = true)]
    private static partial nint MapViewOfFile(
        nint hFileMappingObject,
        uint dwDesiredAccess,
        uint dwFileOffsetHigh,
        uint dwFileOffsetLow,
        nuint dwNumberOfBytesToMap);

    [LibraryImport("kernel32.dll", EntryPoint = "FlushViewOfFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushViewOfFile(nint lpBaseAddress, nuint dwNumberOfBytesToFlush);

    [LibraryImport("kernel32.dll", EntryPoint = "UnmapViewOfFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(nint lpBaseAddress);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    private static readonly nint MmapFailed = new(-1);
    private const int ProtRead = 0x1;
    private const int ProtWrite = 0x2;
    private const int MapShared = 0x01;
    private const uint PageReadOnly = 0x02;
    private const uint PageReadWrite = 0x04;
    private const uint FileMapWrite = 0x0002;
    private const uint FileMapRead = 0x0004;

    private enum WindowAccessMode
    {
        ReadOnly,
        ReadWrite
    }

    private struct WindowMapping
    {
        public nint RawPtr;
        public nint Ptr;
        public long Length;
        public long FlushLength;
        public long MappedLength;

        public readonly bool IsAllocated => Ptr != 0;
    }

    private struct Window
    {
        public long Id;
        public long Start;
        public WindowMapping Mapping;
        public WindowAccessMode AccessMode;
        public int RefCount;
        public bool Retired;

        public readonly long Length => Mapping.Length;
        public readonly nint Ptr => Mapping.Ptr;
    }

    private readonly record struct PageLease(long PageIndex, long WindowId);
}
