namespace Fiberish.Memory;

public sealed class MemoryObjectManager
{
    private readonly Dictionary<string, MemoryObject> _namedObjects = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public static MemoryObjectManager Instance { get; } = new();

    public MemoryObject CreateAnonymous(bool shared)
    {
        return new MemoryObject(MemoryObjectKind.Anonymous, null, 0, 0, shared);
    }

    public MemoryObject CreateFile(Fiberish.VFS.LinuxFile fileHandle, long fileBaseOffset, long fileSize, bool shared)
    {
        return new MemoryObject(MemoryObjectKind.File, fileHandle, fileBaseOffset, fileSize, shared);
    }

    public MemoryObject CreateOrOpenNamed(string name, Func<MemoryObject> factory, out bool created)
    {
        lock (_lock)
        {
            if (_namedObjects.TryGetValue(name, out var existing))
            {
                existing.AddRef();
                created = false;
                return existing;
            }

            var obj = factory();
            _namedObjects[name] = obj;
            created = true;
            return obj;
        }
    }

    public void CloseNamed(string name)
    {
        lock (_lock)
        {
            if (!_namedObjects.TryGetValue(name, out var obj)) return;
            _namedObjects.Remove(name);
            obj.Release();
        }
    }
}
