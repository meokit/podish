using Fiberish.VFS;

namespace Fiberish.Memory;

public enum MemoryObjectKind
{
    Anonymous,
    File,
    Image
}

public sealed class MemoryObject
{
    private readonly Dictionary<uint, IntPtr> _pages = new();
    private readonly object _lock = new();
    private int _refCount = 1;

    public MemoryObject(MemoryObjectKind kind, LinuxFile? file, long fileBaseOffset, long fileSize, bool shared)
    {
        Kind = kind;
        File = file;
        FileBaseOffset = fileBaseOffset;
        FileSize = fileSize;
        IsShared = shared;
    }

    public MemoryObjectKind Kind { get; }
    public LinuxFile? File { get; }
    public long FileBaseOffset { get; }
    public long FileSize { get; }
    public bool IsShared { get; }

    public void AddRef()
    {
        lock (_lock)
        {
            _refCount++;
        }
    }

    /// <summary>
    /// Try to get an existing page without creating it.
    /// </summary>
    public IntPtr GetPage(uint pageIndex)
    {
        lock (_lock)
        {
            return _pages.TryGetValue(pageIndex, out var p) ? p : IntPtr.Zero;
        }
    }

    /// <summary>
    /// Used by COW: store a private page that was just allocated.
    /// </summary>
    internal void SetPage(uint pageIndex, IntPtr ptr)
    {
        lock (_lock)
        {
            _pages[pageIndex] = ptr;
        }
    }

    public void Release()
    {
        List<IntPtr>? toRelease = null;
        lock (_lock)
        {
            _refCount--;
            if (_refCount > 0) return;
            toRelease = _pages.Values.ToList();
            _pages.Clear();
        }

        foreach (var ptr in toRelease) ExternalPageManager.ReleasePtr(ptr);
    }

    public IntPtr GetOrCreatePage(uint pageIndex, Func<IntPtr, bool>? onFirstCreate, out bool isNew)
    {
        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var existing))
            {
                isNew = false;
                return existing;
            }
        }

        var ptr = ExternalPageManager.AllocateExternalPage();
        if (ptr == IntPtr.Zero)
        {
            isNew = false;
            return IntPtr.Zero;
        }

        if (onFirstCreate != null && !onFirstCreate(ptr))
        {
            ExternalPageManager.ReleasePtr(ptr);
            isNew = false;
            return IntPtr.Zero;
        }

        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var raced))
            {
                ExternalPageManager.ReleasePtr(ptr);
                isNew = false;
                return raced;
            }

            _pages[pageIndex] = ptr;
            isNew = true;
            return ptr;
        }
    }

    public MemoryObject ForkCloneForPrivate()
    {
        var clone = new MemoryObject(Kind, File, FileBaseOffset, FileSize, false);
        lock (_lock)
        {
            foreach (var (pageIndex, pagePtr) in _pages)
            {
                if (Kind == MemoryObjectKind.Anonymous)
                {
                    // Anonymous mappings lack CowObject, so they cannot rely on HandleFault COW.
                    // Do a deep copy here to maintain strict isolation for MAP_PRIVATE anon.
                    var newPage = ExternalPageManager.AllocateExternalPage();
                    if (newPage != IntPtr.Zero)
                    {
                        unsafe
                        {
                            Buffer.MemoryCopy((void*)pagePtr, (void*)newPage, 4096, 4096);
                        }

                        clone._pages[pageIndex] = newPage;
                    }
                }
                else
                {
                    // File-backed mappings rely on CowObject for copy-on-write
                    ExternalPageManager.AddRef(pagePtr);
                    clone._pages[pageIndex] = pagePtr;
                }
            }
        }

        return clone;
    }
}